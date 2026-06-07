---
paths:
  - "mod/**/*.cs"
---

# SMAPI API surface gotchas — SemanticVersion namespace, throws-vs-TryParse, Constants types

The concrete `SemanticVersion` class lives in namespace **`StardewModdingAPI.Toolkit`**, NOT `StardewModdingAPI`. Mod code calling it needs `using StardewModdingAPI.Toolkit;` or full qualification — the bare `StardewModdingAPI` using does not bring it in.

- `new SemanticVersion(string)` **throws `FormatException`** on a non-standard/unparseable tag. `static bool TryParse(string, out ISemanticVersion)` does NOT throw — use it when parsing untrusted release tags (e.g. GitHub release names).
- `StardewModdingAPI.Constants.GamePath` → `public static string`. `StardewModdingAPI.Constants.ApiVersion` → `public static ISemanticVersion`. Don't assume both are `ISemanticVersion`.
- `ISemanticVersion` members: `MajorVersion`/`MinorVersion`/`PatchVersion` (int), `IsNewerThan`/`IsOlderThan`/`IsBetween`, `Equals(ISemanticVersion)`, `IsPrerelease`.
- `ModResolver` skips the `MinimumApiVersion` gate when the manifest field is null (`mod.Manifest.MinimumApiVersion?.IsNewerThan(apiVersion) == true`), so omitting `MinimumApiVersion` from `manifest.json` is safe — the mod loads on any API version.

(Verified against the on-disk SMAPI 4.x DLLs/XML docs under `GAME_PATH/smapi-internal/`.)

**How to apply:** When parsing SMAPI/release versions in mod code, import `StardewModdingAPI.Toolkit` and prefer `TryParse` for input you don't control — `new SemanticVersion("v1.2")` throws at runtime, and `TryParse` won't even resolve without the Toolkit using.
