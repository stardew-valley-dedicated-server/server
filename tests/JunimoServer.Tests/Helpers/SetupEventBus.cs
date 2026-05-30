using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using JunimoServer.Tests.Schema;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Static event bus for fixture setup progress.
///
/// Producers call <c>EmitX</c>; this builds a typed <see cref="IRendererEvent"/>
/// record and hands it to the parent <see cref="JunimoServer.TestRunner"/>'s
/// renderer over a named pipe. The pipe is the only sink — the test assembly
/// must be invoked through the runner.
///
/// <para>
/// <b>This bus does NOT persist events to disk.</b> Events flow through the IPC
/// pipe to the runner's state model and drive the live UI; the runner does not
/// mirror them into <c>infrastructure.jsonl</c>. If a future reader needs to
/// confirm an <c>EmitInstanceLeased/Connected/Disconnected</c> fired from a
/// completed run's artifacts, those events are not there. For events that must
/// survive into the on-disk diagnostic log, use
/// <see cref="InfrastructureEventLog.Emit"/> instead (which writes to
/// <c>{runDir}/diagnostics/infrastructure.jsonl</c>).
/// </para>
/// </summary>
public static class SetupEventBus
{
    private static readonly Lazy<ISetupSink> Sink = new(CreateSink);

    private static ISetupSink CreateSink()
    {
        var pipeName = Environment.GetEnvironmentVariable("SDVD_SETUP_PIPE");
        if (string.IsNullOrEmpty(pipeName))
            throw new InvalidOperationException(
                "JunimoServer.Tests must be run via the JunimoServer.TestRunner " +
                "(`make test`); SDVD_SETUP_PIPE is not set.");

        var sink = NamedPipeSink.TryCreate(pipeName);
        if (sink == null)
            throw new InvalidOperationException(
                $"JunimoServer.Tests could not connect to the parent runner's setup pipe " +
                $"'{pipeName}'. Ensure the assembly is launched by JunimoServer.TestRunner " +
                "(`make test`).");

        return sink;
    }

    // ── Static facade — each EmitX builds a typed record and forwards to Sink ──

    public static void EmitPhaseStarted(string category, string phaseName, string? collectionName = null)
        => Sink.Value.Emit(new SetupPhaseStartedEvent(category, phaseName, collectionName));

    public static void EmitPhaseCompleted(string category, string phaseName, bool success,
        string? errorMessage = null, string? collectionName = null)
        => Sink.Value.Emit(new SetupPhaseCompletedEvent(category, phaseName, success, errorMessage, collectionName));

    public static void EmitStep(string category, string stepName, SetupStepStatus status,
        string? details = null, string? collectionName = null)
        => Sink.Value.Emit(new SetupStepEvent(category, stepName, status, details, collectionName));

    public static void EmitScreenshot(string testCollection, string testClass,
        string displayName, string screenshotPath, string source = "server")
        => Sink.Value.Emit(new ScreenshotCapturedEvent(testCollection, testClass, displayName, screenshotPath, source));

    public static void EmitRecording(string testCollection, string testClass,
        string displayName, string recordingPath, string source = "server",
        double timelineOffset = 0, double wallClockDuration = 0)
        => Sink.Value.Emit(new RecordingCapturedEvent(
            testCollection, testClass, displayName, recordingPath, source, timelineOffset, wallClockDuration));

    /// <summary>
    /// Announce that a per-test recording was NOT produced for a given source.
    /// Attribution is passed explicitly (no <c>TestContext.Current</c> reads)
    /// because most callers run deferred under <c>ExecutionContext.SuppressFlow</c>.
    /// Pipe-write failures are swallowed by <see cref="NamedPipeSink.Emit"/>.
    /// </summary>
    public static void EmitRecordingSkipped(string testCollection, string testClass,
        string displayName, string source, RecordingSkipReason reason)
        => Sink.Value.Emit(new RecordingSkippedEvent(
            testCollection, testClass, displayName, source, reason));

    public static void EmitTestAnnotation(string displayName, AnnotationLevel level,
        AnnotationSource source, string message)
        => Sink.Value.Emit(new TestAnnotationEvent(displayName, level, source, message));

    public static void EmitRunMetadata(RunMetadata.RunMetadataJson metadata)
    {
        // RunDir is [JsonIgnore]'d on the DTO (excluded from on-disk
        // run-metadata.json because the path is the run dir itself) — emit it
        // as a sibling field on the wire so the runner-side artifact writer
        // can locate the run dir.
        var data = DiagnosticEmitJson.SerializeToElement(metadata);
        Sink.Value.Emit(new RunMetadataEvent(metadata.RunDir, data));
    }

    public static void EmitFlakyTests(IReadOnlyList<object> entries)
    {
        var tests = DiagnosticEmitJson.SerializeToElement(entries);
        Sink.Value.Emit(new FlakyTestsEvent(tests));
    }

