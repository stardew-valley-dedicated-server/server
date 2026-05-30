using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Events;

/// <summary>
/// Diagnostic / log message from test execution. Emitted by xUnit forwarders;
/// snake_case wire (source, level, message).
/// </summary>
public sealed record DiagnosticEvent(
    [property: JsonPropertyName("source")] LogSource Source,
    [property: JsonPropertyName("level")] LogLevel Level,
    [property: JsonPropertyName("message")] string Message) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.Diagnostic;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Error message from test infrastructure. Snake_case wire on stack_trace.
/// </summary>
public sealed record ErrorEvent(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("stack_trace")] string? StackTrace = null) : IRendererEvent
{
    [JsonPropertyName("event")]
    public string EventName => EventNames.Error;

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
