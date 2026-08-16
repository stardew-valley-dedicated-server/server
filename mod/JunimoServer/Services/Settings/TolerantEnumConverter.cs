using System;
using JunimoServer.Util;
using Newtonsoft.Json;

namespace JunimoServer.Services.Settings;

/// <summary>
/// Reads an enum setting case-insensitively and writes it back as its member name (so files stay
/// <c>"CabinStack"</c>, never <c>0</c>). An unrecognized name, an out-of-range number, or any other
/// token falls back to member 0 and (during a settings load) is recorded for a warning
/// (<see cref="SettingReject.Record"/>) — parsing never throws.
/// An <b>absent</b> key never reaches here, so the property initializer keeps covering missing keys.
/// </summary>
public class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum ReadJson(
        JsonReader reader,
        Type objectType,
        TEnum existingValue,
        bool hasExistingValue,
        JsonSerializer serializer
    )
    {
        var fallback = default(TEnum);

        switch (reader.TokenType)
        {
            case JsonToken.String:
                var raw = (string)reader.Value!;
                if (
                    Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
                    && Enum.IsDefined(typeof(TEnum), parsed)
                )
                {
                    return parsed;
                }
                SettingReject.Record(serializer, reader.Path, raw, fallback.ToString());
                return fallback;

            // In-range integers arrive as long; an out-of-Int64 one arrives as BigInteger and falls
            // to default (Convert.ToInt64 would throw on it). Enum.ToObject accepts an out-of-range long.
            case JsonToken.Integer when reader.Value is long number:
                var asEnum = (TEnum)Enum.ToObject(typeof(TEnum), number);
                if (Enum.IsDefined(typeof(TEnum), asEnum))
                {
                    return asEnum;
                }
                SettingReject.Record(
                    serializer,
                    reader.Path,
                    number.ToString(),
                    fallback.ToString()
                );
                return fallback;

            default:
                SettingReject.RecordToken(reader, serializer, fallback.ToString());
                return fallback;
        }
    }

    public override void WriteJson(JsonWriter writer, TEnum value, JsonSerializer serializer)
    {
        writer.WriteValue(value.ToString());
    }
}
