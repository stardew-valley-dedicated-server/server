# Fix: build SMAPI from source to patch the one-second divisor (+ suppress the XACT log line)

**Status:** validation
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** docker, server
**Related:** none
**Observed:** every boot at `SERVER_TPS=5`: the three `OnOneSecond*` handlers fire every 12s, and the health-check probe interval runs at 12× `HealthCheckSeconds`
**Next step:** sign off on building SMAPI from source (LGPL notice, six install sites) before the patch series is written

> The same disease (per-tick constants assume 60 TPS) in *game* code — fades, movement,
> projectiles, debris — is already fixed mod-side by `TpsAgnosticPacing`
> (`mod/JunimoServer.Shared/TpsAgnosticPacing.cs`, gated on `SDVD_TPS_AGNOSTIC_PACING`),
> which needs no SMAPI build. This plan covers the part that does.

## Symptom

SMAPI fires `GameLoop.OneSecondUpdateTicked` on `SCore.TicksElapsed % 60 == 0`, and
`TicksElapsed` increments once per MonoGame `Update`
([`SCore.cs`](https://github.com/Pathoschild/SMAPI/blob/develop/src/SMAPI/Framework/SCore.cs)).
`ServerOptimizer` pins `Game1.game1.TargetElapsedTime = 1000/Env.ServerTps` ms, so at the
proven-stable `SERVER_TPS=5` the loop runs 5 ticks/sec and the event fires every **60/5 = 12s**, not
1s. The cadence is SMAPI-internal and cannot be made per-second from mod code without patching SMAPI
(raising TPS to 60 is ruled out by `server-tps-headless.md`). This is the trap already documented in
`.claude/rules/one-second-update-ticked-fires-per-game-tick.md`.

Three services subscribe and all run ~12× slower than their "one second" naming implies:

| Handler | File | Impact at 12s |
|---|---|---|
| `AlwaysOnServer.OnOneSecondUpdateTicked` | `AlwaysOn.cs` | Player-facing progression lags (shipping menu, CC unlock, pet choice) up to 12s |
| `GameManagerService.OnOneSecondTicked` | `GameManagerService.cs` | `/newgame`+`/reload` startup polling lags; **+ healthcheck interval bug, below** |
| `MapService.OnOneSecondUpdateTick` | `MapService.cs` | Portrait PNG refresh every 12s (cosmetic) |

**Latent bug surfaced (GameManager):** `RunHealthCheck` (`GameManagerService.cs`) self-decrements
`_healthCheckTimer` once per fire and reloads it with `Env.HealthCheckSeconds` (default **300**,
`Env.cs`). Because the handler fires every 12s rather than 1s, the routine probe interval is
actually `300 × 12 = 3600s` (1 hour), not 300s. (The "unreachable 2+ min → exit" backstop in the same method
uses real `DateTime.Now` and is unaffected.) Fixing the event's cadence at the source fixes this
**for free** — the counter counts fires, and fires become 1/sec — so `_healthCheckTimer` needs no
change.

## Root cause


`src/SMAPI/Framework/SCore.cs`, method `OnPlayerInstanceUpdating`:
```csharp
bool isOneSecond = SCore.TicksElapsed % 60 == 0;   // hardcoded 60
...
if (isOneSecond) events.OneSecondUpdateTicking.RaiseEmpty();
...
if (isOneSecond) events.OneSecondUpdateTicked.RaiseEmpty();
```
`TicksElapsed` is `internal static uint`, bumped once per MonoGame `Update`. `Update`'s rate is set
by `Game1.game1.TargetElapsedTime = 1000/Env.ServerTps` (`ServerOptimizer.cs`). So at `TPS=5`,
`% 60` is satisfied every 60 ticks = 12 real seconds. Changing the divisor to the real TPS makes it
`% 5 == 0` → every 5 ticks = 1 real second. (Verified: `% 60` line + `TicksElapsed` declaration via
SMAPI 4.5.2 source; the firing math via `ServerOptimizer.cs` + SMAPI's 1-update-per-Update loop.)

## Fix

### Chosen fix: patch SMAPI's hardcoded 60-tick divisor at the source

Fix the wrong assumption baked into SMAPI: it hardcodes `60` ticks = 1 second, true only at 60 TPS.
Replace the hardcoded divisor with our configured tick rate so the event's contract (`<TPS>` ticks =
1 second = the event fires) holds at any TPS. This keeps the **real** SMAPI event honest for any
current or future subscriber — no parallel custom event, no per-handler workaround.

Rejected alternatives (all verified against the 4.5.2 DLL, Harmony 2.2.2, and the launch chain):
- **Per-handler wall-clock gates in mod code** — self-gate each handler with an inline
  `(DateTime.UtcNow - _last).TotalSeconds >= 1.0` in `OnUpdateTicked` and drop the `OneSecondUpdateTicked`
  subscription. Would work (it matches the wall-clock gates in `MapService`, `AlwaysOnFestivals`, and `ApiService`)
  but treats the symptom (three known subscribers) not the cause: the real event stays at 12s forever, and
  every future subscriber must independently remember the trap.
- **Harmony on `SCore`, prefix/postfix** — the divisor is the `flag4` *method-local* in
  `OnPlayerInstanceUpdating`; a prefix/postfix cannot rewrite a local. The only mod-reachable seam is
  `ManagedEvent.Raise`, and it fails twice: (1) it sits *downstream* of the `flag4` guard — vanilla only
  calls it on the 60th tick, so a patch there can suppress extra fires but cannot *synthesize* the fires
  missing at ticks 5/10/…/55; (2) `ManagedEvent<T>` is generic and `OneSecondUpdateTickedEventArgs` is a
  reference type, so per Harmony's own docs all reference-type instantiations share one JIT'd `Raise`
  body — patching it would fire on `UpdateTicked`/`Saving`/`DayStarted`/etc. too, uncontrollable without
  an `EventName` check on a per-raise hot path.
- **Harmony transpiler on `SCore.OnPlayerInstanceUpdating`** — *technically* reachable (`SCore` is
  non-generic, the method is a plain instance method; the mod already Harmony-patches five `Framework.*`
  methods, so patching SMAPI internals is precedented here). But an IL transpiler matching a bare `60`
  constant inside the core update loop is the single most version-fragile patch we could own — an
  upstream re-JIT of that expression breaks the match silently. The source patch is strictly more robust
  for the same effect, and we're already building SMAPI from source for the XACT patch anyway.
- **Raising `SERVER_TPS` to 60** — contradicts `server-tps-headless.md` (5 is the proven-stable
  headless value, run by CI + `.env.test`); raises per-server CPU.

### Scope: two small SMAPI-source patches, one build change

This plan stands up an in-repo **build-SMAPI-from-source** path (replacing the prebuilt-installer
download) and carries two independent patches through it, both verified to be unreachable by mod-side
Harmony:

1. **One-second divisor** (the original bug) — `SCore` hardcodes `60` ticks = 1 second; the divisor is a
   method-local, so no prefix/postfix can reach it (the only mod-reachable seam, `ManagedEvent.Raise`,
   sits *downstream* of the guard and shares one JIT body across all reference-type events). Details below.
2. **XACT log-line suppression** — the headless build strips `Content/`, so `Game1.InitializeSounds`
   throws `FileNotFoundException` on `Content/XACT/Wave Bank.xwb`. The game **already catches this** and
   falls back to `DummyAudioEngine`/`DummySoundBank` (no functional harm), but the catch calls
   `log.Error("Game.Initialize() caught exception initializing XACT.", …)`, which SMAPI's `SGameLogger`
   prints as `[ERROR game]`. This fires during `Game.Initialize()` — **before `LoadMods`**, so a mod-side
   Harmony patch is never installed in time to intercept it, and SMAPI owns the process entry point
   (`Program.Main`), so there is no "before SMAPI" seam either. The fix is a one-`if` source patch in
   `SGameLogger.Error`, matching its existing "Error connecting to Steam." special-case. (The test harness
   is already immune — `"XACT"` is in `ServerContainer.IgnoredErrorPatterns` — so this patch is purely to
   clean the log, not to fix a failure.)

Why not a fork: the delta is two small, independent hunks in two files matching SMAPI's own patterns —
not evolving multi-file surgery. The in-repo patch-file series (below) is the right weight. Revisit a
fork only if real mod-loader/compat changes accumulate later.

### Why this needs building SMAPI from source

The repo does **not** build SMAPI today — `docker/Dockerfile` and `docker/rootfs/startapp.sh`
download the prebuilt official installer zip (`SMAPI_VERSION=4.5.2`) and run its Linux installer. The
`60` lives in compiled IL inside that zip. Any fix to it requires compiling a modified SMAPI. That
build switch — not the patch mechanism — is the bulk of the work, and is identical regardless of how we
carry the patch.

### License (verified)

SMAPI is **LGPL v3**. Forking/modifying/redistributing a modified build in a Docker image is permitted.
Weak-copyleft obligations we take on: (1) the modified SMAPI source must remain available — satisfied
trivially by checking the patches into this repo (the delta is "upstream tag 4.5.2 +
`docker/smapi-patches/`"); our mod (which only *links* SMAPI) does not inherit LGPL; (2) include the LGPL
notice. We must stop shipping the prebuilt installer zip for the modified build — we now distribute a
modified binary, and the patch series IS the disclosed source delta. **Action:** add a short `LICENSE`/`NOTICE` line in the SMAPI build
stage or repo docs pointing at upstream 4.5.2 + `docker/smapi-patches/`.

### Strategy: patch-file series applied at build time

A `git format-patch` series checked into `docker/smapi-patches/`, applied at Docker build time against a
freshly-cloned pinned upstream tag. Each patch is re-validated against the pinned upstream on every build;
a failure to apply is a hard build error (no silent drift), and a reviewer sees a normal inline diff.

### Patch 1: `0001-onesecond-tickrate-divisor.patch` (2 files)

Made via `git format-patch` against the `4.5.2` tag, touching:

1. **`src/SMAPI/Framework/SCore.cs`** — replace the hardcoded `60` with the configured divisor read
   from `SConfig`. In `OnPlayerInstanceUpdating` the divisor is the `flag4` local
   `bool flag4 = TicksElapsed % 60 == 0;` (decompiler name; source name `isOneSecond`), gating both
   `OneSecondUpdateTicking` and `OneSecondUpdateTicked`. Replace with:
   ```csharp
   int oneSecondTicks = this.Settings.OneSecondTickInterval > 0 ? this.Settings.OneSecondTickInterval : 60;
   bool isOneSecond = SCore.TicksElapsed % (uint)oneSecondTicks == 0;
   ```
   (Guard `> 0`: a 0/negative config value falls back to vanilla 60, never a divide-by-zero.
   **Verified against 4.5.2 source:** `SCore` holds config as `private readonly SConfig Settings`,
   so `this.Settings.OneSecondTickInterval` is correct.)

2. **`src/SMAPI/Framework/Models/SConfig.cs`** — add one writable property + its `DefaultValues` entry,
   mirroring the existing pattern (verified: `internal class SConfig`, `nameof`-keyed `DefaultValues`,
   "properties must be writable to support merging config.user.json into it"):
   ```csharp
   public int OneSecondTickInterval { get; set; }
   // in DefaultValues:
   [nameof(SConfig.OneSecondTickInterval)] = 60,
   ```

**Config plumbing (no SMAPI-side wiring beyond the field):** SMAPI merges `config.user.json` over
`config.json` automatically (the writable-property contract). We already ship a `config.user.json`
overlay — `docker/rootfs/startapp.sh` copies `/data/smapi-config.json` →
`smapi-internal/config.user.json` (and `Dockerfile` for the test-client). **Add the key there:**
`OneSecondTickInterval` must equal `SERVER_TPS`. Since `SERVER_TPS` is a mod-read env var (`Env.cs`)
and the SMAPI config is static JSON, keep them in sync by having `startapp.sh` inject `SERVER_TPS` into
the generated `config.user.json` (`jq`) — one env var drives both, no editable pair to drift. Mirror for
the test-client with `CLIENT_TPS`. No runtime TPS mutation exists today (only the two boot-time
`TargetElapsedTime` setters); leave a comment at the injection site that any future runtime TPS setter
must also rewrite this config value.

### Patch 2: `0002-suppress-xact-init-error-log.patch` (1 file)

Made via `git format-patch` against the `4.5.2` tag, touching one file:

**`src/SMAPI/Framework/SGameLogger.cs`** — the game's `Game1.InitializeSounds` catch calls
`log.Error("Game.Initialize() caught exception initializing XACT.", exception)`, which routes through
`SGameLogger.Error` (game logs → SMAPI monitor) and prints as `[ERROR game]`. `SGameLogger.Error`
already special-cases one game error (the "Error connecting to Steam." block); add a sibling early-return
for the XACT message. Drop it entirely (it's a stripped-content artifact on the silent headless build,
and the game's own `DummyAudioEngine` fallback already handles it):
```csharp
// in SGameLogger.Error, alongside the existing Steam special-case:
if (error == "Game.Initialize() caught exception initializing XACT.")
    return; // headless build strips Content/XACT; the game's DummyAudioEngine fallback is expected
```
(**Verified against source:** exact string match confirmed in `Game1.InitializeSounds` (`Game1.cs`); `SGameLogger.Error`'s
existing special-case pattern confirmed on the 4.5.2 DLL. No `SConfig` field needed — this is
unconditional on our build, which never ships audio banks.)

### The build path (verified against 4.5.2 source + shipped artifacts)

**Verified facts driving the build stage:**
- **TFM is `net6.0`, self-contained.** `SMAPI.csproj` (4.5.2) declares `<TargetFramework>net6.0</TargetFramework>`,
  and the shipped `StardewModdingAPI.runtimeconfig.json` has `"tfm": "net6.0"` + `includedFrameworks`
  (bundled .NET 6 runtime) + `TieredCompilation=false`. So the publish must be net6.0 self-contained
  linux-x64.
- **`-p:GamePath=/game` disables autodetect — confirmed.** `build/find-game-folder.targets` gates every
  autodetect path on `Condition="!Exists('$(GamePath)')"`, so a preset, existing `/game` short-circuits
  all of it. `SMAPI.csproj` references the game DLLs via `$(GamePath)\*.dll`.
- **Set `-p:CopyToGameFolder=false`.** `common.targets` defaults `CopyToGameFolder=true` (it copies build
  output into the detected game folder); disable it and assemble deliberately instead.
- **Bundled mods are `SMAPI.Mods.ConsoleCommands` + `SMAPI.Mods.SaveBackup`** (confirmed: `src/` dir listing
  at the tag, and the installed `Mods/ConsoleCommands` + `Mods/SaveBackup`).

**Stage (a new `smapi-builder` stage — do NOT extend `game-downloader`, which is `aspnet:10.0`):**

1. **Base it on `mcr.microsoft.com/dotnet/sdk:6.0`** for the SMAPI build stage specifically. This resolves
   the net6-vs-SDK10 snag definitively: don't gamble that `sdk:10.0` restores net6 runtime packs for a
   self-contained publish — use the matching SDK. (The main `mod-builder` stays on `sdk:10.0`; only this
   stage pins 6.0.) The game DLLs come from the `game-downloader` stage via `COPY --from=game-downloader`.
2. `git clone --depth 1 --branch ${SMAPI_VERSION} https://github.com/Pathoschild/SMAPI.git /smapi`
3. Apply the series, fail-fast: for each patch, `git -C /smapi apply --check <p>` then `git -C /smapi apply <p>`
   (a non-applying patch aborts the build with a clear error — no silent drift).
4. Publish SMAPI + the two bundled mods against the staged game DLLs at `/game`:
   `dotnet publish /smapi/src/SMAPI -c Release -r linux-x64 --self-contained true -p:OS=Unix -p:GamePath=/game -p:CopyToGameFolder=false`
   (+ `SMAPI.Mods.ConsoleCommands`, `SMAPI.Mods.SaveBackup`, `--self-contained false`).
5. **Assemble via the SMAPI installer we just built** (`--install --no-prompt --game-path /game`) rather
   than hand-rolling `cp`. The installer is the source of truth for the file layout — it handles the
   `StardewModdingAPI.deps.json` copy (load-bearing: resolves native libs like SkiaSharp), the
   `unix-launcher.sh` → `StardewValley` swap, and the `smapi-internal/` tree. Hand-rolled `cp` is the
   documented fallback only if the installer can't run non-interactively in the build stage.
   - **Fallback `cp` assembly** (if needed): copy publish root (`StardewModdingAPI`, `StardewModdingAPI.dll`,
     `.xml`, `steam_appid.txt`, `unix-launcher.sh`, `StardewModdingAPI.runtimeconfig.json`, whole
     `smapi-internal/`) into `/game`; copy the two mods into `/game/Mods/{ConsoleCommands,SaveBackup}`;
     `cp "/game/Stardew Valley.deps.json" "/game/StardewModdingAPI.deps.json"`; swap the launcher; `chmod 755`.
6. **Verify the artifact, don't trust the green build** (`runtime-post-conditions-are-gates.md`): confirm the
   produced `StardewModdingAPI.dll` is OUR build, its `runtimeconfig.json` targets `net6.0`, and the
   patched `% oneSecondTicks` / XACT early-return are actually in the IL (inspect with `ilspycmd`).

**All six SMAPI install sites must move off the prebuilt zip** (grep `SMAPI-.*-installer` across
`docker/`). Any one left on the zip silently reverts *that image* to vanilla SMAPI (the `60` divisor +
the XACT log):
- `docker/Dockerfile` (server, build-time)
- `docker/Dockerfile.test-client` (test-client, build-time)
- `docker/modern/Dockerfile` (musl/Alpine image, build-time)
- `docker/rootfs/startapp.sh` (server runtime fallback)
- `docker/rootfs-test-client/startapp.sh` (test-client runtime fallback)
- `docker/modern/rootfs/opt/bin/start-game.sh` (musl runtime fallback)

The runtime-fallback sites (`startapp.sh` / `start-game.sh`) only run when `${SMAPI_EXECUTABLE}` is absent
(build-time install skipped) — decide per site whether to (a) point them at a baked-in patched SMAPI
tarball, or (b) drop the runtime-install path entirely and rely on the build-time install. Per
`runtime-post-conditions-are-gates.md`, confirm which path each image actually exercises before assuming a
site is dead. The musl (`docker/modern/`) sites additionally interact with the musl gotchas in
`modern-docker.md` — the built SMAPI must still boot under musl.

### Backmerge / update procedure (when upstream ships e.g. 4.6.0)

The delta is a two-patch series + one `ARG`. To bump:
```bash
git clone https://github.com/Pathoschild/SMAPI.git /tmp/smapi-up && cd /tmp/smapi-up
git checkout 4.6.0
git apply --check <repo>/docker/smapi-patches/*.patch      # conflict probe (both patches)
#   exit 0 → still applies; just bump the ARG
#   exit !0 → git am --3way <repo>/docker/smapi-patches/*.patch; resolve; git am --continue
git format-patch 4.6.0 --stdout -- <changed paths>         # re-emit whichever patch(es) drifted
# bump ARG SMAPI_VERSION=4.6.0 at all six install sites (see "The build path"), rebuild
```
**Pin is a (game, SMAPI) pair:** SDV 1.6.15 ↔ SMAPI 4.5.2. The dominant update trigger is a Stardew
update pulled by `steam-service` (old SMAPI refuses a newer game). Otherwise stay pinned, upgrade
deliberately. **Renovate** already runs here (`renovate.json`) — add a `regexManager` on
`ARG SMAPI_VERSION=` against the `Pathoschild/SMAPI` GitHub-releases datasource to auto-open bump PRs.

**Conflict surface (low):** `git apply` matches on context, not line numbers, so the method moving
doesn't conflict — only an upstream edit *within the 3-line window* (a tick-cadence rework, a
`TicksElapsed` rename) or an `SConfig` schema refactor does. Both are rare and both are exactly when
you'd want to re-examine the patch anyway. Budget ~15 min manual re-derive on the occasional bump.

### Compatibility verification

- **Patch is fail-closed:** `OneSecondTickInterval <= 0` → vanilla `60`. A missing/old `config.user.json`
  → default 60 → current behavior, never a crash. Safe for any not-yet-updated install.
- **Other SMAPI events unaffected** — only the one-second divisor changes; `UpdateTicked`,
  `OneSecondUpdateTicking`/`Ticked` still fire from the same loop, just with a corrected period.
- **LAN/Steam/lobby:** the change is in SMAPI's generic loop, transport-agnostic.
- **Test-client:** test-client also installs SMAPI (`Dockerfile.test-client`); if it runs a different
  TPS (`CLIENT_TPS`), set its `OneSecondTickInterval` to match its own rate, not the server's.
- **The existing 3 mod subscribers** (`AlwaysOn`/`GameManager`/`MapService` `OnOneSecond*`) need **no
  code change** — they automatically start firing every real second once the event is fixed. Do not
  touch `AlwaysOn.cs`, `GameManagerService.cs`, or `MapService.cs` for this fix.
- **Rule update:** `.claude/rules/one-second-update-ticked-fires-per-game-tick.md` and its citation in
  `host-automation.md` (invariant 8) currently state the cadence is "SMAPI's (60-tick) and can't be
  changed from mod code." After this lands that's no longer true *on our build*. Update the rule to
  reflect that our patched SMAPI fires it per-second (and what `OneSecondTickInterval` must be set to),
  preserving the `host-automation.md` cross-link. Do NOT delete the rule — vanilla SMAPI still behaves
  the old way, and the mod must stay correct if ever run on stock SMAPI.

## Verification

Runtime gates — observe, don't infer:

1. Image builds; the produced `StardewModdingAPI.dll` is OUR build (inspect the artifact per
   `runtime-post-conditions-are-gates.md`), targeting net6.0, with both patches present in IL.
2. Boot at `SERVER_TPS=5`: `OneSecondUpdateTicked` fires ~every 1s, not ~12s. Confirm via a per-second
   handler's cadence in the JSONL (e.g. healthcheck log spacing, or instrument a temporary log in
   `OnOneSecond*`). With vanilla SMAPI it's 12s apart.
3. `RunHealthCheck` (`GameManagerService.cs`) interval returns to its intended seconds (it counts
   fires; fires are now 1/sec) — the latent 12× bug noted above is fixed for free.
4. Set `OneSecondTickInterval` to a wrong value (e.g. 60) and confirm cadence reverts to 12s — proves
   the config knob is actually consumed (`verify-claims.md`).
5. **XACT patch:** boot the server and grep the log — the `[ERROR game] ...caught exception initializing
   XACT.` line is **gone**, and the server still runs silent (audio was never functional headless). No
   new error appears in its place.
6. Full E2E suite green at `SERVER_TPS=5` — across the server, test-client, AND musl (`docker/modern/`)
   images, since all three now build their own SMAPI.

## Out of scope

- `PrintBannerAfterDelay` (`AlwaysOn.cs`) and the `* Env.ServerTps` multipliers
  (`AlwaysOn.cs`, `SteamConstants.cs`) are **already TPS-correct** (they convert seconds→
  ticks). Do not touch.
