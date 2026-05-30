using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// A setup phase has started (e.g., "Building Images", "Starting Containers").
/// </summary>
public sealed record SetupPhaseStartedEvent(
    string Category,
    [property: JsonPropertyName("phase")] string PhaseName,
    [property: JsonPropertyName("collection")] string? CollectionName = null) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.SetupStarted;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A setup phase has completed.
/// </summary>
public sealed record SetupPhaseCompletedEvent(
    string Category,
    [property: JsonPropertyName("phase")] string PhaseName,
    bool Success,
    [property: JsonPropertyName("error")] string? ErrorMessage = null,
    [property: JsonPropertyName("collection")] string? CollectionName = null) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.SetupCompleted;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// A step within a setup phase (e.g., "Building server image", "Starting steam-auth container").
/// </summary>
public sealed record SetupStepEvent(
    string Category,
    [property: JsonPropertyName("step")] string StepName,
    [property: JsonConverter(typeof(SetupStepStatusJsonConverter))]
    SetupStepStatus Status,
    string? Details = null,
    [property: JsonPropertyName("collection")] string? CollectionName = null) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.SetupStep;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Serializes <see cref="SetupStepStatus"/> as snake_case (e.g. <c>in_progress</c>) —
/// the wire format every consumer reads.
/// </summary>
public sealed class SetupStepStatusJsonConverter : JsonConverter<SetupStepStatus>
{
    public override SetupStepStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s switch
        {
            "started" => SetupStepStatus.Started,
            "in_progress" => SetupStepStatus.InProgress,
            "completed" => SetupStepStatus.Completed,
            "failed" => SetupStepStatus.Failed,
            "warning" => SetupStepStatus.Warning,
            _ => SetupStepStatus.Started,
        };
    }

    public override void Write(Utf8JsonWriter writer, SetupStepStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToSnakeCase());
    }
}
