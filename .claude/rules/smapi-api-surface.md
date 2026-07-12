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

## No public API to invoke a console command — `ICommandHelper` has only `Add`

`IModHelper.ConsoleCommands` (`ICommandHelper`) exposes **only `Add(name, doc, callback)`** in SMAPI 4.4 — there is **no public `Trigger`/`Run`** to invoke a registered console command programmatically. The commented-out `mod/JunimoServer/Services/Commands/ConsoleCommand.cs` references `helper.ConsoleCommands.Trigger(...)`, which would not compile — treat it as stale, not as proof the API exists. To drive a console command (e.g. from a test-only `/test/*` endpoint, to exercise command-only logic with no HTTP path), reflect into SMAPI internals: `SCore.Instance` (static internal prop) → `CommandManager` (internal field) → `Get(name)` (public, returns the `Command`) → `Command.Callback` (public `Action<string,string[]>`), then invoke `callback(name, args)`. The callback runs on the calling thread — matching real console behaviour (off the game thread), so the command marshals to the game thread itself if it needs to. Gate any such reflection to `Env.IsTest`; a future SMAPI may rename these internals (a test-only break, never production).

(Verified against the on-disk SMAPI 4.x DLLs/XML docs under `GAME_PATH/smapi-internal/`.)

**How to apply:** When parsing SMAPI/release versions in mod code, import `StardewModdingAPI.Toolkit` and prefer `TryParse` for input you don't control — `new SemanticVersion("v1.2")` throws at runtime, and `TryParse` won't even resolve without the Toolkit using.