    public static void EmitTestEnrichment(string displayName, TestEnrichmentData data)
    {
        var failureContext = data.FailureContext != null
            ? (JsonElement?)DiagnosticEmitJson.SerializeToElement(data.FailureContext)
            : null;
        Sink.Value.Emit(new TestEnrichmentEvent(
            DisplayName: displayName,
            Outcome: data.Outcome,
            FailureCategory: data.FailureCategory,
            ErrorPreview: data.ErrorPreview,
            Phase: data.Phase,
            ReproCommand: data.ReproCommand,
            ServerKey: data.ServerKey,
            ServerInstanceId: data.ServerInstanceId,
            ScreenshotPath: data.ScreenshotPath,
            TestBodyMs: data.TestBodyMs,
            ArtifactsMs: data.ArtifactsMs,
            CleanupMs: data.CleanupMs,
            LastKeepDisposeMs: data.LastKeepDisposeMs,
            LeaseReleaseMs: data.LeaseReleaseMs,
            FailureContext: failureContext));
    }

    public static void EmitVncUrl(string label, string url, string? collectionName = null)
        => Sink.Value.Emit(new VncUrlEvent(label, url, collectionName));

    public static void EmitTestRunning(string testCollection, string testClass,
        string testMethod, string displayName)
        => Sink.Value.Emit(new TestRunningEvent(testCollection, testClass, testMethod, displayName));

    // ── Instance lifecycle ──

    public static void EmitInstanceCreated(string instanceId, string instanceType,
        string serverKey, string? vncUrl, string? label, string hostId)
        => Sink.Value.Emit(new InstanceCreatedEvent(instanceId, instanceType, serverKey, vncUrl, label, hostId));

    public static void EmitInstanceLeased(string instanceId, string testName, string? serverInstanceId = null)
        => Sink.Value.Emit(new InstanceLeasedEvent(instanceId, testName, serverInstanceId));

    public static void EmitInstanceClientAttached(string serverInstanceId, string clientInstanceId)
        => Sink.Value.Emit(new InstanceClientAttachedEvent(serverInstanceId, clientInstanceId));

    public static void EmitInstanceReturned(string instanceId)
        => Sink.Value.Emit(new InstanceReturnedEvent(instanceId));

    public static void EmitInstanceDisposed(string instanceId)
        => Sink.Value.Emit(new InstanceDisposedEvent(instanceId));

    public static void EmitInstanceRecording(string instanceId, string recordingPath)
        => Sink.Value.Emit(new InstanceRecordingEvent(instanceId, recordingPath));

    public static void EmitInstancePoisoned(string instanceId, string reason)
        => Sink.Value.Emit(new InstancePoisonedEvent(instanceId, reason));

    public static void EmitInstanceConnected(string instanceId)
        => Sink.Value.Emit(new InstanceConnectedEvent(instanceId));

    public static void EmitInstanceDisconnected(string instanceId)
        => Sink.Value.Emit(new InstanceDisconnectedEvent(instanceId));

    public static void EmitInstanceStats(string instanceId, InstanceStatsData data, string hostId)
        => Sink.Value.Emit(new InstanceStatsEvent(instanceId, hostId, data));

    // ── Display helpers (used by the runner-side renderer) ──

    /// <summary>
    /// Transforms a gerund step name to past tense for completed steps.
    /// e.g. "Starting X" → "Started X", "Building X" → "Built X".
    /// </summary>
    public static string ToPastTense(string stepName)
    {
        if (string.IsNullOrEmpty(stepName)) return stepName;

        if (stepName.StartsWith("Starting ", StringComparison.OrdinalIgnoreCase))
            return "Started " + stepName[9..];
        if (stepName.StartsWith("Building ", StringComparison.OrdinalIgnoreCase))
            return "Built " + stepName[9..];
        if (stepName.StartsWith("Preparing ", StringComparison.OrdinalIgnoreCase))
            return "Prepared " + stepName[10..];
        if (stepName.StartsWith("Checking ", StringComparison.OrdinalIgnoreCase))
            return "Checked " + stepName[9..];
        if (stepName.StartsWith("Waiting for ", StringComparison.OrdinalIgnoreCase))
            return "Waited for " + stepName[12..];
        if (stepName.StartsWith("Cleaning ", StringComparison.OrdinalIgnoreCase))
            return "Cleaned " + stepName[9..];
        if (stepName.StartsWith("Loading ", StringComparison.OrdinalIgnoreCase))
            return "Loaded " + stepName[8..];
        if (stepName.StartsWith("Creating ", StringComparison.OrdinalIgnoreCase))
            return "Created " + stepName[9..];
        if (stepName.StartsWith("Inspecting ", StringComparison.OrdinalIgnoreCase))
            return "Inspected " + stepName[11..];
        if (stepName.StartsWith("Connecting ", StringComparison.OrdinalIgnoreCase))
            return "Connected " + stepName[11..];

        return stepName;
    }

