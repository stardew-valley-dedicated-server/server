using System.Text;
using System.Text.Json;

namespace Diagnostics;

/// <summary>
/// Field reads over an already-parsed <see cref="JsonElement"/>. Every reader tolerates an absent
/// field or an Undefined element — the element a failed <see cref="ApiRead"/> carries — by returning
/// a neutral value, so one unexpected field degrades to an empty cell instead of losing the whole
/// section, which is why the report reads elements rather than deserializing typed responses like
/// the test suite's ServerApiClient does. Being reflection-free also survives PublishTrimmed
/// (docker/Dockerfile publishes this tool with it).
/// </summary>
internal static class Json
{
    /// <summary>Re-indents an element for readable embedding in the report.</summary>
    public static string Pretty(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            element.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A field as display text; "" if absent (numbers and booleans stringify).</summary>
    public static string Field(JsonElement element, string field) =>
        Property(element, field) is { } value
            ? value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.ToString()
            : "";

    /// <summary>A boolean field; missing, null, or non-boolean reads as false.</summary>
    public static bool FieldBool(JsonElement element, string field) =>
        Property(element, field)?.ValueKind == JsonValueKind.True;

    /// <summary>A boolean field as a tri-state: null when absent, JSON null, or non-boolean.</summary>
    public static bool? FieldNullableBool(JsonElement element, string field) =>
        Property(element, field)?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    /// <summary>An integer field, or null if absent or non-numeric.</summary>
    public static long? FieldLong(JsonElement element, string field) =>
        Property(element, field) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>A field's array items; empty when the field is absent or isn't an array.</summary>
    public static IReadOnlyList<JsonElement> FieldArray(JsonElement element, string field) =>
        Property(element, field) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray().ToList()
            : Array.Empty<JsonElement>();

    private static JsonElement? Property(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value)
            ? value
            : null;
}
