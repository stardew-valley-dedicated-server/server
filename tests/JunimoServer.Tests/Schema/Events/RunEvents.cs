using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// Test discovery completed.
/// </summary>
public sealed record DiscoveryCompleteEvent(
    [property: JsonPropertyName("test_cases_discovered")] int TestCasesDiscovered,
    [property: JsonPropertyName("test_cases_to_run")] int TestCasesToRun
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.DiscoveryComplete;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Test assembly execution started.
/// </summary>
public sealed record RunStartedEvent(
    [property: JsonPropertyName("assembly_path")] string AssemblyPath,
    [property: JsonPropertyName("test_cases_to_run")] int TestCasesToRun
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.RunStarted;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Test assembly execution finished.
/// </summary>
public sealed record RunFinishedEvent(
    [property: JsonPropertyName("total_tests")] int TotalTests,
    [property: JsonPropertyName("passed")] int Passed,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property:
        JsonConverter(typeof(TimeSpanMillisecondsJsonConverter)),
        JsonPropertyName("duration_ms")
    ]
        TimeSpan Duration
) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.RunFinished;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Run identity announcement: runId, runDir, git, env, runtime, server-config plan.
/// Emitted once per run via <see cref="SetupEventBus"/> immediately after
/// run-metadata.json is written. The runner stores it on TestRunState and
/// surfaces it to the UI; the runner-side artifact writer uses RunDir to know
/// where to write summary.json. The payload is carried as a verbatim
/// <see cref="JsonElement"/> on the wire so the producer's typed DTO does not
/// have to be shared with the consumer.
/// </summary>
public sealed record RunMetadataEvent(string RunDir, JsonElement Data) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.RunMetadata;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Per-test flakiness over the last 20 runs. Emitted once per run from the test
/// child process. Each entry has shape <c>{ test, failRate, recentRuns }</c>.
/// </summary>
public sealed record FlakyTestsEvent(JsonElement Tests) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.FlakyTests;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