    /// <summary>Format a duration for display. Matches RendererBase.FormatDuration.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1}m", duration.TotalMinutes);
        if (duration.TotalSeconds >= 1)
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}s", duration.TotalSeconds);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F0}ms", duration.TotalMilliseconds);
    }

    // ── Sink interface ──

    private interface ISetupSink
    {
        void Emit<T>(T evt) where T : IRendererEvent;
    }

    // ── NamedPipeSink: writes serialized UTF-8 bytes directly to the pipe ──

    /// <summary>
    /// Writes events as JSONL to a named pipe consumed by the parent test
    /// runner's <c>SetupPipeServer</c>. Serializes each event with
    /// <see cref="DiagnosticEmitJson.SerializeToUtf8Bytes{T}(T)"/> and
    /// enqueues the resulting <c>byte[]</c> onto an unbounded channel; a
    /// single long-running consumer task drains the channel and writes bytes
    /// directly to the pipe stream, batching one flush per drain pass.
    ///
    /// <para>
    /// <b>Pipe failures must never abort the test process.</b> The parent
    /// runner can drop the pipe at any time; the consumer catches IO/operation
    /// errors and silently terminates. <b>ProcessExit drains the channel</b> so
    /// late events (flaky_tests, test_enrichment) reach the parent's
    /// <c>SetupPipeServer.DrainAsync</c> via natural pipe EOF instead of being
    /// aborted with the consumer thread on process tear-down — see
    /// <c>.claude/rules/drain-before-consume-disposal.md</c>.
    /// </para>
    /// </summary>
    private sealed class NamedPipeSink : ISetupSink
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly Channel<byte[]> _channel;
        private readonly Task _consumerTask;
        private readonly CancellationTokenSource _cts = new();
        private int _disposed;

        private NamedPipeSink(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

            // Suppress flow so the long-lived consumer task does not inherit
            // the EC of whichever test happened to first wake the bus.
            // Per .claude/rules/asynclocal-pitfalls.md.
            using (ExecutionContext.SuppressFlow())
            {
                _consumerTask = Task.Factory.StartNew(
                    ConsumeLoop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { DrainAsync(TimeSpan.FromSeconds(2)).Wait(); } catch { }
            };
        }

        public static NamedPipeSink? TryCreate(string pipeName)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                pipe.Connect(timeout: 5000);
                return new NamedPipeSink(pipe);
            }
            catch
            {
                return null;
            }
        }

        public void Emit<T>(T evt) where T : IRendererEvent
        {
            try
            {
                var bytes = DiagnosticEmitJson.SerializeToUtf8Bytes(evt);
                _channel.Writer.TryWrite(bytes);
            }
            catch
            {
                // Pipe failures must never abort the test process.
            }
        }

        private async Task ConsumeLoop()
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var payload))
                    {
                        await _pipe.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                        _pipe.WriteByte((byte)'\n');
                    }
                    await _pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Parent dropped the pipe / shutdown / IO; nothing actionable here.
            }
        }

        private async Task DrainAsync(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _channel.Writer.TryComplete();
            try
            {
                await _consumerTask.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException) { /* drain budget exceeded; proceed with dispose */ }
            catch { /* consumer faulted; nothing more to do */ }

            try { await _pipe.FlushAsync().ConfigureAwait(false); } catch { }
            try { await _pipe.DisposeAsync().ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}

/// <summary>
/// Per-test enrichment payload. Carries fields known only to the test child
/// process (failure category, phase, repro command, server context, lifecycle
/// breakdown, optional failure-context dump). The runner correlates this with
/// the xUnit-native test_passed/failed/skipped event by displayName.
/// </summary>
public sealed record TestEnrichmentData(
    string Outcome,
    string? FailureCategory,
    string? ErrorPreview,
    string? Phase,
    string? ReproCommand,
    string? ServerKey,
    string? ServerInstanceId,
    string? ScreenshotPath,
    long TestBodyMs,
    long ArtifactsMs,
    long CleanupMs,
    long LastKeepDisposeMs,
    long LeaseReleaseMs,
    IReadOnlyDictionary<string, object?>? FailureContext);

/// <summary>Status of a setup step.</summary>
public enum SetupStepStatus
{
    Started,
    InProgress,
    Completed,
    Failed,
    Warning,
}

/// <summary>Extension to serialize <see cref="SetupStepStatus"/> as snake_case.</summary>
public static class SetupStepStatusExtensions
{
    public static string ToSnakeCase(this SetupStepStatus status) => status switch
    {
        SetupStepStatus.Started => "started",
        SetupStepStatus.InProgress => "in_progress",
        SetupStepStatus.Completed => "completed",
        SetupStepStatus.Failed => "failed",
        SetupStepStatus.Warning => "warning",
        _ => status.ToString().ToLowerInvariant(),
    };
}
