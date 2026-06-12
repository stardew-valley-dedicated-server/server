using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// A screenshot was captured for a test (emitted asynchronously after test
/// completion).
/// </summary>
public sealed record ScreenshotCapturedEvent(
    string TestCollection,
    string TestClass,
    string DisplayName,
    string ScreenshotPath,
    string Source = "server"
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.Screenshot;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A video recording was captured for a test (emitted asynchronously after test
/// completion).
/// </summary>
public sealed record RecordingCapturedEvent(
    string TestCollection,
    string TestClass,
    string DisplayName,
    string RecordingPath,
    string Source = "server",
    double TimelineOffset = 0,
    double WallClockDuration = 0
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.Recording;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Reason a per-test video recording was not produced for a given source. Mirrors
/// the existing <c>recording_per_test_clip_skipped</c> infra-log reasons plus the
/// new <see cref="ArtifactsOptedOut"/> site. Wire format is snake_case (matches
/// the legacy reason strings on <c>infrastructure.jsonl</c>).
/// </summary>
[JsonConverter(typeof(SnakeCaseRecordingSkipReasonConverter))]
public enum RecordingSkipReason
{
    /// <summary>Class-level <c>[TestServer(Artifacts = false)]</c> on a passing test.</summary>
    ArtifactsOptedOut,

    /// <summary><c>SDVD_TEST_RECORDING=failure</c> + test passed → clip discarded by retention.</summary>
    RetentionPassed,

    /// <summary>Recorder produced no end timestamp; can't compute clip bounds.</summary>
    EndTimeMissing,

    /// <summary>Recorder existed but never reached running state.</summary>
    RecorderNeverStarted,

    /// <summary>No recorder registered for the marked container.</summary>
    RecorderMissing,

    /// <summary>Computed clip duration was non-positive.</summary>
    ZeroDuration,

    /// <summary>Per-clip ffmpeg extraction returned null / threw.</summary>
    ExtractionFailed,

    /// <summary>Deferred orchestrator finalize threw on the broker's background queue.</summary>
    FinalizeDeferredFailed,
}

/// <summary>
/// A per-test recording clip was NOT produced for one of the reasons in
/// <see cref="RecordingSkipReason"/>. Carries attribution as explicit parameters
/// (no <c>TestContext.Current</c> reads) because most emit sites run deferred
/// under <c>ExecutionContext.SuppressFlow</c>.
///
/// <para>
/// Source naming matches <see cref="RecordingCapturedEvent"/>:
/// <list type="bullet">
///   <item><c>"server"</c> — the server card.</item>
///   <item><c>"client"</c> — un-indexed; applies to ALL client cards for skips
///     produced before per-test indexing (server-acquire-time decisions like
///     <c>ArtifactsOptedOut</c> or <c>RetentionPassed</c>).</item>
///   <item><c>"client"</c>, <c>"client_2"</c>, <c>"client_3"</c>, … — indexed
///     for skips produced past indexing in the orchestrator
///     (<c>ZeroDuration</c>, <c>ExtractionFailed</c>); mirrors the corresponding
///     success event's source.</item>
/// </list>
/// The UI's per-source map handles both: it looks up the indexed source first,
/// falling back to the un-indexed <c>"client"</c> entry.
/// </para>
/// </summary>
public sealed record RecordingSkippedEvent(
    string TestCollection,
    string TestClass,
    string DisplayName,
    string Source,
    RecordingSkipReason Reason
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.RecordingSkipped;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Snake-case wire format for <see cref="RecordingSkipReason"/>. Matches the
/// existing infra-log <c>recording_per_test_clip_skipped</c> reason strings
/// (<c>retention_passed</c>, <c>recorder_missing</c>, …) and the test-ui
/// <c>recordingSkipReasons</c> dictionary keys.
/// </summary>
internal sealed class SnakeCaseRecordingSkipReasonConverter
    : JsonStringEnumConverter<RecordingSkipReason>
{
    public SnakeCaseRecordingSkipReasonConverter()
        : base(namingPolicy: JsonNamingPolicy.SnakeCaseLower) { }
}

/// <summary>
/// A VNC URL became available; the web UI displays these as clickable links.
/// </summary>
public sealed record VncUrlEvent(
    string Label,
    string Url,
    [property: JsonPropertyName("collection")] string? CollectionName = null
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.VncUrl;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
