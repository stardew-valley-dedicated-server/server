using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using JunimoServer.Tests.Helpers;

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

    private const int MaxConsecutiveOpenFailures = 3;
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
    private readonly LineHandler _onLine;
    private readonly Action<string>? _diagnosticCallback;

    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;
    private string? _sinceCursor;

    // The last raw (pre-strip) line emitted, including the timestamp prefix.
    // Used to dedup a single line of overlap on reconnect: Docker's `Since`
    // filter is inclusive, so a follow=true reopen with Since = lastTimestamp
    // replays the line that produced that timestamp. We skip the replay if
    // it matches byte-for-byte. Multiple lines with the same timestamp are
    // not deduped — they re-emit, accepted as the cost of avoiding content
    // hashing on every line. Reconnects are rare (transient network only).
    private string? _lastRawEmitted;
    private bool _expectDedupNextEmit;

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
    /// <param name="onLine">
    /// Per-line callback. Called for each non-empty line after the daemon
    /// timestamp prefix and the logmonitor process tag have been stripped.
    /// </param>
    /// <param name="diagnosticCallback">
    /// Optional sink for human-readable status messages
    /// (e.g. consecutive-error counts). Off-band from <c>onLine</c>.
    /// </param>
    public ContainerLogStreamReader(
        DockerClient client,
        IContainer container,
        string diagnosticLabel,
        LineHandler onLine,
        Action<string>? diagnosticCallback = null
    )
    {
        _client = client;
        _container = container;
        _diagnosticLabel = diagnosticLabel;
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
        var consecutiveOpenFailures = 0;
        var hasReadAny = false;

        while (!ct.IsCancellationRequested)
        {
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

                consecutiveOpenFailures = 0;

                // Pump until EOF, cancellation, or transient failure. The
                // first successful read flips hasReadAny so future opens
                // are treated as reconnects (cursor advances) rather than
                // initial container-not-yet-created retries.
                await PumpAsync(stream, () => hasReadAny = true, ct);

                // PumpAsync returned cleanly. Two cases:
                //
                // 1. hasReadAny == true: daemon closed the stream after
                //    delivering at least one chunk. Container exited under
                //    follow=true — natural end of life for a long-lived
                //    per-container reader. Stop.
                //
                // 2. hasReadAny == false: daemon returned an immediate empty
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
                if (hasReadAny)
                {
                    return;
                }

                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (ShutdownCoordinator.IsShuttingDown)
                {
                    return;
                }

                // Docker daemon restart (OOM, WSL crash) returns
                // InternalServerError for every active stream. Match the
                // existing poll loops: notify the coordinator and stop.
                if (ex.Message.Contains("InternalServerError"))
                {
                    ShutdownCoordinator.NotifyDockerDown(
                        $"{_diagnosticLabel} log stream: {ex.Message}"
                    );
                    return;
                }

                if (!hasReadAny)
                {
                    // The container may not yet exist on the daemon — server
                    // and client containers start log streaming before
                    // StartAsync. Retry without counting toward the strike
                    // threshold.
                    try
                    {
                        await Task.Delay(1000, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                // Post-success transient: socket reset, SSH tunnel hiccup,
                // momentary daemon stall. Reconnect with the cursor so the
                // daemon resumes after the last emitted line.
                if (++consecutiveOpenFailures >= MaxConsecutiveOpenFailures)
                {
                    _diagnosticCallback?.Invoke(
                        $"{_diagnosticLabel} log stream failed "
                            + $"{MaxConsecutiveOpenFailures} consecutive times: {ex.Message}"
                    );
                    return;
                }
                _diagnosticCallback?.Invoke(
                    $"{_diagnosticLabel} log stream error "
                        + $"({consecutiveOpenFailures}/{MaxConsecutiveOpenFailures}): {ex.Message}"
                );

                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                stream?.Dispose();
            }
        }
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

        _onLine(line);
    }
}
