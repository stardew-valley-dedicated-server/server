# Bug Fix: Galaxy session not re-established after a full-NIC outage

## Problem

The Steam-CM-flap fix (shipped in PR #391, commit `3aa6a15` "fix(auth): recreate Steam lobby on GameServer reconnect") recreates the Steam lobby on Steam reconnect. It does not cover a **full-NIC outage**, which drops Steam *and* Galaxy at once. Steam auto-reconnects and the lobby is recreated, but Galaxy never re-authenticates: the GameServer-mode `onLost` handler only flips connection flags, so the `GalaxyNetServer` is left pointing at a dead Galaxy session. Vanilla Steam clients decode the invite code → reach a dead Galaxy lobby → fail to join, even though the Steam lobby pointer is now fresh.

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
- **`SignIntoGalaxy` needs the sidecar.** `AuthService.cs:1196` → `GetEncryptedAppTicketSteam` → `_steamAppTicketFetcher.GetTicket()` (HTTP to the steam-auth sidecar). During a full-NIC outage the sidecar is unreachable, so re-sign-in must run *after* connectivity returns and must tolerate a ticket-fetch failure (retry, not throw-and-die).
- **Galaxy callbacks pump only while `_galaxyInitComplete`.** `SteamHelperUpdate_Prefix` (`AuthService.cs`) calls `GalaxyInstance.ProcessData()` gated on `_galaxyInitComplete`. The Steam fix deliberately leaves `_galaxyInitComplete == true` on Steam-session loss, so the Galaxy callback channel stays live — a Design A recovery callback can still arrive, and a Design B poll has a tick to run on.
- **All four auth callbacks run on the game thread.** Steam via `GameServer.RunCallbacks()`, Galaxy via `GalaxyInstance.ProcessData()` in `SteamHelperUpdate_Prefix`. No locking needed for the recovery state machine.
- **No E2E coverage, by design.** No `tests/` harness drops or recovers a Galaxy session; there is no toxiproxy/netem layer. Same gate as the Steam fix: the manual outage repro is the verification, not xUnit.

## Step 0 — Run the repro to resolve the unknown (gates everything below)

Reproduce a full-NIC outage and read the diagnostics. Cut the container's network entirely (e.g. `docker network disconnect <net> <container>`, then reconnect to restore) — a full cut is the *intended* path here because we want both Steam *and* Galaxy to drop together. A partial cut that only severs Steam's CM connection is the already-shipped #391 scenario, not this one.

1. Start the server. Capture the invite code, the Steam lobby `<L1>`, and confirm a Steam client joins.
2. Cut the network (as above). Wait for `auth_galaxy_lost invocation=1` and `steam_session_lost`.
3. Restore the network. Wait up to ~5 min.
4. Read `infrastructure.jsonl`:
   - `steam_session_connected isReconnect=true` → Steam side recovered (expected; that's the shipped fix).
   - **Decision point — Galaxy:** does `auth_galaxy_lost invocation≥2` **or** any `auth_galaxy_state_change` with `loggedOn=true, networkingSet=true` appear after restore?
     - **Yes → Design A** (the SDK re-fires; build the callback-driven fix).
     - **No → Design B** (the SDK is silent; build the poll-driven fix).

Record the observed `invocation` sequence and any post-restore `auth_galaxy_state_change` rows in the PR description — that evidence is the justification for whichever design is chosen.

## Design A — callback-driven (if the SDK re-fires)

1. **GameServer-mode `onLost` (`AuthService.cs:922`)** — after the existing flag writes, attempt re-sign-in, tolerating sidecar-unreachable:
   - `try { GalaxyInstance.User().SignOut(); } catch { /* log Trace */ }`
   - `try { _instance?.SignIntoGalaxy(); } catch (Exception ex) { /* log Warn; rely on the next OnAuthLost re-fire to retry */ }`
   - Idempotent by construction: if `SignIntoGalaxy` throws (sidecar still down), the SDK's next `OnAuthLost` re-fire retries. Do **not** loop or sleep here — this is a game-thread callback.
2. **GameServer-mode `onStateChange` (`AuthService.cs:969`)** — split the "create networking once" concern from the "re-stamp lobby on every logon" concern. Keep the `Networking != null` guard around the *networking creation* (`:981` `SetSteamNetworking`), but move the recovery re-stamp out from under it: on `(operationalState & 2) != 0`, always call `UpdateGalaxyLobbyWithSteamLobbyId()` when `_steamLobbyId != 0`, regardless of whether `Networking` was already set.
3. **Re-stamp the existing server, not a late-add.** On recovery the dead `GalaxyNetServer` is still in `servers`, so `TryLateAddGalaxyServer` no-ops (`:320`). The re-stamp in step 2 calls `UpdateGalaxyLobbyWithSteamLobbyId()` directly (walks `servers` for the existing `GalaxyNetServer`, `:491`) rather than going through late-add. Verify the existing `GalaxyNetServer.setLobbyData` works against a re-authed session, or that the dead server is replaced — the repro is the gate.

## Design B — poll-driven (if the SDK is silent)

1. **Liveness probe in `SteamHelperUpdate_Prefix`** — on a throttled cadence (e.g. every N ticks, not every tick), when `_galaxyInitComplete`, query Galaxy sign-in state and re-sign-in if lost.
   - **Unverified API:** `GalaxyInstance.User().SignedIn()` / `IsLoggedOn()` are standard Galaxy `IUser` members but live in the closed-source `GalaxyCSharp.dll` — confirm they exist and behave as expected against the binary (or via reflection) before depending on them. If neither is queryable, fall back to detecting staleness another way (e.g. a `GetGalaxyID()` that throws). Per `verify-claims.md`, do not ship against an unverified SDK member.
   - This is closer to a band-aid (`retry-is-evidence-of-root-cause.md`) and is justified **only** if Step 0 proves the SDK gives no callback signal — there is genuinely no event to hang a clean fix on. Document that in the code comment.
2. Re-sign-in path and lobby re-stamp: same as Design A steps 1–3 (the recovery action is identical; only the *trigger* differs).

## Cleanup (both designs)

Once the design is chosen and implemented, **remove the diagnostic instrumentation** — it exists only to resolve Step 0:
- `_galaxyAuthLostCount`, `_galaxyStateChangeCount` fields and their `++`/log/`invocation` uses (`AuthService.cs`).
- The `auth_galaxy_state_change` emit and the pre-early-return diagnostic log in `onStateChange`.
- `auth_galaxy_lost`'s `invocation`/`networkingSet` fields revert to `new { mode = "gameServer" }`.
- `_connectCount` and the `steam_session_connected` `connectNumber`/`isReconnect` fields in `SteamGameServerService.cs` — keep `steam_session_lost`/`steam_session_connected` as plain lifecycle events if useful, or remove if not. Decide based on whether the events earn their keep post-diagnosis.
- Update the event catalog (`tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs`) to match whatever survives.

Keep the structured `steam_session_*` / `auth_galaxy_*` *lifecycle* events only if a consumer (UI, future repro) actually reads them; otherwise cut per `simplest-solution.md`.

## Verification

1. **Build:** `dotnet build mod/JunimoServer/JunimoServer.csproj`.
2. **Repro (the gate):** rerun Step 0's outage. After restore, the post-restore log sequence must show Galaxy re-auth (`Galaxy logged on (GameServer mode)` again) and `Galaxy lobby updated with SteamLobbyId: <L2>`. Then reuse the original invite code from the Steam client — MUST join.
3. **E2E:** `make test` — regression only (Galaxy loss is never exercised in tests); confirms the first-connect flow is unbroken.
