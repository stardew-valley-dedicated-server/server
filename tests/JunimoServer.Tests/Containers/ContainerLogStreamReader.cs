using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Streams a container's stdout+stderr via the daemon's <c>follow=true</c> log
/// endpoint and forwards each fully-formed line to <c>onLine</c>.
/// One persistent <see cref="MultiplexedStream"/> per container replaces the
/// 500 ms <c>GetLogsAsync</c> poll loops the per-container types used to run.
///
/// Used by <see cref="ServerContainer"/>, <see cref="GameClientContainer"/>,
/// and <see cref="SharedSteamAuth"/>; the per-line callback owns site-specific
/// behaviour (SDVD_EVENT forwarding, SMAPI error detection, UI startup
/// surfacing, file-sink writes).
///
/// <para>Reconnect cursor: when <c>Timestamps=true</c> the daemon prefixes
/// each line with an RFC3339Nano timestamp. The reader parses the prefix to
/// advance <see cref="_sinceCursor"/>, then strips it before invoking
/// <c>onLine</c>. On reconnect the cursor is passed back via
/// <see cref="ContainerLogsParameters.Since"/> so the daemon resumes
/// immediately after the last emitted line — no double-emit, no replay
/// window.</para>
/// </summary>
internal sealed class ContainerLogStreamReader : IAsyncDisposable
{
    public delegate void LineHandler(string strippedLine);

    // Same size as ForwardHealingHandler's heal budget: covers a master
    // respawn plus forward restore without holding a reader whose host is
    // gone for a whole test.
    private static readonly TimeSpan DefaultReconnectBudget = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(1);

    private const string EndReasonContainerExited = "container_exited";
    private const string EndReasonOpenFailuresExhausted = "open_failures_exhausted";
    private const string EndReasonCancelled = "cancelled";
    private const string EndReasonDockerDown = "docker_down";
    private const string EndReasonLineHandlerFaulted = "line_handler_faulted";

    private const int ReadBufferSize = 8 * 1024;

