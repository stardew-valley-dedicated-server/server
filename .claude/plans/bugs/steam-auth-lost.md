# Bug Fix: Steam Lobby Not Recreated After Steam Disconnect/Reconnect

## Symptom

After the dedicated server loses Steam GameServer connectivity (Steam CM-server flap, brief upstream outage that doesn't kill the container's network) and reconnects, vanilla Steam clients fail to join via the invite code. The Galaxy lobby survives (Galaxy authenticates separately against GOG backends — see "Galaxy survival" below), so the invite code still resolves to it — but the `SteamLobbyId` stamped into Galaxy's lobby metadata points at a Steam lobby that Valve destroyed when the session dropped. Steam clients decode invite → join Galaxy lobby → read `SteamLobbyId` → connect to dead Steam lobby → fail.

## Root cause

`AuthService.cs:357` guards `CreateSteamLobbyViaHttpAsync()` with `if (_lobbyCreationAttempted) return;`. The guard is set after the first creation (line 376) and only cleared in `SteamHelperShutdown_Prefix` (line 811) — i.e. on full mod shutdown.

On reconnect, `SteamServersConnected_t` fires `SteamGameServerService.OnSteamServersConnected` (line 166), which re-assigns `_serverSteamId` (line 168) and invokes `OnServerSteamIdReceived` (line 184). `AuthService.OnServerSteamIdReceived` (line 144) sees `_galaxyInitComplete == true`, falls through to the `else` at line 159, and calls `CreateSteamLobbyViaHttpAsync()`. The guard at line 357 is still `true` from the first session, so the call early-exits. No new Steam lobby is created.

The guard's intent is right (don't create twice). Its signal is wrong: it tracks "have we ever attempted" instead of "is the current attempt still valid."

`RecreateSteamLobby()` (line 499) is a *reactive* recovery path triggered only when `SetSteamLobbyPrivacy` / `SetSteamLobbyData` get a `NoMatch` response. Those calls don't necessarily happen for an extended period after reconnect (only on next privacy change), so the stale `SteamLobbyId` can stay in Galaxy metadata indefinitely.

Fix: invalidate the latch (and the rest of the cached Steam-lobby state) when the Steam session is lost.

## Fix

### 1. `mod/JunimoServer/Services/SteamGameServer/SteamGameServerService.cs`

- [ ] Add an event next to `OnServerSteamIdReceived` (line 164):
  ```csharp
  /// <summary>
  /// Fired when the GameServer loses its Steam server session.
  /// Subscribers should treat any cached Steam lobby/session state as invalid.
  /// </summary>
  public static event Action OnSteamServersLost;
  ```
- [ ] Invoke from `OnSteamServersDisconnected` (line 196) after the existing log lines:
  ```csharp
  OnSteamServersLost?.Invoke();
  ```

### 2. `mod/JunimoServer/Services/AuthService/AuthService.cs`

- [ ] Add a generation counter field next to `_lobbyCreationAttempted` (line 68). `volatile` matches the pattern used by `_pendingGalaxyLobbyUpdate` (line 76) for fields written on the game thread and read from `Task.Run` bodies:
  ```csharp
  /// <summary>
  /// Incremented on every Steam-session invalidation. Task.Run spawn sites
  /// capture this on the game thread and pass it into the body; the body
  /// only commits results if the captured generation still matches at write
  /// time. Closes the race where a Task.Run from a torn-down session would
  /// otherwise overwrite fresh state with stale results.
  /// </summary>
  private static volatile int _steamSessionGeneration;
  ```
- [ ] Subscribe in the constructor next to the existing `OnServerSteamIdReceived` subscription (line 183):
  ```csharp
  SteamGameServerService.OnSteamServersLost += OnSteamServersLost;
  ```
