using System.Text;
using System.Text.Json;

namespace Diagnostics;

/// <summary>
/// Trim-safe JSON reads over raw response strings. Uses <see cref="JsonDocument"/> (no reflection),
/// so it survives PublishTrimmed. Every reader tolerates malformed input by returning a neutral
/// value — the tool must never throw on a partial or unexpected server response.
/// </summary>
internal static class Json
{
    public static readonly JsonDocumentOptions ManifestOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Re-indents a JSON string for readable embedding; returns it unchanged if unparseable.</summary>
    public static string Pretty(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            using var stream = new MemoryStream();
            using (
                var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })
            )
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>A top-level string field of <paramref name="raw"/>, or null if absent/unparseable.</summary>
    public static string? String(string? raw, string field)
    {
        if (raw == null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty(field, out var value))
            {
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : value.ToString();
            }
        }
        catch
        {
            // fall through to null
        }
        return null;
    }

    /// <summary>A top-level integer field, or null if absent/non-numeric/unparseable.</summary>
    public static long? Long(string? raw, string field)
    {
        if (raw == null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (
                doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var n)
            )
            {
                return n;
            }
        }
        catch
        {
            // fall through to null
        }
        return null;
    }

    /// <summary>A top-level boolean field; a missing/non-bool field reads as false.</summary>
    public static bool Bool(string? raw, string field)
    {
        if (raw == null)
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A cloned top-level array field (usable after the document is disposed), or null.</summary>
    public static JsonElement? Array(string? raw, string field)
    {
        if (raw == null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (
                doc.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.Array
            )
            {
                return value.Clone();
            }
        }
        catch
        {
            // fall through to null
        }
        return null;
    }

    /// <summary>A field of an already-parsed element as display text; "" if absent (numbers stringify).</summary>
    public static string Field(JsonElement element, string field)
    {
        if (
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(field, out var value)
        )
        {
            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : value.ToString();
        }
        return "";
    }

    /// <summary>A boolean field of an already-parsed element; missing/non-bool reads as false.</summary>
    public static bool FieldBool(JsonElement element, string field) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.True;
}
