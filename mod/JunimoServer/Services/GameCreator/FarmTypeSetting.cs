using Newtonsoft.Json;
using System;

namespace JunimoServer.Services.GameCreator
{
    /// <summary>
    /// A farm-type selector that is either a vanilla index (0-6) or a modded farm
    /// <c>Id</c> string (the <c>Data/AdditionalFarms</c> <c>Id</c> field). Mirrors how
    /// the game itself identifies farms: <c>whichFarm</c> is a bucket (0-6 vanilla,
    /// 7 = "consult <c>whichModFarm</c>") and modded farms are resolved by Id, never by
    /// position (see <see cref="GameCreatorService.ResolveFarmType"/>).
    ///
    /// Serializes back to the original scalar (a JSON number for an index, a JSON string
    /// for an Id) so existing <c>"FarmType": 3</c> configs round-trip unchanged.
    /// </summary>
    [JsonConverter(typeof(FarmTypeSettingConverter))]
    public readonly struct FarmTypeSetting
    {
        /// <summary>
        /// The <c>whichFarm</c> bucket value the game uses for any Data/AdditionalFarms farm
        /// (vanilla maps are 0-6; 7 means "look at whichModFarm"). Also the count of vanilla maps.
        /// </summary>
        public const int FirstModdedIndex = 7;

        /// <summary>
        /// Data/AdditionalFarms Id of the base-game Meadowlands farm. It ships with vanilla, so
        /// it's treated as a built-in: the index <c>7</c> is a permanent alias for this Id (see
        /// <see cref="GameCreatorService.ResolveFarmType"/>), independent of any installed mods.
        /// </summary>
        public const string MeadowlandsFarmId = "MeadowlandsFarm";

        /// <summary>
        /// The numeric index that aliases <see cref="MeadowlandsFarmId"/>. It's the modded
        /// bucket itself (7) — Meadowlands is the one AdditionalFarms farm with a number.
        /// </summary>
        public const int MeadowlandsIndex = FirstModdedIndex;

        /// <summary>
        /// Convenience keyword (case-insensitive) selecting the first installed mod farm — i.e.
        /// the first <c>Data/AdditionalFarms</c> entry that isn't the base-game Meadowlands. Lets
        /// an operator with a single farm mod avoid looking up its Id. Order isn't guaranteed
        /// across mod changes, so it's a best-effort convenience, not a stable selector.
        /// </summary>
        public const string FirstModFarmKeyword = "modded";

        /// <summary>Vanilla farm index (0-6) when this is an index selector, else null.</summary>
        public int? Index { get; }

        /// <summary>Modded farm Id when this is an Id selector, else null.</summary>
        public string? Id { get; }

        /// <summary>True when this selects a modded farm by Id.</summary>
        public bool IsModded => Id != null;

        /// <summary>True when this is the <see cref="FirstModFarmKeyword"/> selector (case-insensitive).</summary>
        public bool IsFirstModFarmKeyword =>
            Id != null && string.Equals(Id, FirstModFarmKeyword, StringComparison.OrdinalIgnoreCase);

        private FarmTypeSetting(int? index, string? id)
        {
            Index = index;
            Id = id;
        }

        public static FarmTypeSetting FromIndex(int index) => new(index, null);
        public static FarmTypeSetting FromId(string id) => new(null, id);

        /// <summary>Default selector: vanilla Standard farm (index 0).</summary>
        public static FarmTypeSetting Default => FromIndex(0);

        /// <summary>
        /// Admin-facing display names for the vanilla indices 0-6. "Four Corners" matches the
        /// game's farm-type key only after whitespace is stripped (<c>Game1.GetFarmTypeKey()</c>
        /// returns "FourCorners"); <see cref="TryGetVanillaIndex"/> accepts either spelling.
        /// </summary>
        private static readonly string[] VanillaNames =
            { "Standard", "Riverland", "Forest", "Hilltop", "Wilderness", "Four Corners", "Beach" };

        /// <summary>Friendly display name for a vanilla farm index (0-6), or null if out of range.</summary>
        public static string? VanillaName(int index) =>
            index >= 0 && index < VanillaNames.Length ? VanillaNames[index] : null;

        /// <summary>
        /// Maps a vanilla farm name to its index (0-6), case- and whitespace-insensitive, so
        /// "Standard", "fourcorners", and "Four Corners" all resolve. Returns false for any
        /// non-vanilla name (e.g. a mod farm Id).
        /// </summary>
        public static bool TryGetVanillaIndex(string name, out int index)
        {
            for (var i = 0; i < VanillaNames.Length; i++)
            {
                if (NameKey(VanillaNames[i]) == NameKey(name))
                {
                    index = i;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        /// <summary>
        /// Human-readable label for this selector: the built-in name for a known farm (index
        /// 0-6, index 7 / the Meadowlands Id → "Meadowlands"), or the raw Id for a mod farm.
        /// Display only — does not validate that a mod Id exists in Data/AdditionalFarms.
        /// </summary>
        public string DisplayName()
        {
            if (Id != null)
                return NameKey(Id) == NameKey(MeadowlandsFarmId) ? "Meadowlands" : Id;
            var index = Index ?? 0;
            return VanillaName(index)
                ?? (index == MeadowlandsIndex ? "Meadowlands" : index.ToString());
        }

        private static string NameKey(string s) => s.Replace(" ", "").ToLowerInvariant();

        /// <summary>The original scalar form (index as a number string, or the Id).</summary>
        public override string ToString() => Id ?? Index?.ToString() ?? "0";
    }

    /// <summary>
    /// Reads <see cref="FarmTypeSetting"/> from a JSON number or string and writes it back to
    /// the same scalar form. Parsing is total — an out-of-range index or unknown Id is NOT
    /// rejected here; that's a domain decision handled (with a graceful Standard fallback and a
    /// warning) by <see cref="GameCreatorService.ResolveFarmType"/>. Throwing here would abort
    /// the whole settings load and discard every other setting, which is the wrong failure mode
    /// for one bad field — every sibling setting in ServerSettingsLoader degrades gracefully too.
    /// A quoted integer (<c>"3"</c>) is treated as an index, not an Id.
    /// </summary>
    public class FarmTypeSettingConverter : JsonConverter<FarmTypeSetting>
    {
        public override FarmTypeSetting ReadJson(JsonReader reader, Type objectType,
            FarmTypeSetting existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Integer:
                    // Out-of-int values fall through to an out-of-range index, which the resolver maps
                    // to Standard with a warning — parsing stays total (see class summary).
                    return int.TryParse(reader.Value?.ToString(), out var jsonIndex)
                        ? FarmTypeSetting.FromIndex(jsonIndex)
                        : FarmTypeSetting.FromIndex(int.MaxValue);

                case JsonToken.String:
                    var raw = (string)reader.Value!;
                    return int.TryParse(raw, out var parsedIndex)
                        ? FarmTypeSetting.FromIndex(parsedIndex)
                        : FarmTypeSetting.FromId(raw);

                default:
                    throw new JsonSerializationException(
                        $"FarmType must be a JSON number or string, but got {reader.TokenType}.");
            }
        }

        public override void WriteJson(JsonWriter writer, FarmTypeSetting value, JsonSerializer serializer)
        {
            if (value.Id != null)
                writer.WriteValue(value.Id);
            else
                writer.WriteValue(value.Index ?? 0);
        }
    }
}
