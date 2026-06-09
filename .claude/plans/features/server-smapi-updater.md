# Decouple & Auto-Update SMAPI Version

## Context

The server mod is compiled against a SMAPI version pinned in `docker/Dockerfile` (`ARG SMAPI_VERSION=4.5.2`). Today that single value drives **both** the build-time compile dependency **and** the runtime install, so every SMAPI release forces a new image release. The goal is to decouple these: keep the mod compiled against a known major, but let a running container pick up newer SMAPI releases (within that major) **without an image rebuild**.

Two facts make this safe and cheap:

- SMAPI is **already installed at runtime** by `docker/rootfs/startapp.sh` → `init_smapi()` (the final image ships only the compiled `JunimoServer.dll`, never SMAPI). So runtime version selection is purely a shell/config concern.
- SMAPI is backward-compatible **within a major version**, and `manifest.json`'s `MinimumApiVersion` is only checked when non-null (verified against SMAPI's `ModResolver`: `mod.Manifest.MinimumApiVersion?.IsNewerThan(apiVersion) == true` — null short-circuits, mod loads). A mod compiled against 4.x therefore loads on **any** 4.y. The real guardrail is a **hard major-pin**, not the manifest.

**Outcome:** with no `SMAPI_VERSION` set, the container auto-resolves and installs the latest SMAPI in the compiled major on every start, and a long-running server periodically notices newer releases and (optionally) restarts itself to apply them. Setting `SMAPI_VERSION` pins exactly that version and disables all auto behavior.

## Decisions (from clarification)

- **Auto-restart on update:** OFF by default; opt-in.
- **Update notification:** three-state — `off` / `once` (announce once at detection) / `repeating` (re-announce on each in-game day transition while an update is pending).
- **`MinimumApiVersion`:** **remove it entirely** from `manifest.json`. Rely on our own canonical major-pin as the sole guard. (Verified safe — the field is `ISemanticVersion?`, optional, and skipped when null.)
- **JSON parsing in shell:** add `jq` to the runtime image.

## Architecture

Two halves communicating through one contract file in the game volume, **`/data/game/.smapi-state.json`**:

```json
{ "installedVersion": "4.5.2", "major": 4, "pinned": false }
```

- **Shell (`startapp.sh`)** resolves the version at startup (AUTO = latest-in-major from GitHub; PIN = the env value verbatim), installs only if it differs from the on-disk stamp, and writes the state file. This is the single source of truth for "what's installed" and "are we pinned" — the mod cannot otherwise distinguish "AUTO happened to resolve 4.5.2" from "operator pinned 4.5.2."
- **Mod (`SmapiUpdateService`)** reads the state file; if `pinned` or the check is disabled it stays silent. Otherwise it periodically re-queries GitHub for the latest in-major and reacts (notify and/or restart-after-save).

```
AUTO (no SMAPI_VERSION)                         PIN (-e SMAPI_VERSION=4.5.2)
  resolve latest <major>.x from GitHub            use value verbatim, skip GitHub
        └── compare to /data/game/.smapi-version stamp ──┘   (re)install only if differs
                              │  write .smapi-state.json (installedVersion, major, pinned)
                              ▼
                  SMAPI launches → SmapiUpdateService reads state
                    pinned/disabled → silent
                    auto → every N min query GitHub latest-in-major
                       newer in-major → notify (off/once/repeating) and/or restart-after-save
```

**Restart mechanism (already exists — no supervisor changes):** `docker-compose.yml:42` sets `restart: unless-stopped`. When the mod calls `Environment.Exit(0)`, `StardewModdingAPI` exits 0, `startapp.sh`'s `wait $SMAPI_PID` returns, the script ends, the container exits 0, Docker restarts it, and `init_smapi()` re-resolves/installs. This is the exact pattern already used in production at `GameManagerService.cs:255` (`Environment.Exit(1)` "exiting for container restart… restart will reload last save").

## Code-quality principles (carried into implementation)

- **One responsibility per type:** `SmapiState` = read the contract file; `SmapiReleaseClient` = answer "latest in-major?"; `SmapiUpdateService` = scheduling + reaction policy. No type reaches across these seams.
- **Follow the nearest existing pattern, don't invent:** interval gating from `MapService`, HTTP from `NetworkHelper` (the simple one, not the Steam retry client), config from `ServerSettingsLoader`, restart-via-exit from `GameManagerService`, save hook from `BackupScheduler`. Every new line should look like code already in the repo.
- **No speculative machinery:** no retry/backoff (the poll loop is the retry), no event/DI plumbing beyond what's used, handlers wired only when their feature is enabled.
- **Single source of truth for state:** one `_announcedFor` value drives both notify and restart announcements — no parallel per-channel flags to drift.

---

## Part 1 — Shell: version resolution + cache-guard fix

**File:** `docker/rootfs/startapp.sh` — rewrite `init_smapi()` (lines 158-183).

