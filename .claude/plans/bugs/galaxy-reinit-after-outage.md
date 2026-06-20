# Bug Fix: Galaxy session not re-established after total connectivity loss

## Problem

The Steam-CM-flap fix (shipped in PR #391, commit `3aa6a15` "fix(auth): recreate Steam lobby on GameServer reconnect") recreates the Steam lobby on Steam reconnect. It does not cover a **total connectivity loss**, which drops Steam *and* Galaxy at once. Steam auto-reconnects and the lobby is recreated, but Galaxy never re-authenticates: the GameServer-mode `onLost` handler only flips connection flags, so the `GalaxyNetServer` is left pointing at a dead Galaxy session. Vanilla Steam clients decode the invite code → reach a dead Galaxy lobby → fail to join, even though the Steam lobby pointer is now fresh.

Scope: re-establish Galaxy auth (and re-stamp the current `SteamLobbyId` into the recovered Galaxy lobby) after Galaxy auth is lost on the headless GameServer. Out of scope: the Steam-lobby recreation path (already shipped); LAN/IP transport (unaffected — no Galaxy).

## The unknown this fix is blocked on

The Galaxy SDK (`GalaxyCSharp.dll` / `Galaxy64.dll`) is closed-source. We do not know whether it **re-fires** `OnAuthLost` / `onStateChange` after a headless-server connectivity outage, or whether it goes silent after a single `OnAuthLost`. That single fact picks the design:

- **Re-fires** → callback-driven fix (Design A). Pure event-driven, no polling.
- **Goes silent** → poll-driven fix (Design B). A periodic liveness probe re-signs-in.

Diagnostic instrumentation to resolve this is **already in the tree** (added alongside the Steam fix; marked `remove once the design is chosen`):

- `auth_galaxy_lost` carries `invocation, networkingSet` (`AuthService.cs` GameServer-mode `onLost`).
- `auth_galaxy_state_change` carries `invocation, operationalState, signedIn, loggedOn, networkingSet`, logged **before** the `Networking != null` early-return so a suppressed recovery callback is still observable (`AuthService.cs` GameServer-mode `onStateChange`).
- `steam_session_lost` / `steam_session_connected` (`isReconnect`) bracket the Steam side for correlation (`SteamGameServerService.cs`).

**This fix cannot be designed until the outage repro below has run and reported which case holds.**

## Ground truth

(Line numbers as of the Steam-fix merge; re-grep before editing.)

- **The asymmetry is the root cause.** Client-mode `onGalaxyAuthLost` (`AuthService.cs:1114`) already re-signs-in: `GalaxyInstance.User().SignOut()` then `_instance.SignIntoGalaxy()`. GameServer-mode `onLost` (`AuthService.cs:922`) does **not** — it only sets networking/connection-finished/galaxy-connected flags. The auth listener is registered unconditionally (`GalaxyAuthListener` ctor, `decompiled/.../Listeners/GalaxyAuthListener.cs:27`) and the deferred-init path constructs it (`AuthService.cs:819`, in `PerformDeferredInitialization`), so `OnAuthLost` *does* reach this body — the body just doesn't recover.
- **Vanilla doesn't re-sign-in either.** `GalaxyHelper.onGalaxyAuthLost` (`decompiled/.../GogGalaxy/GalaxyHelper.cs:92`) only sets `ConnectionFinished = true`. So the client-mode reinit is a mod enhancement, not a vanilla contract — copying it into GameServer mode is consistent with existing mod behavior.
- **`onStateChange` early-return blocks recovery re-stamp.** `AuthService.cs:969` (`if (steamHelper.Networking != null) return;`). After first login `Networking` is set, so a recovery logon never reaches the `& 2` block (`:978`) that calls `TryLateAddGalaxyServer()` → `UpdateGalaxyLobbyWithSteamLobbyId()`. This guard mirrors vanilla (`GalaxyHelper.onGalaxyStateChange`, decompiled `:63` `if (networking == null)`) — its intent is "create networking once", but it also gates the lobby re-stamp.
- **`TryLateAddGalaxyServer` also early-returns on an existing server.** `AuthService.cs:320-324` returns if a `GalaxyNetServer` is already in the `servers` list. On recovery the (now-dead) `GalaxyNetServer` is still present, so a late-add is skipped — the recovery path must **re-stamp the existing server** (call `UpdateGalaxyLobbyWithSteamLobbyId()` directly), not rely on late-add.
- **`SignIntoGalaxy` needs the sidecar.** `AuthService.cs:1196` → `GetEncryptedAppTicketSteam` → `_steamAppTicketFetcher.GetTicket()` (HTTP to the steam-auth sidecar). During a total connectivity loss the sidecar is unreachable, so re-sign-in must run *after* connectivity returns and must tolerate a ticket-fetch failure (retry, not throw-and-die).
- **Galaxy callbacks pump only while `_galaxyInitComplete`.** `SteamHelperUpdate_Prefix` (`AuthService.cs`) calls `GalaxyInstance.ProcessData()` gated on `_galaxyInitComplete`. The Steam fix deliberately leaves `_galaxyInitComplete == true` on Steam-session loss, so the Galaxy callback channel stays live — a Design A recovery callback can still arrive, and a Design B poll has a tick to run on.
- **All four auth callbacks run on the game thread.** Steam via `GameServer.RunCallbacks()`, Galaxy via `GalaxyInstance.ProcessData()` in `SteamHelperUpdate_Prefix`. No locking needed for the recovery state machine.
- **No E2E coverage, by design.** No `tests/` harness drops or recovers a Galaxy session; there is no toxiproxy/netem layer. Same gate as the Steam fix: the manual outage repro is the verification, not xUnit.

## Step 0 — Run the repro to resolve the unknown (gates everything below)

Reproduce a total connectivity loss and read the diagnostics. Cut the container's network entirely (e.g. `docker network disconnect <net> <container>`, then reconnect to restore) — a full cut is the *intended* path here because we want both Steam *and* Galaxy to drop together. A partial cut that only severs Steam's CM connection is the already-shipped #391 scenario, not this one.

1. Start the server. Capture the invite code, the Steam lobby `<L1>`, and confirm a Steam client joins.
2. Cut the network (as above). Wait for `auth_galaxy_lost invocation=1` and `steam_session_lost`.
3. Restore the network. Wait up to ~5 min.
4. Read `infrastructure.jsonl`:
   - `steam_session_connected isReconnect=true` → Steam side recovered (expected; that's the shipped fix).
   - **Decision point — Galaxy:** does `auth_galaxy_lost invocation≥2` **or** any `auth_galaxy_state_change` with `loggedOn=true, networkingSet=true` appear after restore?
     - **Yes → Design A** (the SDK re-fires; build the callback-driven fix).
     - **No → Design B** (the SDK is silent; build the poll-driven fix).

Record the observed `invocation` sequence and any post-restore `auth_galaxy_state_change` rows in the PR description — that evidence is the justification for whichever design is chosen.

> **Resolved 2026-06-19 — chosen design is Steam-reconnect-TRIGGERED, lobby-state-GATED (Design C).**
> No auth callback fires (Design A out) and the `IUser` auth-liveness members stay stale-`True` (Design
> B's *auth* poll out) — BUT the Galaxy *lobby* state IS observable: `GalaxySocket.Connected`
> (`lobby != null`) goes false on a full outage and true on a healthy server, and is the re-login gate.
> Recovery has two decoupled halves: an unconditional Steam-lobby re-stamp (the common-path fix) and a
> re-login gated on `Connected == false` (a verified fallback for when Galaxy's own auto-recreate
> doesn't restore the lobby). Automated by `GalaxyOutageReproTests`
> (`tests/JunimoServer.Tests/GalaxyOutageReproTests.cs`; `make test FILTER=GalaxyOutageRepro`). Two
> incidental bugs were fixed along the way: the `SteamGameServerService` Error→Warn poison fix, and the
> remote-host API-unreachable-after-reconnect handling in the repro.

## Design A (callback) — NOT VIABLE

The SDK fires no callback on a total connectivity loss (DETECTION FINDINGS #1). No event to drive recovery.

## Design B (Galaxy-side poll of IUser) — NOT VIABLE for *auth*, but the LOBBY is pollable

All three `Galaxy.Api.IUser` liveness members (`SignedIn()`, `IsLoggedOn()`, `GetGalaxyID()`) stay
`True`/valid through the outage (DETECTION FINDING #2) — so *auth* state is not pollable.

**Correction (verified 2026-06-19, two outage runs): `GalaxySocket.Connected` does NOT stay true.**
The earlier DETECTION #3 ("`GalaxySocket.Connected`/`GalaxyNetServer.connected()` stay `True` through
the outage") was a *deduction* from "no callbacks fire", and it was wrong. `GalaxySocket.Connected` is
`lobby != null`, and on a total connectivity loss Galaxy's **lobby-left callback DOES fire**
(`LOBBY_LEAVE_REASON_CONNECTION_LOST` → `onGalaxyLobbyLeft` nulls `lobby`), so `Connected` flips
**false** within ~3s of the cut and stays false until the lobby is rebuilt — while reading **true** on a
healthy server (idle, with peers, or mid-re-login). The detection phase only instrumented the *auth*
callbacks (`OnAuthLost`/`onStateChange`), never the *lobby-left* callback. So there IS a reliable
Galaxy-side signal — the lobby's own connected state — and it is the gate the chosen design uses.

## Design C — Steam-reconnect-triggered (CHOSEN)

`SteamGameServerService.OnSteamServersConnected` fires on every (re)connect and invokes
`AuthService.OnServerSteamIdReceived` (`SteamGameServerService.cs:205,218`). On a total connectivity loss Steam
drops and reconnects with `isReconnect=true`. In that handler's reconnect branch the fix (a) always
recreates the Steam lobby and re-stamps it into the live Galaxy lobby, and (b) calls
`TryBeginGalaxyReSignInGated`, which re-establishes Galaxy auth **only when `GalaxySocket.Connected`
is false** (lobby genuinely dead) — skipping the disruptive re-login when the lobby is already up.

The recovery action (already implemented during the poll attempt, reuse it):
1. **Off-thread re-sign-in.** `SignIntoGalaxy`'s app-ticket fetch is a *blocking* sidecar HTTP call (up
   to 30s) — it must NOT run on the game thread. `BeginGalaxyReSignIn` fetches the ticket in a `Task.Run`
   (generation-guarded against a torn-down session), then sets `_pendingGalaxyReSignIn`; the game-thread
   `SteamHelperUpdate_Prefix` runs the non-thread-safe `SignInSteam` from the pending flag. Mirrors
   `CreateSteamLobbyViaHttpAsync`.
   **MUST `SignOut()` FIRST** (verified 2026-06-19): after a total connectivity loss the SDK still reports
   `SignedIn()==true` (it never saw connectivity drop — the same stale state that defeats every detection
   signal), so a bare `SignInSteam` throws `"already signed in"` and never re-authenticates. `SignOut()`
   clears the stale state so the fresh sign-in takes. Mirrors the client-mode `onGalaxyAuthLost` path.
2. **Remove the dead `GalaxyNetServer` BEFORE `SignOut()`** (verified 2026-06-19): its per-tick
   `receiveMessages()` → `GalaxySocket.Receive` keeps running while signed out and can hit the SDK at
   ERROR. The live per-tick call when `lobby == null` is `tryCreateLobby` → `Matchmaking().CreateLobby`
   (`GalaxySocket.cs:425-428`, fired off the recreate-timer), whose catch logs `Game1.log.Error`
   (`:173`); `ReadP2PPacket`/`GetPingWith` only throw in a brief `lobby != null`-but-session-dead
   window (`Receive` early-returns before `ReadP2PPacket` once `lobby == null`, `:423-431`). Those
   ERRORs also poison E2E tests. `TryRemoveGalaxyServer` (`stopServer()` + remove from the
   GameServer's `servers` list, via reflection, game thread) halts all per-tick Galaxy networking for
   the re-auth window. Note this remove-then-recreate is only on the **re-login** path; the gate
   (below) only takes it when the lobby is already dead, so it never tears down a healthy live socket.
   (Verified clean 2026-06-20: a `make test FILTER=GalaxyOutageRepro` run took the re-login branch
   — `auth_galaxy_relogin_attempt{galaxyConnected:false}` → `auth_galaxy_recovered` — with zero
   ERROR/FATAL lines, so the dead server's per-tick `tryCreateLobby` does not surface a scannable
   ERROR during the held outage.)
3. **Wait for the re-login to log on, then re-set `GalaxyConnected` and re-stamp** (verified 2026-06-19):
   - A fixed delay is wrong — the post-outage re-login is async; gate on `IUser.IsLoggedOn()` flipping
     true (a FRESH login DOES flip it; it was only stale during the outage because no fresh login had
     happened). With a safety timeout.
   - **MUST re-set `SteamHelper.GalaxyConnected = true` before re-creating the server.** `SteamNetHelper.CreateServer`
     (decompiled `SteamNetHelper.cs:172-174`) returns null + logs `"Could not create a Galaxy server:
     not logged on"` at ERROR when `GalaxyConnected == false`. `onLost` set it false on the outage, and
     the re-login's `onStateChange` `& 2` block (which sets it true) is blocked by its own
     `Networking != null` early-return — so the recovery sets it directly via `SetSteamGalaxyConnected(true)`.
     This was the actual root cause of the recovery's "not logged on" failure, NOT a slow re-auth.
   - Then `TryLateAddGalaxyServer` re-creates a FRESH `GalaxyNetServer` (the dead one was removed in
     step 2) and `UpdateGalaxyLobbyWithSteamLobbyId` re-stamps `_steamLobbyId` into it.

### Re-login SEVERS connected clients, so it is GATED (not unconditional) (verified 2026-06-19)

Re-login does `SignOut` + remove/recreate the `GalaxyNetServer` → a NEW Galaxy lobby → the old lobby's
P2P session (which a connected client was joined through) is torn down. A two-sided check (server
`/players` AND client `GetState().IsConnected`) confirmed: after a healthy-state re-login the client
reports `IsConnected=False` while the server's `/players` entry lingers stale. So **unconditional**
re-login on every Steam reconnect would kick all connected players on a transient Steam-CM-only flap
(Galaxy fine). It is only acceptable when Galaxy is actually dead.

**The discriminator (resolved): `GalaxySocket.Connected` (`lobby != null`).** It reads **false**
during a total connectivity loss (the lobby-left callback fires) and **true** on a healthy server — including
idle, with peers, and mid-re-login (a fresh lobby comes up near-instantly). The gate reads it at the
Steam reconnect and only re-logs-in when it is false. Verified across runs.

### Final design — two decoupled halves (verified 2026-06-19)

Recovery splits into a cheap always-on half and a gated disruptive half:

1. **Re-stamp (UNCONDITIONAL — the common-path fix).** `OnSteamServersLost` resets
   `_lobbyCreationAttempted`, so every reconnect runs `CreateSteamLobbyViaHttpAsync`, which mints a
   fresh Steam lobby and sets `_pendingGalaxyLobbyUpdate`; the per-tick prefix then re-stamps it into
   whichever Galaxy lobby is live (`UpdateGalaxyLobbyWithSteamLobbyId`). This is `setLobbyData` — it
   does NOT rebuild the lobby and does NOT disrupt clients. It fixes the stale Steam-lobby pointer
   regardless of how the Galaxy lobby itself came back.
2. **Re-login (GATED on `Connected == false`, via `TryBeginGalaxyReSignInGated`).** When the Galaxy
   lobby is genuinely down at reconnect, the full `SignOut` + remove + re-sign-in + re-add
   (`BeginGalaxyReSignIn`, the off-thread ticket fetch + game-thread `SignInSteam` + `IsLoggedOn()`
   wait + `SetSteamGalaxyConnected(true)` + `TryLateAddGalaxyServer` + re-stamp described above)
   rebuilds it. When `Connected == true` the gate SKIPS, emitting `auth_galaxy_relogin_skipped`.

Three recovery branches, all verified:
- **Re-stamp** is the actual fix on every observed reconnect (fresh `auth_steam_lobby_created`).
- **Vanilla Galaxy auto-recreate** (`GalaxySocket.Receive` → `tryCreateLobby`, runs per-tick while the
  `GalaxyNetServer` is present, independent of `SteamHelper.GalaxyConnected`) usually rebuilds the
  lobby within ~seconds of restore — *before* the Steam-reconnect callback fires — so the gate most
  often sees `Connected == true` and skips re-login. Confirmed in multiple outage runs.
- **Re-login is a verified INDEPENDENT fallback, NOT dead code.** With vanilla auto-recreate
  experimentally suppressed, the lobby stayed down at reconnect, the gate saw `Connected == false`,
  re-login fired, and it recovered the lobby on its own (`auth_galaxy_recovered` + a fresh invite
  code). So it correctly covers the case where auto-recreate can't/doesn't recover (e.g. wedged Galaxy
  auth) while costing nothing on the common path (the gate skips it).

`null` from the probe (no `GalaxyNetServer` to read) is treated as dead → re-login; it cannot occur on
a healthy flap (which reliably reads `true`), so the recover-biased default is safe.

### Compatibility verification (per `plan-discipline.md`)

- **LAN/IP transport:** unaffected — `OnServerSteamIdReceived`'s Galaxy branch only runs when
  `_galaxyInitComplete` (set only with `STEAM_AUTH_URL`); LAN never reaches it.
- **Passwordless servers:** `OnServerSteamIdReceived` is wired in the `AuthService` ctor (unconditional),
  not in `PasswordProtectionService`, so `harmony-patch-reachability.md` is satisfied.
- **`_steamSessionGeneration`:** `BeginGalaxyReSignIn` captures and re-checks the generation around its
  `Task.Run`, mirroring the existing lobby-recreate path — a torn-down session can't commit a stale re-login.
- **Connected players on a real outage:** during the outage they're already disconnected (network down), so
  re-login can't make their session worse. On a Steam-CM-only flap the gate reads `Connected == true`
  and SKIPS re-login, so connected players are not severed (verified by the no-op gate test).

## Instrumentation: KEEP (the repro test now consumes it)

`GalaxyOutageReproTests` is a permanent regression asset that asserts on these events (read from
`infrastructure.jsonl`, since the API is dark during/after the cut on remote hosts), so they have a
real consumer (`simplest-solution.md` "verify a consumer"). The tests read:

- **Outage test:** `steam_session_lost`, `steam_session_connected{isReconnect}`, then the path-agnostic
  recovery markers `auth_galaxy_recovered` **or** `auth_galaxy_relogin_skipped`, plus a post-reconnect
  `auth_steam_lobby_created` (the re-stamp's pointer refresh).
- **Gate test:** `auth_galaxy_relogin_skipped{reason:"galaxy_lobby_connected"}` present and
  `auth_galaxy_recovered` absent (the gate declined re-login on a healthy lobby).

So **keep** the `steam_session_*`, `auth_steam_lobby_created`, and `auth_galaxy_relogin_*` /
`auth_galaxy_recovered` events. The `_connectCount` field still feeds `isReconnect`. The
`auth_galaxy_lost` / `auth_galaxy_state_change` diagnostics (and their `_galaxyAuthLostCount` /
`_galaxyStateChangeCount` counters) are no longer read by the tests — they document the SDK's
no-auth-callback behavior; keep them as low-cost diagnostics or prune in a follow-up, but they are not
load-bearing for the regression.

## Verification

1. **Build:** `dotnet build mod/JunimoServer/JunimoServer.csproj`, then rebuild `sdvd/server:local`.
2. **Outage recovery (regression):** `make test FILTER=TotalConnectivityLoss_RecordsGalaxyReauthSignal` — cuts
   the network with a client connected, restores, and asserts Galaxy recovered by either valid path
   (`auth_galaxy_recovered` or `auth_galaxy_relogin_skipped`) plus a fresh post-reconnect
   `auth_steam_lobby_created`.
3. **Flap safety (gate):** `make test FILTER=GalaxyReloginGate_WhileHealthy_SkipsAndKeepsClientConnected`
   — drives the gate on a healthy connected server and asserts it SKIPS re-login
   (`auth_galaxy_relogin_skipped`, no `auth_galaxy_recovered`), the client stays connected on both
   sides, and the invite code is unchanged.
4. **Full suite:** `make test` — confirms the first-connect flow and the rest of the suite are unbroken.
