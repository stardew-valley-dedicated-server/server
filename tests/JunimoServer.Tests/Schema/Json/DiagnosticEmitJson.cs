using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Schema.Json;

/// <summary>
/// Serialization for diagnostic event streams — <c>infrastructure.jsonl</c>,
/// <c>llm.jsonl</c>, live WebSocket <c>evt</c> frames, IPC events on the
/// SetupEventBus pipe. Compact (one event per JSON line), camelCase (TS
/// consumers), omit nulls.
///
/// <para>Owns both raw object emit and the typed
/// <see cref="IRendererEvent"/> helpers. Records that need a non-camelCase
/// wire shape (xUnit lifecycle events use snake_case) override per-property
/// via <see cref="JsonPropertyNameAttribute"/>.</para>
/// </summary>
public static class DiagnosticEmitJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Untyped emit for ad-hoc anonymous-object events.</summary>
    public static string Serialize(object? value)
        => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Serialize an already-built <see cref="JsonNode"/> tree (used by
    /// pass-through forwarders that mutate a parsed JSON object before
    /// emission).
    /// </summary>
    public static string Serialize(JsonNode? node)
        => node is null ? "null" : node.ToJsonString(Options);

    /// <summary>
    /// Typed emit for <see cref="IRendererEvent"/> records. Encodes
    /// <c>evt.GetType()</c> so derived records keep their wire-format
    /// polymorphism.
    /// </summary>
    public static string Serialize<T>(T evt) where T : IRendererEvent
        => JsonSerializer.Serialize(evt, evt!.GetType(), Options);

    public static byte[] SerializeToUtf8Bytes<T>(T evt) where T : IRendererEvent
        => JsonSerializer.SerializeToUtf8Bytes(evt, evt!.GetType(), Options);

    public static JsonElement SerializeToElement(object? value)
        => JsonSerializer.SerializeToElement(value, Options);

    public static T? Deserialize<T>(JsonElement el) => el.Deserialize<T>(Options);
}
