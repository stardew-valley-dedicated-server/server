using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// Individual test started.
///
/// xUnit-native lifecycle records use snake_case wire keys (test_collection,
/// display_name, …) while the rest of the bus uses camelCase. The two policies
/// were independently established before they converged on a shared
/// dispatcher; the per-property attributes below preserve byte-for-byte wire
/// compatibility.
/// </summary>
public sealed record TestStartedEvent(
    [property: JsonPropertyName("test_collection")] string TestCollection,
    [property: JsonPropertyName("test_class")] string TestClass,
    [property: JsonPropertyName("test_method")] string TestMethod,
    [property: JsonPropertyName("display_name")] string DisplayName
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestStarted;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Test transitioned from queued to actively running (broker lease acquired).
/// Emits with camelCase keys via <c>SetupEventBus.EmitTestRunning</c>; the
/// xUnit-lifecycle snake_case policy does not apply here.
/// </summary>
public sealed record TestRunningEvent(
    string TestCollection,
    string TestClass,
    string TestMethod,
    string DisplayName
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestRunning;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Test passed.
/// </summary>
public sealed record TestPassedEvent(
    [property: JsonPropertyName("test_collection")] string TestCollection,
    [property: JsonPropertyName("test_class")] string TestClass,
    [property: JsonPropertyName("test_method")] string TestMethod,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property:
        JsonConverter(typeof(TimeSpanMillisecondsJsonConverter)),
        JsonPropertyName("duration_ms")
    ]
        TimeSpan Duration
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestPassed;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Test failed.
/// </summary>
public sealed record TestFailedEvent(
    [property: JsonPropertyName("test_collection")] string TestCollection,
    [property: JsonPropertyName("test_class")] string TestClass,
    [property: JsonPropertyName("test_method")] string TestMethod,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property:
        JsonConverter(typeof(TimeSpanMillisecondsJsonConverter)),
        JsonPropertyName("duration_ms")
    ]
        TimeSpan Duration,
    [property: JsonPropertyName("exception_type")] string ExceptionType,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("stack_trace")] string? StackTrace,
    [property: JsonPropertyName("screenshot_path")] string? ScreenshotPath = null,
    [property: JsonPropertyName("artifact_id")] string? ArtifactId = null
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestFailed;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Test skipped.
/// </summary>
public sealed record TestSkippedEvent(
    [property: JsonPropertyName("test_collection")] string TestCollection,
    [property: JsonPropertyName("test_class")] string TestClass,
    [property: JsonPropertyName("test_method")] string TestMethod,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("reason")] string Reason
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestSkipped;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A single output line from a running test, streamed in real-time via named pipe IPC.
/// </summary>
public sealed record TestOutputEvent(string DisplayName, string Line) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestOutput;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Per-test free-form annotation (Plane C). Carries a severity level + producer
/// source so renderers can format consistently. Single sink for all
/// <c>TestBase.Log*</c> calls and curated infrastructure narration
/// (broker, recording, mod-forwarded).
/// </summary>
public sealed record TestAnnotationEvent(
    string DisplayName,
    AnnotationLevel Level,
    AnnotationSource Source,
    string Message
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestAnnotation;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Per-test enrichment carrying fields known only to the test child process.
/// </summary>
public sealed record TestEnrichmentEvent(
    string DisplayName,
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
    JsonElement? FailureContext
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.TestEnrichment;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Serializes a <see cref="TimeSpan"/> as integer milliseconds (a JSON number,
/// rounded half-away-from-zero from <c>TotalMilliseconds</c>) so the
/// <c>duration_ms</c> field is uniformly integer-typed on the wire. Other
/// emitters of the same field — LLMRenderer, TestRunArtifactWriter,
/// TestRunState — already cast via <c>(long)…TotalMilliseconds</c>; this
/// converter applies the same convention to the xUnit-lifecycle records.
/// Read stays on <c>GetDouble()</c> so older wire data with fractional
/// values still parses cleanly.
/// </summary>
public sealed class TimeSpanMillisecondsJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => TimeSpan.FromMilliseconds(reader.GetDouble());

    public override void Write(
        Utf8JsonWriter writer,
        TimeSpan value,
        JsonSerializerOptions options
    ) =>
        writer.WriteNumberValue(
            (long)Math.Round(value.TotalMilliseconds, MidpointRounding.AwayFromZero)
        );
}