- [ ] Add the handler. Resets four fields. `_galaxyInitComplete` is **not** reset — the Galaxy callback dispatch loop at lines 703-721 is gated on it, and the mod has no Galaxy-reinit path that would set it back to `true` (the existing `onLost` at line 872 doesn't re-init). Bump the generation **before** clearing the other fields so any concurrent Task.Run completion sees the new generation:
  ```csharp
  private static void OnSteamServersLost()
  {
      _monitor.Log("Steam session lost — invalidating cached Steam lobby state", LogLevel.Warn);
      _steamSessionGeneration++;
      _steamLobbyId = 0;
      _lobbyCreationAttempted = false;
      _lastSteamLobbyPrivacy = null;
      _pendingGalaxyLobbyUpdate = false;
  }
  ```
- [ ] Guard the Task.Run write site in `CreateSteamLobbyViaHttpAsync` (line 380). Capture on the game thread before spawning; check inside the body before committing to fields:
  ```csharp
  _lobbyCreationAttempted = true;
  var generation = _steamSessionGeneration;
  _monitor.Log($"Creating Steam lobby via steam-auth service, GameServer ID: {gameServerSteamId}", LogLevel.Info);

  Task.Run(() =>
  {
      // ... existing CreateLobby HTTP call ...
      if (ulong.TryParse(result.lobby_id, out var lobbyId))
      {
          if (generation != _steamSessionGeneration)
          {
              _monitor.Log($"Discarding Steam lobby {lobbyId} from invalidated session (gen {generation}, current {_steamSessionGeneration})", LogLevel.Warn);
              return;
          }
          _steamLobbyId = lobbyId;
          // ... existing log + Emit + _pendingGalaxyLobbyUpdate = true ...
      }
  });
  ```
- [ ] Guard `RecreateSteamLobby`. The capture must happen at the **outer Task.Run spawn site on the game thread**, not inside `RecreateSteamLobby` itself — both callers (`SetSteamLobbyPrivacy:580`, `SetSteamLobbyData:640`) run the recreate logic from inside an already-spawned Task.Run, so capturing inside `RecreateSteamLobby` is already racing with `OnSteamServersLost`. Change the signature to accept the generation:
  ```csharp
  private static bool RecreateSteamLobby(int capturedGeneration)
  {
      // ... existing CreateLobby HTTP call ...
      if (result != null && ulong.TryParse(result.lobby_id, out var newLobbyId))
      {
          if (capturedGeneration != _steamSessionGeneration)
          {
              _monitor.Log($"Discarding recreated Steam lobby {newLobbyId} from invalidated session", LogLevel.Warn);
              return false;
          }
          _steamLobbyId = newLobbyId;
          // ... rest unchanged ...
      }
  }
  ```
  Update both callers to capture **before** entering their outer `Task.Run` and pass it in:
  ```csharp
  // SetSteamLobbyPrivacy (~line 577), SetSteamLobbyData (~line 640)
  var generation = _steamSessionGeneration; // game thread
  Task.Run(() =>
  {
      try { /* existing call */ }
      catch (Exception ex) when (IsLobbyLostError(ex))
      {
          if (RecreateSteamLobby(generation)) { /* retry */ }
      }
  });
  ```

On reconnect: `OnSteamServersConnected` → `OnServerSteamIdReceived` → `CreateSteamLobbyViaHttpAsync` runs to completion → sets `_pendingGalaxyLobbyUpdate = true` (line 414) → next game tick (line 716) calls `UpdateGalaxyLobbyWithSteamLobbyId` and stamps the new ID into Galaxy.

### Why the generation counter is required (not optional)

Without it, this sequence stamps a dead lobby ID into Galaxy:

1. T0: Task.Run #1 in flight, creating lobby `L1`.
2. T1: Steam disconnects. Handler clears `_steamLobbyId`, `_lobbyCreationAttempted`.
3. T2: Steam reconnects. Task.Run #2 spawned → creates lobby `L2`.
4. T3: Task.Run #2 completes first → `_steamLobbyId = L2`. Stamped into Galaxy.
5. T4: Task.Run #1 finally completes → `_steamLobbyId = L1` (overwriting `L2`) → `_pendingGalaxyLobbyUpdate = true` → next tick stamps stale `L1`.

The `NoMatch` reactive path doesn't necessarily recover this: steam-auth's `_currentLobbyId` (`tools/steam-service/SteamAuthService.cs:61`) may still equal `L1`, so subsequent `setPrivacy`/`setLobbyData` calls forward to its SteamKit client against `L1` and may succeed against Valve — even though `L1` is no longer joinable by clients. The stale `L1` then lives in Galaxy until mod restart.

## Galaxy survival

`UpdateGalaxyLobbyWithSteamLobbyId` (line 452) walks `Game1.server`'s servers list to find `GalaxyNetServer` and calls `setLobbyData`. Whether it succeeds after the outage depends on whether Galaxy's session survived.

- **Galaxy auths independently of Steam GameServer.** `SignIntoGalaxy` (line 1123) uses an encrypted Steam app ticket fetched over HTTP from the steam-auth sidecar; Galaxy's runtime traffic targets GOG backends, not Valve CMs.
- **Galaxy loss is observable.** `onLost` (line 872) logs `Galaxy auth lost` and emits the `auth_galaxy_lost` infrastructure event.
- **Galaxy loss is NOT auto-recovered by the mod.** `onLost` flips connection state but does not re-run `SignIntoGalaxy`. No Galaxy-reinit path exists today.

Implication: a Steam-CM-only flap leaves Galaxy untouched, and this fix's reconnect chain works end-to-end. A local-NIC outage drops both — Steam auto-reconnects, but Galaxy stays dead, and `UpdateGalaxyLobbyWithSteamLobbyId` either fails to find a working `GalaxyNetServer` or calls into a dead session (silently caught at lines 485-489). The repro below uses CM-only blocking to isolate this fix from the unhandled Galaxy-reinit gap, which is a separate follow-up.

## Verified assumptions

These are the load-bearing claims behind the fix. Each is decided by reading source (mod or Steamworks.NET) except item 8.

1. **`_initialized` survives a disconnect/reconnect cycle.** Cleared only in `SteamGameServerService.Shutdown` (line 224), not in `OnSteamServersDisconnected`. `_serverSteamId` is reassigned by every `OnSteamServersConnected` (line 168).

2. **The `active` flag stays true across disconnects.** Set at `SteamHelperInitialize_Prefix:332`, never cleared. So `SteamHelperUpdate_Prefix` keeps pumping `GameServer.RunCallbacks()` during the outage — any enqueued reconnect callback will dispatch.

3. **`RunCallbacks()` is invoked on the game thread.** Two call sites — `SteamGameServerService.OnUpdateTicked:71` (SMAPI `UpdateTicked` hook at line 53) and `AuthService.SteamHelperUpdate_Prefix:694` (Harmony prefix on `SteamHelper.Update`) — both run on the game thread.

4. **`SteamServersConnected_t` is the auto-reconnect notification.** Per Valve's `ISteamUser` callback reference: *"Called when a connection to the Steam back-end has been established... should only be seen if the user has dropped connection due to a networking issue or a Steam server update."*

5. **`SteamServersDisconnected_t` only fires after a previously-successful logon and is followed by a matching `SteamServersConnected_t`.** Per Valve: *"Real-time services will be disabled until a matching SteamServersConnected_t has been posted."*

6. **`SteamServerConnectFailure_t` is the periodic retry-loop failure event, distinct from `Disconnected`.** Per Valve: *"This will occur periodically if the Steam client is not connected, and has failed when retrying."* `m_bStillRetrying` confirms the Steam client itself drives the retry loop — no user-code retry needed.

   **Documentation locality caveat:** Items 4–6 live on the `ISteamUser` reference page, not `ISteamGameServer`. Steamworks.NET registers the GameServer variants via `Callback<T>.CreateGameServer` against the same callback IDs, and the struct definitions are identical, but Valve doesn't separately document GameServer-mode behavior. Repro empirically confirms by observing the second `Connected to Steam servers!` line with no intervention.

7. **`Callback<T>` dispatches synchronously inside `RunCallbacks()`.** From Steamworks.NET source (`CallbackDispatcher.RunFrame`): `SteamAPI_ManualDispatch_GetNextCallback` is invoked in a while loop, each callback's user delegate runs on the calling thread before `RunFrame` returns. No async queueing. So disconnect-handler field writes complete on the game thread before the reconnect callback runs.

8. **The Galaxy lobby persists across the outage (not verifiable from source).** The Galaxy SDK is closed-source; GOG's lobby-reaping logic is opaque. The repro tests this directly — if the invite code stops resolving to a Galaxy lobby during the outage, item 8 was wrong and a separate Galaxy-lobby recreation problem must be fixed first.

## Constraints

- **Threading.** All callback dispatch is on the game thread (item 3 + item 7). `OnSteamServersLost`'s field writes are serialized with the rest of the lobby-state state machine without locks. Generation captures must happen on the game thread (spawn site), not inside the Task.Run body — see the `RecreateSteamLobby` step above for why both callers were updated.
- **Log level.** `Warn` per `.claude/rules/debugging.md`. `Error` from mod code trips `ServerContainer`'s `\b(ERROR|FATAL)\b` regex.
- **First-connect path.** `OnSteamServersLost` cannot fire before `OnSteamServersConnected` — `SteamServersDisconnected_t` requires a prior successful logon (item 5). Initial connection failures route via `SteamServerConnectFailure_t`, which is unchanged.
- **Shutdown path.** `SteamGameServerService.Shutdown` (line 205) disposes callback subscriptions (lines 215-220) before `LogOff()` (line 222), so no `OnSteamServersLost` fires during intentional shutdown.
- **Subscription lifetime.** `OnSteamServersLost += OnSteamServersLost` is never removed, matching the existing `OnServerSteamIdReceived` subscription. `GalaxyAuthService` enforces process-singleton (lines 172-173), so the subscription can't leak across reconstruction.

## Verification

1. **Build:** `dotnet build mod/JunimoServer/JunimoServer.csproj`.

2. **Grep checks.**
   - `OnSteamServersLost` appears exactly 4 times across the two files: event declaration + invoke (`SteamGameServerService.cs`), subscription + handler (`AuthService.cs`).
   - `_steamSessionGeneration` appears exactly 7 times in `AuthService.cs`: declaration, `++` in `OnSteamServersLost`, capture + read pair in `CreateSteamLobbyViaHttpAsync` (2), capture at each of the two `RecreateSteamLobby` spawn sites — `SetSteamLobbyPrivacy` / `SetSteamLobbyData` (2), and read in `RecreateSteamLobby`'s comparison (1). Lower count = an unguarded write site.
   - Pre-existing writers must be unchanged — each gets exactly one new writer (the new handler):
     - `_steamLobbyId`: lines 405, 532, 810.
     - `_lobbyCreationAttempted`: lines 376, 811.
     - `_lastSteamLobbyPrivacy`: lines 533, 577, 597, 607, 612, 620.
     - `_pendingGalaxyLobbyUpdate`: lines 414, 718.

3. **Manual repro (Docker server + Steam client).**

   The recovery chain crosses three external systems — Valve's Steam CMs, GOG Galaxy backends, and the steam-auth sidecar's own SteamKit session. The obvious "cut the container's NIC" drops all three at once and muddies attribution. The repro below isolates this fix using a Steam-CM-only block.

   **Setup.** Start the server. Wait for `Steam lobby created via HTTP: <L1>` and `Galaxy lobby updated with SteamLobbyId: <L1>`. Capture `<L1>` and the invite code. Confirm a Steam client joins via the invite code.

   **Outage.** From the Docker host, resolve Valve's CM IPs reachable from the container (`docker exec <server> getent hosts cm0-iad1.cm.steampowered.com cm1-iad1.cm.steampowered.com`) and block egress to those IPs only via host iptables, leaving Galaxy and the steam-auth sidecar reachable. Hold until the server log shows `Disconnected from Steam servers`.

   **During-outage checks.**
   - **a. Galaxy still up.** `Galaxy auth lost` MUST be absent from the SMAPI log and `auth_galaxy_lost` MUST be absent from `infrastructure.jsonl`. If either fires, the block is too broad — drop the iptables rule, restart the server, narrow the IP set, retry. The Trace-level `GalaxyInstance.ProcessData() failed` line is benign.
   - **b. Latch invalidation fired.** `Steam session lost — invalidating cached Steam lobby state` MUST appear exactly once.
   - **c. Galaxy invite code still resolves.** From a separate Galaxy-aware client, attempt to resolve the invite code (without joining). Resolution MUST succeed; the downstream join to `<L1>` MUST fail. This confirms item 8 and isolates the bug to the Steam-lobby pointer.

   **Restore.** Drop the iptables rule. Wait up to ~30s (Valve docs: typical ~10s, rarely up to 5min).

   **Post-restore log sequence (in order, other lines may interleave):**
   1. `Connected to Steam servers!`
   2. `Server Steam ID: <id>`
   3. `Creating Steam lobby via steam-auth service, GameServer ID: <id>`
   4. `Steam lobby created via HTTP: <L2>` with `<L2> != <L1>`
   5. `Galaxy lobby updated with SteamLobbyId: <L2>`

   Then reuse the original invite code from the Steam client. MUST join successfully.

   **Pass:** all five lines in order, `<L2> != <L1>`, Steam-client join succeeds. A `Discarding Steam lobby ... from invalidated session` line is informational (confirms the counter caught a late Task.Run); it does not fail the run.

   **Fail diagnostics.**
   - No `Steam session lost` line: event wiring is broken (subscription missing or event never invoked from `OnSteamServersDisconnected`).
   - `Steam session lost` fires but no `Connected to Steam servers!` within 5 minutes: GameServer mode does not share `ISteamUser`'s auto-reconnect (item 6 caveat is wrong) — this fix is incomplete and an explicit `LogOnAnonymous()` retry is needed.
   - `Connected` fires but no `Creating Steam lobby` follow-up: `OnServerSteamIdReceived` not re-firing, OR the handler's `_lobbyCreationAttempted` reset didn't land — verify field-write order and `_serverSteamId` reassignment at `SteamGameServerService.cs:168`.
   - `<L2> == <L1>`: steam-auth returned a sticky/cached ID; investigate `tools/steam-service/SteamAuthService.cs` (separate bug, this fix is still correct).
   - All five lines present but Steam-client join fails: the new `<L2>` was stamped but Valve has no record of it — investigate steam-auth's `CreateLobby` HTTP path.

   **Without iptables access** (managed host): fall back to cutting the entire container network. The repro becomes non-deterministic — `Galaxy auth lost` will fire and line 5 may be missing because the Galaxy-reinit gap masks it. In that mode, rely on the grep checks (step 2) for static verification of the handler and counter wiring, and file the Galaxy-reinit follow-up before retrying the broader-outage repro.

4. **E2E:** `make test`. Regression check only — tests never lose a Steam session, so the recovery path is not exercised. Confirms the normal first-connect lobby flow is unbroken.

5. **No automated test for disconnect/reconnect recovery — by design.** A non-flaky test would need deterministic Steam-CM-only fault injection inside a Docker test container plus deterministic Valve reconnect timing. Neither exists: there is no toxiproxy/netem layer in `tests/`, and Valve reconnect time is seconds-to-minutes by their own docs. A field-reset test seam would verify the handler body but not the bug class — the failure mode is the *interaction* between disconnect callback dispatch, Task.Run completion ordering, and reconnect chain re-firing. Per `.claude/rules/universal/simplest-solution.md`, scaffolding a seam to verify mechanical field writes has no safety value. Manual repro (step 3) is the gate; the generation counter moves the only race a test could plausibly catch from test-runtime to compile-time.