**New env vars** (declared in `docker/Dockerfile`, see Part 4):

- `SMAPI_MAJOR` (default `4`) — the compiled major; the AUTO-mode filter target. Explicit, not derived (in AUTO mode there's no `SMAPI_VERSION` to derive from).
- `SMAPI_DEFAULT_VERSION` — offline / rate-limit fallback. **Declared as `ENV SMAPI_DEFAULT_VERSION=${SMAPI_VERSION}`** so it always equals the build `ARG` and Renovate's single bump propagates to it (see Part 4 — a separate literal would silently drift from the build version when Renovate bumps only the `ARG` line).

**`resolve_smapi_version()`** (new helper) — sets `RESOLVED`, `PINNED`, and `RESOLVED_MAJOR`:

```sh
if [ -n "${SMAPI_VERSION:-}" ]; then
    RESOLVED="$SMAPI_VERSION"; PINNED=true                          # PIN mode (verbatim)
else
    PINNED=false
    RESOLVED="$(fetch_latest_in_major "$SMAPI_MAJOR")"             # AUTO mode (already swallows failure → empty)
    if [ -z "$RESOLVED" ]; then
        echo "Could not resolve latest SMAPI ${SMAPI_MAJOR}.x from GitHub; falling back to ${SMAPI_DEFAULT_VERSION}."
        RESOLVED="$SMAPI_DEFAULT_VERSION"                           # fallback (offline / rate-limited / empty list)
    fi
fi
RESOLVED_MAJOR="${RESOLVED%%.*}"   # first dotted segment — truthful even if a pin crosses majors
```

**`fetch_latest_in_major(major)`** (new helper) — use the **list** endpoint, never `/latest` (which returns the newest across _all_ majors and would hand back a future 5.0.0). Returns the highest in-major tag, or empty string on any failure:

```sh
fetch_latest_in_major() {
    curl -fsSL --max-time 15 -H "Accept: application/vnd.github+json" \
      https://api.github.com/repos/Pathoschild/SMAPI/releases 2>/dev/null \
    | jq -r --arg M "$1" '
        [ .[] | select(.draft==false and .prerelease==false)
              | .tag_name | ltrimstr("v")
              | select(startswith($M + ".")) ]
        | sort_by(split(".") | map(tonumber)) | last // empty' 2>/dev/null \
    || true   # never let curl/jq failure abort the script under `set -euo pipefail`
}
```

The `// empty` makes jq print nothing (not the string `null`) when the filtered list is empty — so an empty major or a successful-but-no-match response is treated as "unresolved" by the caller's `[ -z ]` check, triggering fallback rather than installing a bogus version.

**Cache-guard fix** — replace `if [ -e "${SMAPI_EXECUTABLE}" ]` (line 160) with a version-stamp comparison. Stamp file: **`/data/game/.smapi-version`** (bare version string, lives in the `game-data` volume exactly as long as the install):

```sh
resolve_smapi_version
INSTALLED="$(cat /data/game/.smapi-version 2>/dev/null || echo '')"
if [ ! -e "$SMAPI_EXECUTABLE" ] || [ "$INSTALLED" != "$RESOLVED" ]; then
    install_smapi "$RESOLVED"             # existing download+install (lines 166-172), parameterized
    echo "$RESOLVED" > /data/game/.smapi-version    # stamp ONLY after a successful install
else
    echo "SMAPI $INSTALLED already current, skipping install."
fi
write_smapi_state "$RESOLVED" "$RESOLVED_MAJOR" "$PINNED"   # writes /data/game/.smapi-state.json
cp -rf /data/smapi-config.json "${GAME_DEST_DIR}/smapi-internal/config.user.json"  # existing line 182 — MUST stay after install
```

This fixes the bug three ways: a version change reinstalls (stamp differs), a missing executable forces install, and AUTO mode reinstalls on restart when GitHub reports newer. The installer's option `2` (`printf "2\n\n"`) overwrites `StardewModdingAPI` + `smapi-internal/` in place and does **not** touch `Mods/` — no manual removal needed. Keep the `config.user.json` copy **after** the installer (the installer may rewrite `smapi-internal/`).

**Failure mode — invalid pinned version (operator typo, e.g. `SMAPI_VERSION=4.9.9`):** the installer download 404s and, under `set -euo pipefail`, `install_smapi` aborts the script → container exits non-zero → Docker `unless-stopped` restarts it into the same failure (crash loop, visible in `docker logs`). This matches the **current** behavior exactly (today's `init_smapi` has the same `curl | unzip | installer` chain under the same `set -e`), so it is not a regression. Note it in the docs as "a pinned version must be a real SMAPI release tag." Stamping only after a successful install (above) means a failed attempt never poisons the stamp.

**`write_smapi_state(version, major, pinned)`** (new helper) — emit the contract file **atomically** (write-temp-then-rename) so the mod, which may be reading it during a concurrent restart, never sees a half-written file:

```sh
write_smapi_state() {
    printf '{ "installedVersion": "%s", "major": %s, "pinned": %s }\n' "$1" "$2" "$3" \
        > /data/game/.smapi-state.json.tmp
    mv -f /data/game/.smapi-state.json.tmp /data/game/.smapi-state.json   # rename is atomic on the same fs
}
```

`major` is the **resolved** version's major (`RESOLVED_MAJOR`), not the build `SMAPI_MAJOR` — so a pin that crosses majors reports the truth (e.g. pinned `5.0.0` → `"major": 5`), and the mod's `_compiledMajor` guard stays self-consistent with what's actually installed.

---

## Part 2 — Mod: `SmapiUpdateService`

**New folder:** `mod/JunimoServer/Services/UpdateCheck/`, namespace `JunimoServer.Services.UpdateCheck`. Three new files; the service is auto-discovered by reflection in `ModEntry.RegisterServices()` (no `ModEntry.cs` edit needed).

### `SmapiState.cs` (POCO + safe loader)

- Properties: `string InstalledVersion`, `int Major`, `bool Pinned`.
- `static SmapiState Load(string gamePath, IMonitor monitor)` reads `Path.Combine(gamePath, ".smapi-state.json")` via `System.Text.Json`. **On any read/parse failure or missing file, return `Pinned = true`** (fail-safe: never auto-restart when mode is unknown — e.g. an old image or running outside the container).
- `gamePath` comes from `StardewModdingAPI.Constants.GamePath` (verified: `string`, the directory of the executing SMAPI assembly). Inside the container SMAPI runs as `/data/game/StardewModdingAPI`, so this resolves to `/data/game` — the same directory the shell writes `.smapi-state.json` to. (`startapp.sh:139-141` symlinks `/data/game`→`/data/game`; source and target are identical under the compose `game-data:/data/game` mount, so a symlink-canonicalized `GamePath` still matches.)

### `SmapiReleaseClient.cs` (GitHub query)

Model on the **simpler** of the two HTTP precedents — `Util/NetworkHelper.GetIpAddressExternalAsync` (`NetworkHelper.cs:74-85`, with its `static readonly HttpClient` field at `:55-58`): a `static readonly HttpClient` with a `Timeout`, a single `await ...ConfigureAwait(false)`, and a `try/catch` returning a sentinel on failure. **Do not** mirror `SteamAuthApiClient`'s retry/backoff/transient-classifier — a failed poll is harmless because the 5-minute loop already _is_ the retry (per `retry-is-evidence-of-root-cause.md`; adding a backoff loop on top would be redundant machinery). Use `System.Text.Json`.

- `async Task<ISemanticVersion> GetLatestInMajorAsync(int major)` — GET `https://api.github.com/repos/Pathoschild/SMAPI/releases` with a `User-Agent` header (GitHub rejects requests without one), deserialize, skip drafts/prereleases, take each `tag_name` (strip a leading `v`), keep those `StartsWith($"{major}.")`, return the highest. `try/catch` → `null` on any failure (network, rate-limit, parse) — the caller treats `null` as "no newer version this cycle".
- **Parse with `SemanticVersion.TryParse(tag, out var v)`, never the `new SemanticVersion(tag)` constructor** — verified against SMAPI source: the constructor throws `FormatException` on a non-standard tag, which would abort the parse on one malformed release; `TryParse` returns `bool` and never throws, so it skips bad tags silently. (Verified API surface: `ISemanticVersion` exposes `MajorVersion` (int), `IsNewerThan(ISemanticVersion)`, `Equals(ISemanticVersion)`; the concrete `SemanticVersion`/`TryParse` live in namespace **`StardewModdingAPI.Toolkit`** — `SmapiReleaseClient.cs` needs `using StardewModdingAPI.Toolkit;`, since the bare `StardewModdingAPI` namespace does not export the concrete type.)
- **Test seam:** when `Env.IsTest` (`Env.cs:20`, driven by `SDVD_ENV=test`) and `SMAPI_FAKE_LATEST` is set, return that version without calling GitHub — makes the action paths deterministic without waiting on a real SMAPI release.

Expose `async Task<...>` and let the caller `await` it inside its `Task.Run` (matching `NetworkHelper`'s async shape), rather than blocking `.Result` — cleaner and avoids the `AggregateException`-unwrapping the Steam client needs.

### `SmapiUpdateService.cs` (`: ModService`)

**Separation of concerns.** The service owns three distinct jobs; keep them in separate methods so no single method does discovery + policy + side-effects:

1. **Discovery** — `SmapiReleaseClient` (already its own class) answers "what's the latest in-major version?"
2. **Poll scheduling** — the tick handler decides _when_ to kick a discovery and marshals the result back to the game thread.
3. **Reaction policy** — separate `Notify(...)` and `MaybeArmRestart(...)` methods, each reading config and acting. The tick handler orchestrates; it does not inline their logic.

**State — one "pending update" value, not a scatter of flags.** Collapse the announcement bookkeeping to a single nullable field plus the running version:

```csharp
private readonly ISemanticVersion _runningVersion;   // Constants.ApiVersion, set in Entry
private int _compiledMajor;                           // from SmapiState
private DateTime _lastCheck = DateTime.UtcNow;        // wait one full interval before first poll
private volatile bool _checkInFlight;
private ISemanticVersion _latestSeen;                 // newest the poller has found (cross-thread handoff)
private ISemanticVersion _announcedFor;               // last version we announced about; null = nothing pending
private bool _restartArmed;                           // set once when a restart-worthy update is pending
```

`_announcedFor` replaces the previous `_announcedVersion` + `_restartAnnouncedVersion` pair (those tracked the same "have I told the user about version X" fact for two channels and would drift — see `orthogonal-fields.md`). One value, keyed off the _transition_ to a new pending version, drives both notify and the one-time restart announcement.

**Constructor:** `(IModHelper helper, IMonitor monitor, ServerSettingsLoader settings) : base(helper, monitor)`.

**`Entry()`:**

1. `var state = SmapiState.Load(Constants.GamePath, Monitor);`
2. If `state.Pinned || !settings.SmapiUpdateCheckEnabled` → log one `Info` line ("SMAPI auto-update check disabled (pinned)" / "(disabled in settings)") and **return** — no subscriptions (clean kill switch: a disabled feature wires up nothing).
3. Capture `_compiledMajor = state.Major`, `_runningVersion = Constants.ApiVersion`.
4. Subscribe `OneSecondUpdateTicked` (poll + react), `DayStarted` (repeating re-announce). The restart latch subscribes to `Saved` **only when** `settings.SmapiAutoRestartOnUpdate` — don't wire a handler the feature never uses.

**Poll scheduling** (DateTime-delta interval-gate idiom from `MapService.OnUpdateTicked`, `Services/Map/MapService.cs:70-82`), discovery off the game thread:

```csharp
private void OnOneSecondUpdateTicked(object s, OneSecondUpdateTickedEventArgs e)
{
    ReactToPendingUpdate();                            // 1) act on the game thread (see below)

    if (DateTime.UtcNow - _lastCheck < TimeSpan.FromMinutes(_settings.SmapiUpdateCheckIntervalMinutes)) return;
    if (_checkInFlight) return;                         // 2) kick at most one poll per interval
    _lastCheck = DateTime.UtcNow;
    _checkInFlight = true;
    // asynclocal-pitfalls.md: short-lived per-cycle Task.Run, emits no request-correlated events, and the
    // reaction is marshaled back to the game thread via plain fields — no capture/rebind or SuppressFlow needed.
    Task.Run(async () =>
    {
        var v = await _releaseClient.GetLatestInMajorAsync(_compiledMajor);   // null on failure
        if (v != null) _latestSeen = v;
        _checkInFlight = false;
    });
}
```

The poller does **field writes only** — never `Game1`, chat, settings, or `Environment.Exit`. All side-effects run on the game thread in `ReactToPendingUpdate()`.

**`ReactToPendingUpdate()`** (game thread) — the single, small orchestrator:

```
seen = _latestSeen
if seen == null or not seen.IsNewerThan(_runningVersion): return        // nothing pending
if seen.MajorVersion != _compiledMajor: Trace(...); return             // major guardrail (defense in depth)
if seen.Equals(_announcedFor): return                                  // already handled this version
_announcedFor = seen                                                   // transition: a NEW pending version
if _settings.SmapiAutoRestartOnUpdate: AnnounceRestart(seen); _restartArmed = true
else if notifyMode != Off:               Notify(seen)
```

Restart and notify are mutually exclusive at the transition, which is why the "please restart" / "restarting after next save" precedence is structural here, not a special-case suppression.

**World-readiness guard (the loop starts at the title screen).** `Entry()` subscribes at mod-load, so the first ticks fire at the title screen, before any save is loaded and before a multiplayer session exists. `SendPublicMessage` → `GetMultiplayer().sendChatMessage` is valid only in-session. Centralize the guard in one helper used by every chat path:

```csharp
private void Broadcast(string msg) {
    Monitor.Log(msg, LogLevel.Info);                                   // console: always
    if (Game1.IsServer && Game1.gameMode == 3)                         // chat: only when world-ready
        Helper.SendPublicMessage(msg);                                 // extension: ModHelperExtensions.cs:43-47
}
```

`SendPublicMessage` iterates `Game1.otherFarmers`, so it **must run on the game thread** (it can throw if a player connects/disconnects mid-iteration — see the note at `ApiService.cs:2140`). Every caller of `Broadcast` here is already game-thread (`ReactToPendingUpdate`, `DayStarted`, `Saved`); **never call `Broadcast` from the poll `Task.Run`.** `Saved`/`DayStarted` handlers are inherently in-session, but routing them through `Broadcast` too keeps one code path. The idiom is exercised in production at `BackupScheduler.cs:41,45`. **Never `LogLevel.Error`** (`debugging.md` test-poison rule).

**Notify (`SmapiUpdateNotify` enum `Off`/`Once`/`Repeating`):**

- `Notify(version)` → `Broadcast("New SMAPI version available, please restart server to update")`.
- `Once` and `Repeating` both announce once at the transition (via `ReactToPendingUpdate` above). `Repeating` additionally re-announces on each `DayStarted` while an update is still pending (`_announcedFor != null && _announcedFor.IsNewerThan(_runningVersion)`) — once per in-game day (not per save), so no double-fire.

**Auto restart-after-save (`SmapiAutoRestartOnUpdate`, default `false`):**

- `AnnounceRestart(version)` → `Broadcast("New SMAPI version available, restarting server after next save")`.
- `OnSaved` (subscribed only when the flag is on): if `_restartArmed`, `Monitor.Log("Restarting to apply SMAPI update", LogLevel.Warn)` then `Environment.Exit(0)`. Use `Saved` (not `Saving`) so the save is durable before exit (same rationale as `BackupScheduler`).

---

## Part 3 — Config knobs

**File:** `mod/JunimoServer/Services/Settings/ServerSettings.cs` — append to `ServerRuntimeSettings` (after `NetworkBroadcastPeriod`, line 77):

```csharp
/// <summary>Enable periodic SMAPI update checks. Ignored entirely when SMAPI_VERSION is pinned.</summary>
public bool SmapiUpdateCheckEnabled { get; set; } = true;
/// <summary>Minutes between SMAPI update checks. Clamped to [1, 1440].</summary>
public int SmapiUpdateCheckIntervalMinutes { get; set; } = 5;
/// <summary>Update notification mode: "Off", "Once", or "Repeating" (re-announce each in-game day).</summary>
public string SmapiUpdateNotify { get; set; } = "Once";
/// <summary>Automatically restart after the next save when a newer in-major SMAPI is found.</summary>
public bool SmapiAutoRestartOnUpdate { get; set; } = false;
```

**File:** `mod/JunimoServer/Services/Settings/ServerSettingsLoader.cs` — add typed accessors in the runtime region, with:

- `SmapiUpdateNotify` parsed to an enum via the existing `Enum.TryParse` pattern (mirror `ParseLobbyMode`, line 219), defaulting to `Once`.
- `SmapiUpdateCheckIntervalMinutes` clamped `[1, 1440]` via a `ClampInterval` helper mirroring `ClampBroadcastPeriod` (line 228).

**Env overrides (optional, follow the `Env.VerboseLogging` nullable-override precedence at `ModEntry.cs`):** add to `Env.cs` using the existing `ParseNullableBool`/`ParseInt` helpers — `SMAPI_AUTO_RESTART`, `SMAPI_UPDATE_NOTIFY`, `SMAPI_UPDATE_CHECK_INTERVAL_MINUTES`. **Do not** add these to `docker-compose.yml`'s env block (keep it lean); document as optional. The pin switch remains the existing `SMAPI_VERSION` — no new pin env. Defaults live in `server-settings.json` per the `Env.cs:7` doctrine ("Game configuration has moved to server-settings.json").

---

## Part 4 — Build-time decoupling (minimal split)

**File:** `docker/Dockerfile`.

- **Keep** `ARG SMAPI_VERSION=4.5.2` as the **build** pin (compile-dependency stage, lines 2/21/116/137). Renovate's custom regex manager (`renovate.json:193-200`) matches the string `ARG SMAPI_VERSION=<digits>` — keeping the `ARG` line verbatim means Renovate's SMAPI bump keeps working **unchanged**. (Verified: the regex keys on `ARG`, not `ENV`, so the runtime `ENV` changes below are invisible to it.)
- **Add** to the runtime stage (stage 4, near line 137): `ARG SMAPI_MAJOR=4` → `ENV SMAPI_MAJOR=${SMAPI_MAJOR}`; and `ENV SMAPI_DEFAULT_VERSION=${SMAPI_VERSION}` — **derive the fallback from the build ARG, never a second literal**. If it were a hardcoded `4.5.2`, Renovate would bump the `ARG` to 4.5.3 and leave the fallback at 4.5.2, so an offline AUTO container would install an _older_ SMAPI than the mod was compiled against. Deriving it keeps one source of truth and one Renovate bump.
- **Change** the runtime `ENV SMAPI_VERSION=${SMAPI_VERSION}` (line 137) to **empty** (`ENV SMAPI_VERSION=`) so default runtime behavior is AUTO. Operators pin with a compose env entry / `-e SMAPI_VERSION=...`. (The build `ARG` is consumed in stage 2 before this; emptying the _runtime_ ENV does not affect the build.)
- **Add `jq`** to the runtime apt layer (lines 141-172) — verified absent today.

**Renovate verification (the user's explicit concern):** after the change, the regex manager's `matchStrings` (`renovate.json:197`, `ARG SMAPI_VERSION=(?<currentValue>[0-9.]+)`) still finds exactly one match (`ARG SMAPI_VERSION=4.5.2`) in `docker/Dockerfile` and one in `Dockerfile.test-client`, unchanged. The new `ENV SMAPI_MAJOR`/`ENV SMAPI_DEFAULT_VERSION` lines do **not** match the `ARG `-anchored pattern, so Renovate ignores them (correct — `SMAPI_MAJOR` should only change on a deliberate major migration, and `SMAPI_DEFAULT_VERSION` rides the `ARG`). `SMAPI_DEFAULT_VERSION=${SMAPI_VERSION}` is a Docker build-arg reference, not a digit literal, so it is structurally un-matchable by the regex — no risk of Renovate touching it. No `renovate.json` edit needed.

**`docker/Dockerfile.test-client`** — **no change needed.** Its `ARG SMAPI_VERSION` is already `4.5.2`, matching the server build version (the two share a `/data/game` volume, so their compile-time SMAPI must match — and it already does; Renovate's regex manager bumps both Dockerfiles together). The test-client keeps its existing runtime `init_smapi` (build-time-pinned via runtime `ENV SMAPI_VERSION=${SMAPI_VERSION}`) — this feature does **not** convert the test-client to AUTO mode.

**Out of scope (leave untouched):** `docker/modern/Dockerfile` (4.5.1, Renovate-excluded experimental — `ignorePaths` `renovate.json:26`) and its `docker/modern/rootfs/opt/bin/start-game.sh` — the experimental image keeps build-time-pinned behavior. Defer renaming `ARG SMAPI_VERSION` → `SMAPI_BUILD_VERSION` (would force a lockstep `renovate.json:196-197` change for no functional gain here).

**File:** `mod/JunimoServer/manifest.json` — **remove** the `"MinimumApiVersion": "3.0.0"` line entirely. Verified safe: SMAPI's resolver checks `MinimumApiVersion?.IsNewerThan(apiVersion) == true`, so a null/absent field skips the check and the mod loads on any version. Our canonical major-pin (shell filter + mod `_compiledMajor` guard) is the sole compatibility gate.

---

## Part 5 — Keep the shared volume on one SMAPI version (runtime-guard skew)

**The risk is at runtime, not build time.** The server, test-client, and steam-auth containers all mount the **same** `server_game-data` volume at `/data/game` (`ServerContainer.cs:251`, `GameClientContainer.cs:177`, `SharedSteamAuth.cs:125`; both `*Options.cs` default `GameDataVolume = "server_game-data"`). There is exactly **one** SMAPI install in that volume. The two build `ARG`s already agree — `docker/Dockerfile` and `docker/Dockerfile.test-client` both pin `4.5.2` (Renovate keeps them in lockstep via the shared regex manager). So there is no compile-time skew to fix.

The skew this work *introduces* is dynamic: after this change the server's `init_smapi` uses a **version-stamp** reinstall guard (reinstall when the stamp ≠ `RESOLVED`), while the test-client keeps the bare `[ -e "$SMAPI_EXECUTABLE" ]` guard (`docker/rootfs-test-client/startapp.sh:53`) — it never re-checks once the binary exists. A server in **AUTO** mode that resolves a newer 4.x than the test-client's pinned value would reinstall on every start while the client never adapts, flapping the shared install. So in tests the server's AUTO path must stay dormant, per the project's "one shared resource ⇒ one source of truth" principle (cf. `orthogonal-fields.md`, `protocol-invariant-not-file-workaround.md`).

**Fix — pin one SMAPI version into every container that mounts the volume, so AUTO never runs in tests.** Both `ServerContainer` and `GameClientContainer` read `SMAPI_VERSION` at runtime (the server via the rewritten `init_smapi`; the client via `rootfs-test-client/startapp.sh:56`). The harness resolves a single test SMAPI version once and passes it to both:

- Add a single source: `TestEnvLoader.Get("SMAPI_VERSION") ?? "4.5.2"` (a `TestEnv.SmapiVersion` accessor; surface in `.env.test` / `.env.test.example`).
- `tests/JunimoServer.Tests/Containers/ServerContainer.cs` (env block, currently lines 257-272): `.WithEnvironment("SMAPI_VERSION", testSmapiVersion)`.
- `tests/JunimoServer.Tests/Containers/GameClientContainer.cs` (env block at line 177-190): `.WithEnvironment("SMAPI_VERSION", testSmapiVersion)` — the **same** value.
- The build `ARG`s are **already aligned** (both `4.5.2`); no Dockerfile change is needed for them. Renovate bumps both Dockerfiles together via its regex manager, so they stay aligned going forward.

**Result:** with both containers PIN-pinned to one value, the server's AUTO/version-stamp path never runs in tests, so the shared volume is deterministic and identical regardless of boot order; server and client always run the same SMAPI; nothing flaps. Update behavior is exercised via the `SMAPI_FAKE_LATEST` seam, not by real GitHub polling in unrelated tests.

**Note on the shared-volume race in general:** even with one version, two containers booting simultaneously into a fresh volume could both enter `install_smapi`. The existing code already tolerates this (the installer is idempotent and `Mods/`-preserving; the loser's reinstall is a no-op once the stamp matches). The version-stamp + atomic state-file write don't worsen it. If a fresh-volume double-install ever proves harmful, a lock file under `/data/game` is the follow-up — out of scope here, flagged.

---

## Part 6 — Operator documentation

New operator-facing behavior must be documented or the feature is only half-shipped (per `verify-documented-config-is-consumed.md` — every documented knob needs a consumer, and here every consumer needs a doc).

- **`docs/admins/operations/upgrading.md`** — the "Updating Game Files" section (lines 58-88) currently tells operators to delete the `game-data` volume to get "SMAPI updates." Rewrite to reflect the new model:
    - Default (AUTO): the server checks for and installs the latest SMAPI in the supported major **on every restart**, and (if enabled) notifies/auto-restarts while running — no volume deletion needed.
    - Pinning: set `SMAPI_VERSION=<exact tag>` to freeze SMAPI and disable all auto behavior; the tag must be a real [SMAPI release](https://github.com/Pathoschild/SMAPI/releases) or the container will fail to start.
    - The installed/resolved SMAPI version and the pinned flag live in `/data/game/.smapi-state.json` (the `info` console command reports the *mod* version and runtime state — `ServerCommand.cs:98-106` — not the SMAPI version). Optionally, this feature could add a SMAPI-version line to `info`; if so, call it out as a deliberate addition rather than implying it already exists.
- **`docs/admins/configuration/environment.md`** (the env reference the upgrading doc links to) — **add** `SMAPI_VERSION` (pin + disable auto; verified absent today), and the optional `SMAPI_AUTO_RESTART` / `SMAPI_UPDATE_NOTIFY` / `SMAPI_UPDATE_CHECK_INTERVAL_MINUTES` overrides. Follow the file's `| Variable | Description | Default |` table convention. Note these mirror the `server-settings.json` keys (`SmapiAutoRestartOnUpdate`, `SmapiUpdateNotify`, `SmapiUpdateCheckIntervalMinutes`) and that the settings file is the primary home.
- **`docs/admins/configuration/server-settings.md`** — in the "Server Runtime Settings" table (same `| Setting | Description | Default |` table where `MaxPlayers`/`CabinStrategy`/`NetworkBroadcastPeriod` live), add the four new `Smapi*` keys with their defaults and the `Off`/`Once`/`Repeating` enum. Also add them to the example file `.local-container/settings/server-settings.json`'s `Server` block.

(Filenames confirmed: `docs/admins/configuration/environment.md` is referenced from `upgrading.md:19`; the settings reference is `docs/admins/configuration/server-settings.md`.)

**Shell / Docker:**

- `docker/rootfs/startapp.sh` — rewrite `init_smapi()`; add `resolve_smapi_version()`, `fetch_latest_in_major()`, `write_smapi_state()`; replace the binary-exists guard with a version-stamp comparison.
- `docker/Dockerfile` — add `jq`; add `ARG/ENV SMAPI_MAJOR=4` and `ENV SMAPI_DEFAULT_VERSION=${SMAPI_VERSION}` (derive from the build ARG, never a second literal — see Part 4); set runtime `ENV SMAPI_VERSION=` empty; keep build `ARG SMAPI_VERSION=4.5.2`.
- `mod/JunimoServer/manifest.json` — remove `MinimumApiVersion`.

**Mod (C#):**

- `mod/JunimoServer/Services/UpdateCheck/SmapiUpdateService.cs` — NEW (`: ModService`, auto-discovered).
- `mod/JunimoServer/Services/UpdateCheck/SmapiReleaseClient.cs` — NEW (`static HttpClient` + try/catch per `NetworkHelper`; no retry loop; `Env.IsTest` + `SMAPI_FAKE_LATEST` seam).
- `mod/JunimoServer/Services/UpdateCheck/SmapiState.cs` — NEW (state-file POCO + fail-safe loader).
- `mod/JunimoServer/Services/Settings/ServerSettings.cs` — 4 new fields on `ServerRuntimeSettings`.
- `mod/JunimoServer/Services/Settings/ServerSettingsLoader.cs` — 4 accessors + notify-enum parse + `ClampInterval`.
- `mod/JunimoServer/Env.cs` — optional env overrides + `SMAPI_FAKE_LATEST` test seam constant.

**Tests / skew fix (Part 5):**

- `tests/JunimoServer.Tests/Containers/ServerContainer.cs` — pin `SMAPI_VERSION` (one line).
- `tests/JunimoServer.Tests/Containers/GameClientContainer.cs` — pin the **same** `SMAPI_VERSION` (one line).
- `.env.test` / `.env.test.example` / `TestEnvLoader` — expose the single test pin value.
- (`docker/Dockerfile.test-client` needs no change — its `ARG SMAPI_VERSION` is already `4.5.2`, aligned with the server.)

**Docs:**

- `docs/admins/operations/upgrading.md` — rewrite "Updating Game Files" for the AUTO/pin model.
- `docs/admins/configuration/environment.md` — document `SMAPI_VERSION` + optional override env vars.
- `docs/admins/configuration/server-settings.md` (+ example `.local-container/settings/server-settings.json`) — document the four `Smapi*` keys.

No `ModEntry.cs` change (reflection auto-discovers the service). No `renovate.json`, `docker-compose.yml`, `docker/modern/**`, or steam-service change.

---

## Verification (E2E-only project; helpers verified by inspecting real runs)

**Shell (exercisable without launching the game — inspect logs + `docker exec`):**

1. **Cache fix:** clean volume + AUTO → logs `Installing SMAPI <resolved>`, stamp written. Restart → `already current, skipping`. `docker exec` overwrite `/data/game/.smapi-version` with `4.0.0`, restart → reinstall triggers.
2. **AUTO resolution:** with network, resolved == latest 4.x (cross-check the GitHub API by hand); `.smapi-state.json` shows `pinned:false` and correct `major`.
3. **Offline fallback:** block egress → falls back to `SMAPI_DEFAULT_VERSION`, logs the fallback line, still `pinned:false`. Also test a reachable-but-empty result (`SMAPI_MAJOR=99`) → same fallback, no bogus install.
4. **Pin (valid):** `-e SMAPI_VERSION=4.5.1` → installs 4.5.1, `.smapi-state.json` `pinned:true`, `major:4`, no `api.github.com` call in logs.
5. **Pin (cross-major truthfulness):** `-e SMAPI_VERSION=5.0.0` (if/when it exists) → `.smapi-state.json` reports `major:5` (derived from resolved, not build `SMAPI_MAJOR`).
6. **Renovate:** dry-run / inspect — confirm Renovate still detects `ARG SMAPI_VERSION` and proposes a bump that touches only that line, leaving `ENV SMAPI_DEFAULT_VERSION=${SMAPI_VERSION}` (un-matchable) and `ENV SMAPI_MAJOR` alone.
7. **Atomic state write:** the state file is written via `.tmp` + `mv` — a concurrent `cat` never observes a partial JSON.

**Skew fix (Part 5) — the user's explicit concern:** 8. **Shared-volume consistency:** boot server then client (and the reverse order) against the shared `server_game-data` volume → `docker exec` both, confirm identical `/data/game/.smapi-version` and `StardewModdingAPI` version regardless of boot order. Repeat after deleting the volume to test the fresh-install path. Confirm no flapping: restart both repeatedly, the stamp never changes (both resolve the same pinned version).

**Mod (use `Env.IsTest` + `SMAPI_FAKE_LATEST` for determinism):** 9. **Pin/disabled suppresses mod:** logs the disabled line, never queries GitHub, no subscriptions wired. 10. **Notify=Once:** AUTO, fake-latest higher, interval 1 min → chat + `Info` "please restart" appears **exactly once** across multiple intervals (idempotency), and again only if a _newer_ version appears. 11. **Notify=Repeating:** same, but re-announces once per in-game `DayStarted` while pending; stops once running == latest. 12. **Auto-restart:** enable flag, fake-latest higher, trigger a save → "restarting after next save" broadcast → on `Saved`, `Environment.Exit(0)` → container restarts → `init_smapi` reinstalls → comes back healthy via the existing `HEALTHCHECK` (`docker/Dockerfile:249`). Confirm the `Saved` handler is wired only with the flag on, and the notify "please restart" message does not also fire. 13. **Major guardrail:** `SMAPI_FAKE_LATEST=5.0.0`, compiled major 4 → neither action fires; at most a `Trace` note. 14. **Title-screen safety:** with a higher fake-latest, confirm a detection that lands **before** a save is loaded logs to console but does **not** attempt a chat broadcast (no exception, no `GetMultiplayer` access); the chat message appears only once the world is ready (`Game1.gameMode == 3`). 15. **No test poison:** confirm the service emits zero `LogLevel.Error` lines and tick cadence stays steady while a check runs (`debugging.md`).