    // Matches "2024-01-01T12:34:56.123456789Z " emitted by the Docker daemon
    // when Timestamps=true. The fractional component is optional (some daemons
    // emit whole-second timestamps under load); the trailing space is the
    // separator between the timestamp and the line content.
    private static readonly Regex DaemonTimestampPrefix = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)\s",
        RegexOptions.Compiled
    );

    // Matches jlesage/baseimage-gui logmonitor process tag: "[app         ] "
    // or "[init        ] ". The logmonitor pads process names to 12 characters
    // inside brackets.
    private static readonly Regex LogmonitorProcessTag = new(
        @"^\[\w+\s*\]\s*",
        RegexOptions.Compiled
    );

    private readonly DockerClient _client;
    private readonly IContainer _container;
    private readonly string _diagnosticLabel;
    private readonly string _hostId;
    private readonly LineHandler _onLine;
    private readonly Action<string>? _diagnosticCallback;

    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;
    private string? _sinceCursor;
    private long _linesEmitted;
    private int _reconnects;

    // Silent-gap measurement: a chunk arriving more than GapThreshold after the
    // previous one emits container_log_stream_gap with the gap's bounds. A
    // quiet container produces the same shape as a stalled transport; the
    // discriminator is every stream on a host gapping at the same instant, so
    // the reader records and the reader of the artifact correlates.
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(10);
    private DateTime _lastChunkAtUtc;

    // The last raw (pre-strip) line emitted, including the timestamp prefix.
    // Used to dedup a single line of overlap on reconnect: Docker's `Since`
    // filter is inclusive, so a follow=true reopen with Since = lastTimestamp
    // replays the line that produced that timestamp. We skip the replay if
    // it matches byte-for-byte. Multiple lines with the same timestamp are
    // not deduped — they re-emit, accepted as the cost of avoiding content
    // hashing on every line. Reconnects follow a transport loss only.
    private string? _lastRawEmitted;
    private bool _expectDedupNextEmit;

    // Last transport-state read error emitted, so a persistently unreadable
    // file during an outage doesn't emit one event per read. Cleared on a
    // successful read.
    private string? _lastUnreadableError;

    /// <param name="client">
    /// Host-scoped Docker client (per <c>docker-test-resources.md</c> — every
    /// per-host consumer must go through <c>host.ApiClient</c>).
    /// </param>
    /// <param name="container">
    /// The Testcontainers <see cref="IContainer"/> whose log stream to follow.
    /// <see cref="IContainer.Id"/> may throw <see cref="InvalidOperationException"/>
    /// when read before <see cref="IContainer.StartAsync"/> has assigned a
    /// daemon-side ID — call sites typically launch the reader before
    /// <c>StartAsync</c>, so the read loop catches that and retries on the
    /// container-not-yet-ready path.
    /// </param>
    /// <param name="diagnosticLabel">
    /// Short label used in diagnostic messages forwarded via
    /// <paramref name="diagnosticCallback"/> (e.g. <c>"server-0"</c>,
    /// <c>"client-2"</c>, <c>"steam-auth-shared"</c>).
    /// </param>
    /// <param name="hostId">Docker host the container runs on; stamped as <c>host_id</c> on emitted events.</param>
    /// <param name="onLine">
    /// Per-line callback. Called for each non-empty line after the daemon
    /// timestamp prefix and the logmonitor process tag have been stripped.
    /// </param>
    /// <param name="diagnosticCallback">
    /// Optional sink for human-readable status messages. Off-band from
    /// <c>onLine</c>; the on-disk <c>container_log_stream_*</c> events are the
    /// source of truth for why the reader reconnected or stopped.
    /// </param>
    public ContainerLogStreamReader(
        DockerClient client,
        IContainer container,
        string diagnosticLabel,
        string hostId,
        LineHandler onLine,
        Action<string>? diagnosticCallback = null
    )
    {
        _client = client;
        _container = container;
        _diagnosticLabel = diagnosticLabel;
        _hostId = hostId;
        _onLine = onLine;
        _diagnosticCallback = diagnosticCallback;
    }

    /// <summary>
    /// Starts the streaming loop on the current task scheduler. Returns the
    /// running task; the caller typically discards it and calls
    /// <see cref="DrainAsync"/> + <see cref="DisposeAsync"/> on shutdown.
    /// Idempotent; subsequent calls return the same task.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        if (_runTask != null)
        {
            return _runTask;
        }

        _runTask = RunWithLinkedCtsAsync(ct);
        return _runTask;
    }

    private async Task RunWithLinkedCtsAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        await RunInternalAsync(linked.Token);
    }

    /// <summary>
    /// Cancels the read loop and awaits its completion up to
    /// <paramref name="timeout"/>, so any fully-formed lines already split out
    /// of the in-flight chunk are flushed through <c>onLine</c>
    /// (and into the per-site sink) before the consumer's
    /// <see cref="IAsyncDisposable.DisposeAsync"/> closes that sink.
    /// Per <c>drain-before-consume-disposal.md</c> — call this before
    /// disposing the consumer (e.g. <c>ContainerLogFile</c>).
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException) { }
        if (_runTask == null)
        {
            return;
        }

        await Task.WhenAny(_runTask, Task.Delay(timeout));
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException) { }
        try
        {
            _cts.Dispose();
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    private async Task RunInternalAsync(CancellationToken ct)
    {
        var hasReadAny = false;
        // Set when a post-data open fails or a clean EOF is not confirmed as
        // container exit; cleared by the next successful open. Anchors the
        // time-based reconnect budget and the reconnected event's gap.
        DateTime? outageStartUtc = null;
        var outageOpenFailures = 0;
        // Incident that governed the current outage's reconnect deadline,
        // resolved once at each budget decision. Reconnect/ended events report
        // this instead of re-resolving at emit time, so a transport-state file
        // rewritten mid-outage can't make the event name a different incident
        // than the one whose window set the deadline.
        string? outageIncidentId = null;
        Exception? lastFault = null;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                EmitEnded(EndReasonCancelled, "token", lastFault, outageStartUtc, outageIncidentId);
                return;
            }

            MultiplexedStream? stream = null;
            try
            {
                var parameters = new ContainerLogsParameters
                {
                    Follow = true,
                    ShowStdout = true,
                    ShowStderr = true,
                    Timestamps = true,
                    Since = _sinceCursor,
                };

                stream = await _client.Containers.GetContainerLogsAsync(
                    _container.Id,
                    parameters,
                    ct
                );

                // Reconnecting with a non-null cursor: Docker's Since filter
                // is inclusive, so the first line it returns is the one
                // produced at exactly _sinceCursor — i.e. a replay of the
                // last line we already emitted. Arm the dedup so the first
                // matching emit is skipped.
                _expectDedupNextEmit = _sinceCursor != null;

                // Pump until EOF, cancellation, or transient failure. The
                // first successful read flips hasReadAny so future opens
                // are treated as reconnects (cursor advances) rather than
                // initial container-not-yet-created retries. Reconnect is
                // emitted here, on the first byte actually read — NOT on a
                // successful open — because a reopen that immediately EOFs on
                // a running container is not a recovery; clearing the outage
                // there would reset the budget every cycle and loop forever.
                await PumpAsync(
                    stream,
                    onFirstRead: () =>
                    {
                        hasReadAny = true;
                        if (outageStartUtc is { } outageStart)
                        {
                            EmitReconnected(outageStart, outageOpenFailures, outageIncidentId);
                            outageStartUtc = null;
                            outageOpenFailures = 0;
                            outageIncidentId = null;
                            lastFault = null;
                        }
                    },
                    ct
                );

                // PumpAsync returned cleanly. Two cases:
                //
                // 1. hasReadAny == false: daemon returned an immediate empty
                //    body and closed. This happens when the container is in
                //    the *created* state — the call sites start the stream
                //    BEFORE container.StartAsync, and Testcontainers' Id
                //    getter unblocks as soon as the daemon assigns an ID
                //    (during create), which is before the container is
                //    actually running. Retry like the !hasReadAny exception
                //    path: the daemon will return logs once the container
                //    transitions to running. Without this, the very first
                //    Tty=false open after container creation would terminate
                //    the reader before any logs flow.
                //
                // 2. hasReadAny == true: either the container exited under
                //    follow=true, or the transport under the stream died —
                //    a killed ssh master EOFs the stream exactly the same
                //    way. Only the daemon can tell them apart, so inspect.
                //    The inspect goes through the same daemon-socket forward
                //    the stream used, so an inspect *failure* is itself a
                //    transport loss and never counts as "container exited".
                if (hasReadAny)
                {
                    var verdict = await InspectAfterEofAsync(ct);
                    if (verdict.Exited)
                    {
                        EmitEnded(
                            EndReasonContainerExited,
                            verdict.Detail,
                            lastFault: null,
                            outageStartUtc,
                            outageIncidentId
                        );
                        return;
                    }

                    if (verdict.Fault is { } inspectFault)
                    {
                        // Same handling as a failed open: budget, docker_down
                        // classification, retry delay.
                        throw new EofUnconfirmedException(verdict.Detail, inspectFault);
                    }

                    // A running container that keeps EOF-ing without delivering
                    // data is still an outage: accumulate against the same budget
                    // the exception path uses, or the reader would reconnect
                    // forever. onFirstRead clears this once real data flows again.
                    var now = DateTime.UtcNow;
                    outageStartUtc ??= now;
                    outageOpenFailures++;
                    var budget = ResolveBudget(outageStartUtc.Value);
                    outageIncidentId = budget.IncidentId;
                    if (now >= budget.DeadlineUtc)
                    {
                        EmitEnded(
                            EndReasonOpenFailuresExhausted,
                            $"{outageOpenFailures} EOF re-opens while container running; budget {budget.Source}",
                            lastFault,
                            outageStartUtc,
                            outageIncidentId
                        );
                        return;
                    }

                    _diagnosticCallback?.Invoke(
                        $"{_diagnosticLabel} log stream EOF while {verdict.Detail}; reconnecting"
                    );
                }

                await Task.Delay(ReopenDelay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                EmitEnded(EndReasonCancelled, "token", lastFault, outageStartUtc, outageIncidentId);
                return;
            }
            catch (LineHandlerException ex)
            {
                // A per-line callback failed. That is a sink fault, not a
                // transport loss: end here rather than reconnecting (which
                // would reset the outage budget and skip the line), so the
                // failure surfaces as its own terminal event.
                EmitEnded(
                    EndReasonLineHandlerFaulted,
                    "line handler threw",
                    ex.InnerException,
                    outageStartUtc,
                    outageIncidentId
                );
                return;
            }
            catch (Exception ex)
            {
                lastFault = ex is EofUnconfirmedException ? ex.InnerException! : ex;

                if (ShutdownCoordinator.IsShuttingDown)
                {
                    EmitEnded(
                        EndReasonCancelled,
                        "shutdown_coordinator",
                        lastFault,
                        outageStartUtc,
                        outageIncidentId
                    );
                    return;
                }

                // Docker daemon restart (OOM, WSL crash) returns
                // InternalServerError for every active stream. Notify the
                // coordinator and stop; nothing to reconnect to.
                if (IsDaemonInternalError(lastFault))
                {
                    EmitEnded(
                        EndReasonDockerDown,
                        "daemon_500",
                        lastFault,
                        outageStartUtc,
                        outageIncidentId
                    );
                    ShutdownCoordinator.NotifyDockerDown(
                        $"{_diagnosticLabel} log stream: {lastFault.Message}"
                    );
                    return;
                }

                if (!hasReadAny)
                {
                    // The container may not yet exist on the daemon — server
                    // and client containers start log streaming before
                    // StartAsync. Retry outside the outage budget.
                    if (!await TryDelayAsync(ReopenDelay, ct))
                    {
                        EmitEnded(
                            EndReasonCancelled,
                            "token",
                            lastFault,
                            outageStartUtc,
                            outageIncidentId
                        );
                        return;
                    }
                    continue;
                }

                // Post-data transport loss: socket reset, ssh master gone,
                // daemon stall. Reconnect with the cursor until the budget
                // deadline; the deadline stretches to cover the runner's
                // published re-establish window when one is in effect.
                var now = DateTime.UtcNow;
                outageStartUtc ??= now;
                outageOpenFailures++;
                var budget = ResolveBudget(outageStartUtc.Value);
                outageIncidentId = budget.IncidentId;
                if (now >= budget.DeadlineUtc)
                {
                    EmitEnded(
                        EndReasonOpenFailuresExhausted,
                        $"{outageOpenFailures} failed re-opens; budget {budget.Source}",
                        lastFault,
                        outageStartUtc,
                        outageIncidentId
                    );
                    _diagnosticCallback?.Invoke(
                        $"{_diagnosticLabel} log stream gave up after {outageOpenFailures} "
                            + $"failed re-opens ({budget.Source}): {lastFault.Message}"
                    );
                    return;
                }
                _diagnosticCallback?.Invoke(
                    $"{_diagnosticLabel} log stream re-open {outageOpenFailures} failed "
                        + $"({(budget.DeadlineUtc - now).TotalSeconds:F0}s left): {lastFault.Message}"
                );

                if (!await TryDelayAsync(ReopenDelay, ct))
                {
                    EmitEnded(
                        EndReasonCancelled,
                        "token",
                        lastFault,
                        outageStartUtc,
                        outageIncidentId
                    );
                    return;
                }
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }

    /// <summary>False when the token fired before the delay elapsed.</summary>
    private static async Task<bool> TryDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Carries a failed post-EOF inspect into the open-failure handling with
    /// the daemon's exception as <see cref="Exception.InnerException"/>.
    /// </summary>
    private sealed class EofUnconfirmedException(string detail, Exception inner)
        : Exception(detail, inner);

    /// <summary>
    /// Wraps a fault thrown by the per-line <see cref="LineHandler"/> so
    /// <see cref="RunInternalAsync"/> ends the reader with
    /// <c>line_handler_faulted</c> rather than misclassifying a sink failure as
    /// a transport loss and reconnecting.
    /// </summary>
    private sealed class LineHandlerException(Exception inner) : Exception(inner.Message, inner);

    private readonly record struct EofVerdict(bool Exited, string Detail, Exception? Fault);

    private async Task<EofVerdict> InspectAfterEofAsync(CancellationToken ct)
    {
        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(_container.Id, ct);
            var state = inspect.State;
            if (state is null || !state.Running)
            {
                return new EofVerdict(
                    Exited: true,
                    $"inspect: State.Running=false status={state?.Status ?? "unknown"} exitCode={state?.ExitCode}",
                    Fault: null
                );
            }

            return new EofVerdict(
                Exited: false,
                $"inspect: State.Running=true status={state.Status}",
                Fault: null
            );
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new EofVerdict(Exited: true, "inspect: container not found", Fault: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EofVerdict(Exited: false, "inspect failed", ex);
        }
    }

    private static bool IsDaemonInternalError(Exception fault)
    {
        for (var e = fault; e is not null; e = e.InnerException)
        {
            if (e is DockerApiException { StatusCode: HttpStatusCode.InternalServerError })
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct ReconnectBudget(
        DateTime DeadlineUtc,
        string Source,
        string? IncidentId
    );

    /// <summary>
    /// Deadline for giving up on an outage that began at
    /// <paramref name="outageStartUtc"/>: the fixed default, stretched to the
    /// end of the runner's re-establish window when
    /// the host's <c>diagnostics/transport-state.{hostId}.json</c> shows an action overlapping the
    /// outage. An in-progress action (no <c>windowEndUtc</c> yet) waits up to
    /// twice the default from the action start — the forward comes back only
    /// once the respawn completes, but a runner that died mid-action never
    /// finishes the document.
    /// </summary>
    private ReconnectBudget ResolveBudget(DateTime outageStartUtc)
    {
        var defaultDeadline = outageStartUtc + DefaultReconnectBudget;
        var defaultSource = $"default {DefaultReconnectBudget.TotalSeconds:F0}s";
        var state = TryReadTransportState();
        if (state is null || state.ActionStartedAtUtc < outageStartUtc - DefaultReconnectBudget)
        {
            return new ReconnectBudget(defaultDeadline, defaultSource, null);
        }

        if (state.WindowEndUtc is not { } windowEnd)
        {
            var inProgressDeadline = state.ActionStartedAtUtc + DefaultReconnectBudget * 2;
            return new ReconnectBudget(
                inProgressDeadline > defaultDeadline ? inProgressDeadline : defaultDeadline,
                $"incident {state.IncidentId} in progress",
                state.IncidentId
            );
        }

        return windowEnd > defaultDeadline
            ? new ReconnectBudget(
                windowEnd,
                $"incident {state.IncidentId} window",
                state.IncidentId
            )
            : new ReconnectBudget(defaultDeadline, defaultSource, state.IncidentId);
    }

    private TransportState? TryReadTransportState()
    {
        try
        {
            var state = TransportStateFile.TryRead(TestArtifacts.RunDir, _hostId);
            _lastUnreadableError = null;
            return state;
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
            )
        {
            // ResolveBudget reads this file once per reopen (~1s) during an
            // outage, so a persistently unreadable file would emit one event
            // per second per container. Emit only when the error first appears
            // or changes; a successful read re-arms it.
            var signature = $"{ex.GetType().Name}: {ex.Message}";
            if (signature != _lastUnreadableError)
            {
                _lastUnreadableError = signature;
                InfrastructureEventLog.Emit(
                    TransportEventNames.TransportStateUnreadable,
                    new TransportStateUnreadableEvent(
                        TransportStateFile.PathFor(TestArtifacts.RunDir, _hostId),
                        ex.GetType().Name,
                        ex.Message,
                        Label: _diagnosticLabel,
                        HostId: _hostId
                    )
                );
            }
            return null;
        }
    }

    private void EmitReconnected(DateTime outageStartUtc, int openFailures, string? incidentId)
    {
        _reconnects++;
        var now = DateTime.UtcNow;
        InfrastructureEventLog.Emit(
            TransportEventNames.ContainerLogStreamReconnected,
            new StreamReconnectedEvent(
                _diagnosticLabel,
                _hostId,
                outageStartUtc,
                now,
                (long)(now - outageStartUtc).TotalMilliseconds,
                openFailures,
                _sinceCursor,
                incidentId
            )
        );
        _diagnosticCallback?.Invoke(
            $"{_diagnosticLabel} log stream reconnected after {(now - outageStartUtc).TotalSeconds:F1}s"
        );
    }

    private void EmitEnded(
        string reason,
        string detail,
        Exception? lastFault,
        DateTime? outageStartUtc,
        string? incidentId
    )
    {
        InfrastructureEventLog.Emit(
            TransportEventNames.ContainerLogStreamEnded,
            new StreamEndedEvent(
                _diagnosticLabel,
                _hostId,
                reason,
                detail,
                lastFault?.GetType().Name,
                lastFault?.Message,
                lastFault is null ? null : TransportEventFormat.Chain(lastFault),
                _sinceCursor,
                _linesEmitted,
                _reconnects,
                outageStartUtc is { } start
                    ? (long)(DateTime.UtcNow - start).TotalMilliseconds
                    : null,
                incidentId
            )
        );
    }

    private async Task PumpAsync(MultiplexedStream stream, Action onFirstRead, CancellationToken ct)
    {
        // Per-target line buffers: the daemon delivers chunks tagged stdout
        // or stderr, and a chunk boundary may fall mid-line on either target.
        // Independent buffers so a stderr read doesn't corrupt a partial
        // stdout line and vice versa. Today's poll-loop path concatenated
        // Stdout + Stderr before splitting; emitting both targets through one
        // handler is behavioural parity.
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();
        var readBuffer = new byte[ReadBufferSize];
        var firstRead = true;

        while (!ct.IsCancellationRequested)
        {
            var result = await stream.ReadOutputAsync(readBuffer, 0, readBuffer.Length, ct);

            if (result.EOF)
            {
                // Flush trailing partial lines — a daemon-clean EOF (container
                // exited) often closes mid-line. Tail content is logically
                // complete at EOF; emit it.
                FlushPartial(stdoutBuffer);
                FlushPartial(stderrBuffer);
                return;
            }

            if (result.Count == 0)
            {
                continue;
            }

            if (firstRead)
            {
                onFirstRead();
                firstRead = false;
            }

            RecordGap(DateTime.UtcNow);

            var sb =
                result.Target == MultiplexedStream.TargetStream.StandardError
                    ? stderrBuffer
                    : stdoutBuffer;

            sb.Append(Encoding.UTF8.GetString(readBuffer, 0, result.Count));

            // Split sb on '\n', emit each complete line, retain any trailing
            // partial line for the next read on the same target.
            int newlineIndex;
            while ((newlineIndex = IndexOfNewline(sb)) >= 0)
            {
                var line = sb.ToString(0, newlineIndex);
                sb.Remove(0, newlineIndex + 1);
                EmitLine(line);
            }
        }
    }

    private void RecordGap(DateTime nowUtc)
    {
        var previous = _lastChunkAtUtc;
        _lastChunkAtUtc = nowUtc;
        if (previous == default || nowUtc - previous < GapThreshold)
        {
            return;
        }

        InfrastructureEventLog.Emit(
            TransportEventNames.ContainerLogStreamGap,
            new StreamGapEvent(
                _diagnosticLabel,
                _hostId,
                previous,
                nowUtc,
                (long)(nowUtc - previous).TotalMilliseconds
            )
        );
    }

    private static int IndexOfNewline(StringBuilder sb)
    {
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private void FlushPartial(StringBuilder sb)
    {
        if (sb.Length == 0)
        {
            return;
        }

        var tail = sb.ToString();
        sb.Clear();
        EmitLine(tail);
    }

    private void EmitLine(string raw)
    {
        // Trim the trailing CR a Windows-style line might carry, plus any
        // stray whitespace; LF was already consumed by the splitter.
        var trimmed = raw.TrimEnd();
        if (trimmed.Length == 0)
        {
            return;
        }

        // Dedup the single inclusive replay produced by reconnecting with
        // Since = lastTimestamp. The first emit after a reconnect is the
        // candidate; if it matches the last-emitted raw line byte-for-byte,
        // skip it. Either path clears the flag — subsequent lines flow
        // through normally.
        if (_expectDedupNextEmit)
        {
            _expectDedupNextEmit = false;
            if (trimmed == _lastRawEmitted)
            {
                return;
            }
        }

        _lastRawEmitted = trimmed;

        // Parse the daemon's timestamp prefix → cursor, then strip it.
        // A parse miss must not poison the stream: keep the previous cursor
        // and emit the line unchanged minus the logmonitor tag. The next
        // valid timestamp will advance the cursor; the worst case is a
        // small replay window on reconnect rather than a stalled feed.
        var line = trimmed;
        var match = DaemonTimestampPrefix.Match(line);
        if (match.Success)
        {
            _sinceCursor = match.Groups[1].Value;
            line = line.Substring(match.Length);
        }

        line = LogmonitorProcessTag.Replace(line, "");
        if (line.Length == 0)
        {
            return;
        }

        // A line-handler fault is a sink failure, not a transport loss. Wrap it
        // so RunInternalAsync ends the reader instead of catching it as a failed
        // read and reconnecting. _linesEmitted counts only delivered lines.
        try
        {
            _onLine(line);
        }
        catch (Exception ex)
        {
            throw new LineHandlerException(ex);
        }

        _linesEmitted++;
    }
}
