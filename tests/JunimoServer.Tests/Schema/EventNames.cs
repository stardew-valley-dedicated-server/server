namespace JunimoServer.Tests.Schema;

/// <summary>
/// Single source of truth for the <c>"event"</c> discriminator string emitted on
/// every <see cref="IRendererEvent"/>. Producer-side records reference these
/// constants via <c>[JsonPropertyName("event")] string EventName =&gt; EventNames.X</c>;
/// the consumer-side <c>EventDispatcher</c> route table keys off the same
/// constants. A typo on either side is a compile error.
/// </summary>
public static class EventNames
{
    // Setup phase events (camelCase wire policy)
    public const string SetupStarted = "setup_started";
    public const string SetupCompleted = "setup_completed";
    public const string SetupStep = "setup_step";

    // Test lifecycle (xUnit-native, snake_case wire policy via per-property attributes)
    public const string DiscoveryComplete = "discovery_complete";
    public const string RunStarted = "run_started";
    public const string RunFinished = "run_finished";
    public const string TestStarted = "test_started";
    public const string TestPassed = "test_passed";
    public const string TestFailed = "test_failed";
    public const string TestSkipped = "test_skipped";
    public const string Diagnostic = "diagnostic";
    public const string Error = "error";

    // Test running (broker lease acquired) and child-process supplements
    public const string TestRunning = "test_running";
    public const string TestOutput = "test_output";
    public const string TestAnnotation = "test_annotation";
    public const string TestEnrichment = "test_enrichment";

    // Run-level
    public const string RunMetadata = "run_metadata";
    public const string FlakyTests = "flaky_tests";

    // Artifacts
    public const string Screenshot = "screenshot";
    public const string Recording = "recording";
    public const string RecordingSkipped = "recording_skipped";
    public const string VncUrl = "vnc_url";

    // Instance lifecycle
    public const string InstanceCreated = "instance_created";
    public const string InstanceLeased = "instance_leased";
    public const string InstanceClientAttached = "instance_client_attached";
    public const string InstanceReturned = "instance_returned";
    public const string InstanceDisposed = "instance_disposed";
    public const string InstanceRecording = "instance_recording";
    public const string InstancePoisoned = "instance_poisoned";
    public const string InstanceConnected = "instance_connected";
    public const string InstanceDisconnected = "instance_disconnected";
    public const string InstanceStats = "instance_stats";
}
