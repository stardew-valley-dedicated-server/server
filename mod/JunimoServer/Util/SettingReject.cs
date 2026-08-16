using System.Collections.Generic;
using Newtonsoft.Json;

namespace JunimoServer.Util;

/// <summary>
/// A settings value a converter could not parse, replaced by the type's default. The loader threads
/// a collection of these through the serializer context, drains it after a load, and warns about each.
/// </summary>
/// <param name="Path">JSON path of the offending property, e.g. <c>Server.CabinStrategy</c>.</param>
/// <param name="RejectedValue">The value the file carried, human-readable.</param>
/// <param name="FallbackUsed">The default the converter applied instead.</param>
public sealed record SettingReject(string Path, string RejectedValue, string FallbackUsed)
{
    /// <summary>
    /// Records a rejected value against the sink threaded through <see cref="JsonSerializer.Context"/>
    /// for the loader to warn about. With no sink (e.g. an API body, which parses best-effort) it is a
    /// no-op; the converter returns its default either way, so parsing never throws.
    /// </summary>
    public static void Record(
        JsonSerializer serializer,
        string path,
        string rejectedValue,
        string fallbackUsed
    )
    {
        if (serializer.Context.Context is ICollection<SettingReject> sink)
        {
            sink.Add(new SettingReject(path, rejectedValue, fallbackUsed));
        }
    }

    /// <summary>
    /// Records the current token as unparseable and consumes it: <see cref="JsonReader.Skip"/> reads a
    /// nested object/array to its end (a no-op on a scalar) so it can't leave the reader mid-structure
    /// and break the next property. See <see cref="Record"/> for the sink semantics.
    /// </summary>
    public static void RecordToken(
        JsonReader reader,
        JsonSerializer serializer,
        string fallbackUsed
    )
    {
        var path = reader.Path;
        var described = Describe(reader);
        reader.Skip();
        Record(serializer, path, described, fallbackUsed);
    }

    /// <summary>Readable form of the current token for a warning: the scalar value it carries, or a
    /// word for a value that has none (an object, an array, a null).</summary>
    private static string Describe(JsonReader reader) =>
        reader.Value?.ToString()
        ?? reader.TokenType switch
        {
            JsonToken.StartObject => "an object",
            JsonToken.StartArray => "an array",
            JsonToken.Null => "null",
            _ => reader.TokenType.ToString(),
        };
}
