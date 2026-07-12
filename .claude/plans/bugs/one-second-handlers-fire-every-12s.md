# Fix: `OneSecondUpdateTicked` handlers fire every 12s at `SERVER_TPS=5`

## ⚠️ Do NOT fix this with per-handler wall-clock gates

The obvious workaround — gate each `OnOneSecond*` handler's body behind an inline
`(DateTime.UtcNow - _last).TotalSeconds >= 1.0` check inside `OnUpdateTicked`, and drop the
`OneSecondUpdateTicked` subscription — was the first plan for this bug and is captured in full below
for its diagnosis value, but it is **rejected as the fix**. It leaves the real SMAPI event
permanently wrong (still fires at 12s) and adds a second, conflicting notion of "one second" that
every *future* subscriber must independently know to avoid. The correct fix is at the root: patch
SMAPI itself so `OneSecondUpdateTicked` fires at the actual configured tick rate, for every current
and future subscriber, with zero per-handler workaround code. See "Chosen fix" below.

## Problem (verified against code + SMAPI source)

SMAPI fires `GameLoop.OneSecondUpdateTicked` on `SCore.TicksElapsed % 60 == 0`, and
`TicksElapsed` increments once per MonoGame `Update`
([`SCore.cs`](https://github.com/Pathoschild/SMAPI/blob/develop/src/SMAPI/Framework/SCore.cs)).
`ServerOptimizer.cs:286` pins `Game1.game1.TargetElapsedTime = 1000/Env.ServerTps` ms, so at the
proven-stable `SERVER_TPS=5` the loop runs 5 ticks/sec and the event fires every **60/5 = 12s**, not
1s. The cadence is SMAPI-internal and cannot be made per-second from mod code without patching SMAPI
(raising TPS to 60 is ruled out by `server-tps-headless.md`). This is the trap already documented in
`.claude/rules/one-second-update-ticked-fires-per-game-tick.md`.

Three services subscribe and all run ~12× slower than their "one second" naming implies:

| Handler | File:line | Impact at 12s |
|---|---|---|
| `AlwaysOnServer.OnOneSecondUpdateTicked` | `AlwaysOn.cs:308` | Player-facing progression lags (shipping menu, CC unlock, pet choice) up to 12s |
| `GameManagerService.OnOneSecondTicked` | `GameManagerService.cs:191` | `/newgame`+`/reload` startup polling lags; **+ healthcheck interval bug, below** |
| `MapService.OnOneSecondUpdateTick` | `MapService.cs:83` | Portrait PNG refresh every 12s (cosmetic) |

**Latent bug surfaced (GameManager):** `RunHealthCheck` (`GameManagerService.cs:316-353`) self-decrements
`_healthCheckTimer` once per fire and reloads it with `Env.HealthCheckSeconds` (default **300**,
`Env.cs:27`). Because the handler fires every 12s rather than 1s, the routine probe interval is
actually `300 × 12 = 3600s` (1 hour), not 300s. (The "unreachable 2+ min → exit" backstop at `:338`
uses real `DateTime.Now` and is unaffected.) Fixing the event's cadence at the source fixes this
**for free** — the counter counts fires, and fires become 1/sec — so `_healthCheckTimer` needs no
change.

---

## Chosen fix: patch SMAPI's hardcoded 60-tick divisor

Fix the wrong assumption baked into SMAPI: it hardcodes `60` ticks = 1 second, true only at 60 TPS.
Replace the hardcoded divisor with our configured tick rate so the event's contract (`<TPS>` ticks =
1 second = the event fires) holds at any TPS. This keeps the **real** SMAPI event honest for any
current or future subscriber — no parallel custom event, no per-handler workaround.

Rejected alternatives:
- **Per-handler wall-clock gates in mod code** (see the superseded diagnosis section below) — leaves
  the real `OneSecondUpdateTicked` at 12s forever; every future subscriber has to independently
  remember the trap instead of the event just being correct.
- **Harmony-patching SMAPI's `SCore`** — `isOneSecond` is a *local* in a private core-loop method;
  only a fragile IL transpiler could reach it, and SMAPI internals carry no stability contract
  (`smapi-api-surface.md`). Unreliable across SMAPI versions.
- **Raising `SERVER_TPS` to 60** — contradicts `server-tps-headless.md` (5 is the proven-stable
  headless value, run by CI + `.env.test`); raises per-server CPU.

### Root cause (verified at source)

`src/SMAPI/Framework/SCore.cs`, method `OnPlayerInstanceUpdating`:
```csharp
bool isOneSecond = SCore.TicksElapsed % 60 == 0;   // hardcoded 60
...
if (isOneSecond) events.OneSecondUpdateTicking.RaiseEmpty();
...
if (isOneSecond) events.OneSecondUpdateTicked.RaiseEmpty();
```
`TicksElapsed` is `internal static uint`, bumped once per MonoGame `Update`. `Update`'s rate is set
by `Game1.game1.TargetElapsedTime = 1000/Env.ServerTps` (`ServerOptimizer.cs:286`). So at `TPS=5`,
`% 60` is satisfied every 60 ticks = 12 real seconds. Changing the divisor to the real TPS makes it
`% 5 == 0` → every 5 ticks = 1 real second. (Verified: `% 60` line + `TicksElapsed` declaration via
SMAPI 4.5.2 source; the firing math via `ServerOptimizer.cs` + SMAPI's 1-update-per-Update loop.)

### Why this needs building SMAPI from source

The repo does **not** build SMAPI today — `docker/Dockerfile:45-51` and `docker/rootfs/startapp.sh:166`
download the prebuilt official installer zip (`SMAPI_VERSION=4.5.2`) and run its Linux installer. The
`60` lives in compiled IL inside that zip. Any fix to it requires compiling a modified SMAPI. That
build switch — not the patch mechanism — is the bulk of the work, and is identical regardless of how we
carry the patch.

### License (verified)

SMAPI is **LGPL v3**. Forking/modifying/redistributing a modified build in a Docker image is permitted.
Weak-copyleft obligations we take on: (1) the modified SMAPI source must remain available — satisfied
trivially by checking the patch into this repo (the delta is "upstream tag 4.5.2 + our `.patch`"); our
mod (which only *links* SMAPI) does not inherit LGPL; (2) include the LGPL notice. We must stop shipping
the prebuilt installer zip for the modified build — we now distribute a modified binary, and the patch
file IS the disclosed source delta. **Action:** add a short `LICENSE`/`NOTICE` line in the SMAPI build
stage or repo docs pointing at upstream 4.5.2 + `docker/smapi-patches/`.

### Strategy: patch-file applied at build time (NOT a forked repo)

For a ~3-line change, a long-lived forked GitHub repo is pure overhead (offsite, low discoverability,
its own CI, silent-rot risk). Instead: a `git format-patch` series checked into **this** repo at
`docker/smapi-patches/`, applied at Docker build time against a freshly-cloned pinned upstream tag.
The patch is re-validated against the exact pinned upstream on every build; a failure to apply is a hard
build error (no silent drift). Reviewer sees it as a normal inline `+`/`-` diff. Revisit a real fork
only if the patch ever grows to dozens of lines across many files — nowhere near that now.

### The patch (2 files)

`docker/smapi-patches/0001-onesecond-tickrate-divisor.patch`, made via `git format-patch` against the
`4.5.2` tag, touching:

1. **`src/SMAPI/Framework/SCore.cs`** — replace the hardcoded `60` with the configured divisor read
   from `SConfig`. Read the value once into a local at the top of `OnPlayerInstanceUpdating`:
   ```csharp
   int oneSecondTicks = this.Settings.OneSecondTickInterval > 0 ? this.Settings.OneSecondTickInterval : 60;
   bool isOneSecond = SCore.TicksElapsed % (uint)oneSecondTicks == 0;
   ```
   (Guard `> 0`: a 0/negative config value falls back to vanilla 60, never a divide-by-zero. Confirm
   the `SConfig` instance accessor name on the pinned tag — `this.Settings` vs a field — when authoring
   the patch; the divisor field lives on whatever `SCore` already holds the config as.)

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
overlay — `docker/rootfs/startapp.sh:182` copies `/data/smapi-config.json` →
`smapi-internal/config.user.json` (and `Dockerfile:161` for the test-client). **Add the key there:**
`docker/rootfs/data/smapi-config.json` gains `"OneSecondTickInterval": <value>`. The value must equal
`SERVER_TPS`. Since `SERVER_TPS` is an env var read by the mod (`Env.cs:35`) and the SMAPI config is a
static JSON file, they must be kept in sync — see Open Question 1.

### Dockerfile changes (`docker/Dockerfile`, replacing `:45-51`)

New stage (or extend `game-downloader`) — clone, patch, build, assemble into `/game`:

1. `git clone --depth 1 --branch ${SMAPI_VERSION} https://github.com/Pathoschild/SMAPI.git /smapi`
2. `git -C /smapi apply --check /patches/*.patch` then `git -C /smapi apply /patches/*.patch`
   (fail-fast: `--check` first so a non-applying patch aborts the build with a clear error).
3. Build against the staged game DLLs (already at `/game` from the `game-downloader` stage):
   `dotnet publish /smapi/src/SMAPI --configuration Release --runtime linux-x64 --framework net6.0`
   `-p:OS=Unix -p:GamePath=/game -p:CopyToGameFolder=false --self-contained true`
   plus the two bundled mods (`SMAPI.Mods.ConsoleCommands`, `SMAPI.Mods.SaveBackup`,
   `--self-contained false`). `-p:GamePath=/game` wins over autodetect (the targets short-circuit on an
   already-set `$(GamePath)`).
4. **Assemble the install into `/game` with `cp` (replaces the interactive installer)** — the installer
   is just an extractor+copier. The minimal Linux install:
   - copy publish output (root: `StardewModdingAPI`, `StardewModdingAPI.dll`, `.xml`, `steam_appid.txt`,
     `unix-launcher.sh`, `StardewModdingAPI.runtimeconfig.json`; plus the whole `smapi-internal/` tree)
     into `/game`
   - copy the two built mods into `/game/Mods/ConsoleCommands` and `/game/Mods/SaveBackup`
   - `cp "/game/Stardew Valley.deps.json" "/game/StardewModdingAPI.deps.json"`
     **(load-bearing — resolves native libs like SkiaSharp; easy to miss)**
   - `mv "/game/StardewValley" "/game/StardewValley-original"` *if it exists*, then
     `mv "/game/unix-launcher.sh" "/game/StardewValley"`
   - `chmod 755 "/game/StardewValley" "/game/StardewModdingAPI"`

   (Alternative: keep using the SMAPI **installer we just built** — it has `--install --no-prompt
   --game-path` — instead of hand-rolling `cp`. Lower risk of missing the deps.json step; decide at
   implementation. Either way, do NOT download the prebuilt zip anymore.)

5. Mirror the same into `docker/Dockerfile.test-client` (`:52-58`) and the runtime fallback installer in
   `docker/rootfs/startapp.sh:166` + `docker/rootfs-test-client/startapp.sh` — **all four SMAPI install
   sites** must move off the prebuilt zip, or a non-Docker-build path silently reverts to vanilla SMAPI
   (the `60`). Per `verify-edit-landed-in-artifact.md`, confirm which path each runtime actually uses.

#### ⚠️ Build-image snag (flagged, must resolve): net6.0 vs SDK 10

SMAPI 4.5.2 bundle projects are **net6.0**; our build image is `mcr.microsoft.com/dotnet/sdk:10.0`.
A self-contained net6.0 `linux-x64` publish needs the net6 runtime/targeting packs restorable. Options:
(a) use a `dotnet/sdk:6.0` (or multi-SDK) image for *just the SMAPI build stage* — cleanest; (b) stay
on SDK 10 and let restore pull the net6 runtime packs for a self-contained publish. **Verify the
produced `StardewModdingAPI.runtimeconfig.json`/apphost actually target 6.0 before trusting a green
build** (`verify-edit-landed-in-artifact.md`). Note the official packaging overrides the generated
runtimeconfig with `src/SMAPI.Installer/assets/runtimeconfig.json` (pins `6.0.0`, `rollForward
latestMinor`, `TieredCompilation=false`) — use that file if going framework-dependent.

### Backmerge / update procedure (when upstream ships e.g. 4.6.0)

The "fork" is one `.patch` + one `ARG`. To bump:
```bash
git clone https://github.com/Pathoschild/SMAPI.git /tmp/smapi-up && cd /tmp/smapi-up
git checkout 4.6.0
git apply --check <repo>/docker/smapi-patches/*.patch      # conflict probe
#   exit 0 → still applies; just bump the ARG
#   exit !0 → git am --3way <repo>/docker/smapi-patches/*.patch; resolve; git am --continue
git format-patch 4.6.0 --stdout > <repo>/docker/smapi-patches/0001-onesecond-tickrate-divisor.patch
# bump docker/Dockerfile ARG SMAPI_VERSION=4.6.0 (+ the 3 other install sites), rebuild
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
  `host-automation.md:29` (invariant 8) currently state the cadence is "SMAPI's (60-tick) and can't be
  changed from mod code." After this lands that's no longer true *on our build*. Update the rule to
  reflect that our forked SMAPI fires it per-second (and what `OneSecondTickInterval` must be set to),
  preserving the `host-automation.md:29` cross-link.

### Post-conditions (runtime gates — observe, don't infer)

1. Image builds; the produced `StardewModdingAPI.dll` is OUR build (inspect the artifact per
   `verify-edit-landed-in-artifact.md`), targeting net6.0.
2. Boot at `SERVER_TPS=5`: `OneSecondUpdateTicked` fires ~every 1s, not ~12s. Confirm via a per-second
   handler's cadence in the JSONL (e.g. healthcheck log spacing, or instrument a temporary log in
   `OnOneSecond*`). With vanilla SMAPI it's 12s apart.
3. `RunHealthCheck` (`GameManagerService.cs:316`) interval returns to its intended seconds (it counts
   fires; fires are now 1/sec) — the latent 12× bug noted above is fixed for free.
4. Set `OneSecondTickInterval` to a wrong value (e.g. 60) and confirm cadence reverts to 12s — proves
   the config knob is actually consumed (`verify-documented-config-is-consumed.md`).
5. Full E2E suite green at `SERVER_TPS=5`.

### Open questions (decide before/at implementation)

1. **Keep `OneSecondTickInterval` in sync with `SERVER_TPS`.** Both must equal the tick rate. Cleanest:
   have `startapp.sh` inject `SERVER_TPS` into the generated `config.user.json` at boot (jq/sed) so a
   single env var drives both — rather than two independently-edited values that can drift. (Mirror for
   the test-client with `CLIENT_TPS`.)
2. **Mid-day TPS changes?** `Env.ServerTps` is read once at boot; if TPS is never changed at runtime,
   a static config value is fine. Confirm no runtime TPS mutation exists.
3. **`cp` install vs built installer** (Dockerfile step 4) — pick at implementation; the built-installer
   route is lower-risk for the deps.json step.

## Out of scope

- `PrintBannerAfterDelay` (`AlwaysOn.cs:249`) and the `* Env.ServerTps` multipliers
  (`AlwaysOn.cs:254,397`, `SteamConstants.cs:98`) are **already TPS-correct** (they convert seconds→
  ticks). Do not touch.

---

## Appendix: superseded diagnosis — per-handler wall-clock gate (rejected, kept for reference)

This was the original plan before the SMAPI-level root cause fix above was chosen. It is **not** the
approach to implement — see the warning at the top of this document. Kept because the problem
statement and per-file impact analysis above are still accurate and useful context; only the "fix" was
wrong.

The rejected approach: keep the work on the game thread, drive it from the existing per-tick
`OnUpdateTicked`, and self-gate each of the three handlers with an inline
`(DateTime.UtcNow - _last).TotalSeconds >= 1.0` check, deleting the `OneSecondUpdateTicked`
subscription in each. This matches an established in-repo pattern (`MapService.cs:71`,
`AlwaysOnFestivals.cs:260`, `ApiService.cs:1226`) and would have worked, but it treats the symptom
(three known subscribers) instead of the cause (the event itself lies about its cadence), leaving a
trap for every future subscriber. Superseded by the SMAPI patch above, which fixes the event once for
everyone.
