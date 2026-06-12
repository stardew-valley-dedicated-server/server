using System.Text.Json;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Test-harness mirror of the mod's farm-type selector: either a vanilla index (0-6)
/// or a modded farm <c>Id</c> string (the <c>Data/AdditionalFarms</c> <c>Id</c>). The
/// test project does not reference the mod assembly, so this is a parallel type that
/// carries the same union over the wire (server-settings.json, the <c>/newgame</c> body,
/// and the <c>/settings</c> response).
///
/// Implicit conversions from <see cref="int"/> and <see cref="string"/> let call sites pass
/// a bare index (<c>farmType: 0</c>) or Id (<c>farmType: "MeadowlandsFarm"</c>) directly.
/// </summary>
[JsonConverter(typeof(FarmTypeSettingJsonConverter))]
public readonly struct FarmTypeSetting
{
    public int? Index { get; }
    public string? Id { get; }
    public bool IsModded => Id != null;

    private FarmTypeSetting(int? index, string? id)
    {
        Index = index;
        Id = id;
    }

    public static FarmTypeSetting FromIndex(int index) => new(index, null);

    public static FarmTypeSetting FromId(string id) => new(null, id);

    /// <summary>
    /// Builds a selector from a boxed <c>[InlineData]</c> argument (an <see cref="int"/> index
    /// or a <see cref="string"/> Id), since theory arguments can't be a custom struct.
    /// </summary>
    public static FarmTypeSetting FromObject(object value) =>
        value switch
        {
            int index => FromIndex(index),
            string id => FromId(id),
            _ => throw new ArgumentException(
                $"Farm type must be an int index or string Id, got {value?.GetType().Name ?? "null"}."
            ),
        };

    public static implicit operator FarmTypeSetting(int index) => FromIndex(index);

    public static implicit operator FarmTypeSetting(string id) => FromId(id);

    /// <summary>
    /// The scalar value as it goes onto the wire: a boxed <see cref="int"/> for an index
    /// or a <see cref="string"/> for an Id. Both Newtonsoft and System.Text.Json serialize
    /// a boxed int/string to the correct JSON scalar.
    /// </summary>
    public object ToJsonValue() => Id ?? (object)(Index ?? 0);

    /// <summary>Stable scalar string for cache keys and display labels.</summary>
    public override string ToString() => Id ?? (Index ?? 0).ToString();
}

/// <summary>
/// Reads/writes <see cref="FarmTypeSetting"/> as a JSON number (index) or string (Id),
/// matching the mod's scalar wire form. Used when deserializing the <c>/settings</c> response.
/// </summary>
public class FarmTypeSettingJsonConverter : JsonConverter<FarmTypeSetting>
{
    public override FarmTypeSetting Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => FarmTypeSetting.FromIndex(reader.GetInt32()),
            JsonTokenType.String => FarmTypeSetting.FromId(reader.GetString()!),
            _ => throw new JsonException(
                $"FarmType must be a number or string, got {reader.TokenType}."
            ),
        };

    public override void Write(
        Utf8JsonWriter writer,
        FarmTypeSetting value,
        JsonSerializerOptions options
    )
    {
        if (value.Id != null)
            writer.WriteStringValue(value.Id);
        else
            writer.WriteNumberValue(value.Index ?? 0);
    }
}
