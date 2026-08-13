# Typed enums in ServerSettings, via one tolerant converter

Give `ServerSettings` fields their real enum types instead of `string`, and delete the three hand-rolled `Parse*` helpers in `ServerSettingsLoader`. Secondary win: a typo in one of these fields currently falls back **silently** — the conversion makes it warn.

## Today

Three settings in `ServerSettings.cs` are enum-valued but declared `string`, each with a private `Parse*` fallback in `ServerSettingsLoader.cs`:

| Setting | Enum | Declared default | 0-member |
|---|---|---|---|
| `Server.CabinStrategy` | `CabinStrategy` | `"CabinStack"` | `CabinStack` |
| `Server.ExistingCabinBehavior` | `ExistingCabinBehavior` | `"KeepExisting"` | `KeepExisting` |
| `Server.LobbyMode` | `LobbyMode` | `"Shared"` | `Shared` |

Every consumer already binds to the loader's typed accessor (`ServerSettingsLoader.CabinStrategy` etc.), never to the raw string — so the conversion is contained to the POCO, the loader, and the one API DTO below.

All three declare their fallback as member 0, so `default(TEnum)` is the right fallback for every field and the converter needs no per-property default.

## Why this is not just changing the field type

Newtonsoft reads enum names from JSON case-insensitively with no converter at all, so the naive change compiles and appears to work. Three things break:

1. **It writes back as an integer.** `ServerSettingsLoader.SaveToFile` uses plain `JsonConvert.SerializeObject`, which serializes enums as numbers. A newly created `server-settings.json` would carry `"CabinStrategy": 0`, contradicting the docs and every operator's muscle memory.
2. **An unknown name throws, and the catch discards the whole file.** Newtonsoft raises `JsonSerializationException` on an unrecognized enum name. That lands in `LoadOrCreate`'s catch, which logs at `LogLevel.Error`, then falls through to `new ServerSettings()` + `SaveToFile(defaults)` — **overwriting** the operator's farm name, admin ids and every other setting because one field was misspelled. The `Error` line is also E2E test poison (`rules/debugging.md`).
3. **Today's fallbacks are silent.** None of the four `Parse*` helpers log when they reject a value, so a typo'd `CabinStrategy` quietly becomes `CabinStack` and the operator has no clue. `SettingsCommand`'s `Enum.IsDefined` checks can never report FAIL, because they run on the already-normalized loader accessor — dead validation branches that this change should either make live or delete.

## Precedent to follow

`GameSettings.FarmType` is **already** a typed value in this same POCO, handled by `FarmTypeSettingConverter` (`Services/GameCreator/FarmTypeSetting.cs`). Its class docstring states the rule this plan generalizes:

> Parsing is total — an out-of-range index or unknown Id is NOT rejected here [...] Throwing here would abort the whole settings load and discard every other setting, which is the wrong failure mode for one bad field.

So the target shape is settled: a **total** converter that never throws, writes the scalar back in its human form, and leaves the domain fallback to a place that can warn about it.

## Design — `TolerantEnumConverter<TEnum>`

One generic `JsonConverter<TEnum>` beside the settings types.

- **`ReadJson`** — `Enum.TryParse(ignoreCase: true)`. On an unparseable string, a number outside the defined members, or any other token type: return the configured default and warn, naming the field, the bad value and the value used instead. Never throw — hazard 2.
- **`WriteJson`** — write `value.ToString()` (the member name), so files stay `"CabinStack"` rather than `0` — hazard 1.
- **Missing keys are not the converter's problem** — an **absent** key never invokes it, so the property initializer keeps covering missing keys exactly as `= "CabinStack"` does today. Only the *present-but-invalid* path reaches `ReadJson`.
- **Warning channel** — the converter runs inside `JsonConvert.DeserializeObject` with no `IMonitor` in scope. Simplest workable shape: the converter collects rejects into a list the loader drains and logs right after `LoadOrCreate`, so the warning names the file. Decide this when implementing; do not reach for a static monitor.

## Files

- `Services/Settings/TolerantEnumConverter.cs` — new.
- `Services/Settings/ServerSettings.cs` — three fields retyped + `[JsonConverter]`; add `using JunimoServer.Services.CabinManager`. Watch the `LobbyMode LobbyMode` same-name-as-type property: legal C# (color-color), but the loader already qualifies it as `Settings.LobbyMode` and should keep doing so.
- `Services/Settings/ServerSettingsLoader.cs` — delete `ParseCabinStrategy`, `ParseExistingCabinBehavior`, `ParseLobbyMode`; their three accessors become pass-throughs. Drain and log the converter's rejects.
- `Services/Api/ApiService.cs` — `HandleGetSettings` feeds `GameSettingsInfo.SpawnMonstersAtNight` and `ServerRuntimeSettingsInfo.CabinStrategy` / `.ExistingCabinBehavior`, all `string` on a published contract. Assign `.ToString()` to keep the wire format byte-identical.
- `Services/Commands/SettingsCommand.cs` — resolve the dead `Enum.IsDefined` branches in its validate path.
- `docs/admins/configuration/server-settings.md` — only if the accepted-value prose changes; it should not.

## Not in scope (raise separately)

- **`Game.SpawnMonstersAtNight`** (`"auto"`/`"true"`/`"false"`, read by `ParseNullableBool`). Tri-state-with-a-sentinel-word, not an enum; converting it to `bool?` changes the documented config surface (`"auto"` → `null`) and the API DTO's documented values. Recommendation: leave as `string`.
- **Typing the API DTOs themselves.** Would change the published OpenAPI schema and needs a generator special-case, as `FarmTypeSetting` already has in `OpenApiGenerator`. Keep DTOs `string` and convert at the boundary.

## Runtime gates

Per `runtime-post-conditions-are-gates` — none of these are closed by a green build.

1. **A bogus value keeps every other setting.** Put `"CabinStrategy": "Nonsense"` in a committed-shape `server-settings.json`; the server starts, uses CabinStack, logs one `Warn` naming the field, and the file's other settings are intact and **not** overwritten with defaults. This is the hazard-2 regression test.
2. **Round-trip stays human.** Delete the settings file, let the server recreate it, confirm the three fields read `"CabinStack"` / `"KeepExisting"` / `"Shared"` — not integers.
3. **Case-insensitivity survives.** `"cabinstack"` still parses (today's `ignoreCase: true` behavior).
4. **`GET /settings` is unchanged.** Byte-compare the response against a pre-change capture.
5. **The harness still boots.** `ServerContainer.BuildSettingsFileBytes` writes these as strings; a full E2E class must pass unchanged.

## Ordering

1. `TolerantEnumConverter<TEnum>` + the reject-collection seam.
2. Retype the three fields; delete the three parsers; fix the API boundary.
3. `SettingsCommand` dead-branch cleanup.
4. Gates 1-4 locally, then gate 5.

## Related

- `rules/universal/verify-claims.md` — gate 1 must run against the repo's committed settings shape, not just the example.
- `rules/debugging.md` — the fallback logs `Warn`; an `Error` line cancels E2E runs.
- `Services/GameCreator/FarmTypeSetting.cs` — the in-tree converter this generalizes.
