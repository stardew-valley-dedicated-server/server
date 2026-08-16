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

## SMAPI's built-in commands are NOT in `CommandManager` during mod `Entry`

SMAPI registers its own console commands (`help`, `harmony_summary`, …) when its console input
starts — after `LoadMods`, so after every mod's `Entry`. Enumerating `CommandManager.GetAll()`
from `Entry` therefore returns zero built-ins and misses any mod that loads later, with no error —
the registry is simply still empty of them, so the reflection "succeeds" and the gap is invisible
until something reads the output (a catalog written at `Entry` shipped without `help`; only a
failing E2E content assertion exposed it). `GameLaunched` (first update tick) is the earliest
mod-visible event at which built-ins and all mods' `Entry`-time commands are present —
`CommandCatalogFile` writes at `Entry` for early availability and re-writes on `GameLaunched` and
`SaveLoaded` for exactly this reason.

**How to apply:** When parsing SMAPI/release versions in mod code, import `StardewModdingAPI.Toolkit` and prefer `TryParse` for input you don't control — `new SemanticVersion("v1.2")` throws at runtime, and `TryParse` won't even resolve without the Toolkit using.
