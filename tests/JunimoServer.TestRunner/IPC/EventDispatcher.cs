using System.Text.Json;
using JunimoServer.TestRunner.Diagnostics;
using JunimoServer.TestRunner.Rendering;
using JunimoServer.Tests.Schema;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.TestRunner.IPC;

/// <summary>
/// Parses an opaque JSONL line and dispatches it to one of <see cref="ITestRenderer"/>'s
/// event methods. Used by <see cref="SetupPipeServer"/> to read the named-pipe
/// stream from the xUnit child process.
///
/// <para>
/// Routing is keyed off <see cref="EventNames"/> constants — the same constants
/// the producer-side records reference via
/// <c>[JsonPropertyName("event")] string EventName =&gt; EventNames.X</c>. A typo
/// on either side is a compile error.
/// </para>
/// </summary>
public static class EventDispatcher
{
    private static readonly Dictionary<string, Action<JsonElement, ITestRenderer>> Routes = new()
    {
        [EventNames.SetupStarted] = (e, r) =>
            r.OnSetupPhaseStarted(Deserialize<SetupPhaseStartedEvent>(e)),
        [EventNames.SetupCompleted] = (e, r) =>
            r.OnSetupPhaseCompleted(Deserialize<SetupPhaseCompletedEvent>(e)),
        [EventNames.SetupStep] = (e, r) => r.OnSetupStep(Deserialize<SetupStepEvent>(e)),
        [EventNames.TestRunning] = (e, r) => r.OnTestRunning(Deserialize<TestRunningEvent>(e)),
        [EventNames.Screenshot] = (e, r) =>
            r.OnScreenshotCaptured(Deserialize<ScreenshotCapturedEvent>(e)),
        [EventNames.Recording] = (e, r) =>
            r.OnRecordingCaptured(Deserialize<RecordingCapturedEvent>(e)),
        [EventNames.RecordingSkipped] = (e, r) =>
            r.OnRecordingSkipped(Deserialize<RecordingSkippedEvent>(e)),
        [EventNames.VncUrl] = (e, r) =>
        {
            var evt = Deserialize<VncUrlEvent>(e);
            if (!string.IsNullOrEmpty(evt.Url))
            {
                r.OnVncUrl(evt);
            }
        },
        [EventNames.InstanceCreated] = (e, r) =>
            r.OnInstanceCreated(Deserialize<InstanceCreatedEvent>(e)),
        [EventNames.InstanceLeased] = (e, r) =>
            r.OnInstanceLeased(Deserialize<InstanceLeasedEvent>(e)),
        [EventNames.InstanceClientAttached] = (e, r) =>
            r.OnInstanceClientAttached(Deserialize<InstanceClientAttachedEvent>(e)),
        [EventNames.InstanceReturned] = (e, r) =>
            r.OnInstanceReturned(Deserialize<InstanceReturnedEvent>(e)),
        [EventNames.InstanceDisposed] = (e, r) =>
            r.OnInstanceDisposed(Deserialize<InstanceDisposedEvent>(e)),
        [EventNames.InstanceRecording] = (e, r) =>
            r.OnInstanceRecording(Deserialize<InstanceRecordingEvent>(e)),
        [EventNames.InstancePoisoned] = (e, r) =>
            r.OnInstancePoisoned(Deserialize<InstancePoisonedEvent>(e)),
        [EventNames.InstanceConnected] = (e, r) =>
            r.OnInstanceConnected(Deserialize<InstanceConnectedEvent>(e)),
        [EventNames.InstanceDisconnected] = (e, r) =>
            r.OnInstanceDisconnected(Deserialize<InstanceDisconnectedEvent>(e)),
        [EventNames.InstanceStats] = (e, r) =>
            r.OnInstanceStats(Deserialize<InstanceStatsEvent>(e)),
        [EventNames.TestOutput] = (e, r) => r.OnTestOutput(Deserialize<TestOutputEvent>(e)),
        [EventNames.TestAnnotation] = (e, r) =>
            r.OnTestAnnotation(Deserialize<TestAnnotationEvent>(e)),
        [EventNames.TestEnrichment] = (e, r) =>
            r.OnTestEnrichment(Deserialize<TestEnrichmentEvent>(e)),
        [EventNames.RunMetadata] = (e, r) =>
        {
            if (!e.TryGetProperty("data", out var dataEl))
            {
                return;
            }

            var runDir =
                e.TryGetProperty("runDir", out var rd) && rd.ValueKind == JsonValueKind.String
                    ? rd.GetString() ?? ""
                    : "";
            r.OnRunMetadata(new RunMetadataEvent(runDir, dataEl.Clone()));
        },
        [EventNames.FlakyTests] = (e, r) =>
        {
            if (!e.TryGetProperty("tests", out var tests))
            {
                return;
            }

            r.OnFlakyTests(new FlakyTestsEvent(tests.Clone()));
        },

        // ── xUnit lifecycle events (snake_case wire keys) ──

        [EventNames.DiscoveryComplete] = (e, r) =>
            r.OnDiscoveryComplete(Deserialize<DiscoveryCompleteEvent>(e)),
        [EventNames.RunStarted] = (e, r) => r.OnRunStarted(Deserialize<RunStartedEvent>(e)),
        [EventNames.RunFinished] = (e, r) => r.OnRunFinished(Deserialize<RunFinishedEvent>(e)),
        [EventNames.TestStarted] = (e, r) => r.OnTestStarted(Deserialize<TestStartedEvent>(e)),
        [EventNames.TestPassed] = (e, r) => r.OnTestPassed(Deserialize<TestPassedEvent>(e)),
        [EventNames.TestFailed] = (e, r) => r.OnTestFailed(Deserialize<TestFailedEvent>(e)),
        [EventNames.TestSkipped] = (e, r) => r.OnTestSkipped(Deserialize<TestSkippedEvent>(e)),
        [EventNames.Diagnostic] = (e, r) => r.OnDiagnostic(Deserialize<DiagnosticEvent>(e)),
        [EventNames.Error] = (e, r) => r.OnError(Deserialize<ErrorEvent>(e)),
    };

    /// <summary>
    /// Parse <paramref name="json"/> and invoke the matching renderer method.
    /// Unknown event types are dropped silently (the wire is permissive --
    /// producers may add new event types ahead of consumer support). Malformed
    /// JSON and renderer-side exceptions are reported once per exception type
    /// on stderr; subsequent failures of the same type are silent so a runaway
    /// producer doesn't flood the log.
    /// </summary>
    public static void Dispatch(string json, ITestRenderer renderer)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("event", out var eventProp))
            {
                return;
            }

            var eventType = eventProp.GetString();
            if (eventType == null)
            {
                return;
            }

            if (Routes.TryGetValue(eventType, out var handler))
            {
                handler(root, renderer);
            }
        }
        catch (Exception ex)
        {
            var preview = json.Length > 120 ? json[..120] + "..." : json;
            FirstFailureLog.ReportOnce(
                "EventDispatcher",
                ex,
                $"dispatch (first 120 chars of line: \"{preview}\")",
                Console.Error.WriteLine
            );
            // Renderer-side throws also surface here. Callers wrap Dispatch()
            // and may count consecutive failures for null-renderer degradation;
            // rethrow non-JSON exceptions so that path sees them.
            if (ex is not JsonException)
            {
                throw;
            }
        }
    }

    private static T Deserialize<T>(JsonElement el)
        where T : IRendererEvent
    {
        var v = DiagnosticEmitJson.Deserialize<T>(el);
        if (v == null)
        {
            throw new JsonException($"Failed to deserialize event as {typeof(T).Name}");
        }

        return v;
    }
}
