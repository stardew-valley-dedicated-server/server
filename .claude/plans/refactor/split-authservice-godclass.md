# Refactor: split `GalaxyAuthService` along the lifecycle / Harmony-shim seam

## Why (and why NOT "just extract the outage code")

`mod/JunimoServer/Services/AuthService/AuthService.cs` is ~1789 lines doing four distinct jobs:

1. **Harmony patch registration + patch bodies** — the static `*_Prefix`/`*_Postfix` methods that
   re-route `SteamHelper`/`SteamNetServer`/`GalaxySocket`/`SteamUser`/`SteamFriends` to GameServer
   APIs (`SteamHelperInitialize_Prefix`, `SteamHelperUpdate_Prefix`, `SteamHelperShutdown_Prefix`,
   `SteamNetServer_Initialize_Prefix`, `GalaxySocket_OnLobbyCreated_Prefix`,
   `GalaxySocket_GetInviteCode_Postfix`, `SteamUser_GetSteamID_Prefix`,
   `SteamFriends_GetPersonaName_Prefix`).
2. **Steam-lobby HTTP lifecycle** — create / recreate / privacy / data via the steam-auth sidecar
   (`CreateSteamLobbyViaHttpAsync`, `RecreateSteamLobby`, `SetSteamLobbyPrivacy`,
   `SetSteamLobbyData`, `GetOrCreateApiClient`, `IsLobbyLostError`).
3. **Galaxy init + GalaxyNetServer plumbing** — deferred init, late-add, server resolution, lobby
   re-stamp (`PerformDeferredInitialization`, `SignIntoGalaxy`, `UseExternalSteamAuth`,
   `TryLateAddGalaxyServer`, `GetGalaxyServer`, `UpdateGalaxyLobbyWithSteamLobbyId`).
4. **Total connectivity loss recovery** (this PR) — `OnSteamServersLost`/`OnServerSteamIdReceived` reconnect
   branch, `TryBeginGalaxyReSignInGated`, `BeginGalaxyReSignIn`, `ConsumePendingGalaxyReSignIn`,
   `PumpGalaxyReLogonWait`, `TryRemoveGalaxyServer`, `IsGalaxyLobbyConnected`,
   `TriggerGalaxyReSignInForTest`.

**Rejected approach — extract only (4) into a `GalaxyReauthRecovery` class.** The recovery state
(`_pendingGalaxyReSignIn`, `_galaxyAwaitingReLogon`, `_galaxyReSignInInFlight`,
`_pendingReSignInTicket{,Length}`, `_galaxyReLogonWaitedTicks`) IS self-contained — touched only by
the recovery methods and `OnSteamServersLost`'s reset. But the recovery *logic* is not independent:

- It reads/writes **`_steamLobbyId`** (the re-stamp), which is owned and written across (2) — so a
  recovery-only class would have to share that field back, leaking the god-state instead of
  dissolving it.
- It needs the **`SteamHelper __instance`** that only exists inside `SteamHelperUpdate_Prefix`'s tick
  (`SetSteamGalaxyConnected(__instance, true)`, `PumpGalaxyReLogonWait(__instance)`), plus
  `_helper.Reflection` to reach vanilla internals. Recovery is *driven by the patch tick* — it is
  the patch's per-tick work, not standalone logic.
- Everything is `static` because Harmony patches must be. A recovery-only class is either statically
  coupled the same way, or needs an instance-bridge (`_instance.Recovery.Pump(...)`) that adds
  indirection without removing the static-ness.

So extracting (4) alone splits the *file* but not the *god-class*: the new class still reaches back
into `AuthService` for `_steamLobbyId` and `SteamHelper`. The clean seam is elsewhere.

## The real seam: lifecycle state-machine vs. Harmony-patch shim

The god-class is two things welded together:

- **A — the patch shim** (thin, must stay static): the `*_Prefix`/`*_Postfix` bodies + their
  registration. Each body should do nothing but unpack its `__instance`/`__result` and delegate to B.
- **B — the lobby/auth lifecycle coordinator** (the real logic, can be instance state): owns
  `_steamLobbyId`, `_steamSessionGeneration`, `_lobbyCreationAttempted`, `_galaxyInitComplete`, the
  pending/recovery flags, and the create/recreate/re-stamp/init/recovery methods.

Splitting on A/B dissolves the god-class because **all four concerns' shared state moves into one
owner (B)**, and the patches (A) become a flat list of one-line delegations. Concern (4) then lands
naturally inside B with no field-sharing leak, because B *is* where `_steamLobbyId` lives.

## Proposed shape

```
GalaxyAuthService              (A — patch shim, unchanged registration site)
  - ctor: wires SteamGameServerService events, registers Harmony patches
  - *_Prefix / *_Postfix       → each delegates to _instance.Coordinator.<Method>(...)
  - holds the single static _instance bridge (already exists)

GalaxyLobbyCoordinator         (B — NEW, instance state owned by the service)
  - fields: _steamLobbyId, _steamSessionGeneration, _lobbyCreationAttempted,
            _galaxyInitComplete, _pendingGalaxyLobbyUpdate, + the 6 recovery fields
  - Steam-lobby lifecycle:  CreateSteamLobby / RecreateSteamLobby / SetPrivacy / SetData
  - Galaxy plumbing:        PerformDeferredInit / TryLateAddGalaxyServer / GetGalaxyServer /
                            UpdateGalaxyLobbyWithSteamLobbyId
  - Outage recovery:        OnReconnect / TryBeginGalaxyReSignInGated / BeginGalaxyReSignIn /
                            ConsumePendingReSignIn / PumpReLogonWait / TryRemoveGalaxyServer /
                            IsGalaxyLobbyConnected
  - per-tick entry point:   Pump(SteamHelper instance)   ← called by SteamHelperUpdate_Prefix
```

Optionally B splits further into `SteamLobbyLifecycle` + `GalaxyReauthRecovery` once it's an
instance class — but that's a second step, only if B itself grows unwieldy. The first cut is just
A/B.

## Constraints (verified against the current code — keep these or the split regresses)

- **Patches stay static; only their bodies thin out.** Harmony requires static patch methods. The
  shim keeps `static *_Prefix(...)` signatures; each just calls `_instance.Coordinator.X(...)`.
  The `_instance` bridge already exists for exactly this.
- **Game-thread confinement is load-bearing.** All four auth callbacks + the recovery state machine
  run on the game thread (`GameServer.RunCallbacks()` / `GalaxyInstance.ProcessData()` in the Update
  prefix). The two `Task.Run` ticket fetches are the only off-thread code and publish via
  volatile-write/volatile-read. Moving fields onto an instance must preserve: the `volatile` on
  `_steamSessionGeneration`/`_pendingGalaxyLobbyUpdate`/`_galaxyReSignInInFlight`/`_pendingGalaxyReSignIn`,
  and the generation-guard pattern (`CreateSteamLobbyViaHttpAsync`, `BeginGalaxyReSignIn`).
- **No new per-tick allocation** (`mod-game-thread-allocation.md`). `Pump(instance)` and its
  delegations are plain method calls — no closures/LINQ/collections in the hot path. (Instance-method
  dispatch is free; do NOT introduce a captured-lambda dispatch table.)
- **Reflection-string single-source.** `GetGalaxyServer` already centralizes the `"servers"` /
  `"server"` reflection; keep it as the one resolver — don't re-scatter the strings across the split.
- **`OnSteamServersLost` resets all pending/recovery flags together** (it's the session-invalidation
  barrier). Whatever owns those fields must keep that reset in one place; don't split the reset across
  two classes.
- **Test endpoint stays wired.** `TriggerGalaxyReSignInForTest` (used by `/test/galaxy_relogin`) must
  remain reachable — it'd become `Coordinator.TriggerReSignInForTest()` behind the same static shim.

## Verification (post-conditions are runtime gates — `runtime-post-conditions-are-gates.md`)

1. **Build:** `dotnet build mod/JunimoServer/JunimoServer.csproj` 0/0; rebuild `sdvd/server:local`.
2. **Format/lint:** `make lint-check` clean.
3. **Outage regression (the real gate):** `make test FILTER=GalaxyOutageRepro` — must stay 2/2 with
   `degradation:"clean"`, recovery markers present (`auth_galaxy_recovered` OR
   `auth_galaxy_relogin_skipped` + post-reconnect `auth_steam_lobby_created`), zero ERROR/FATAL.
   This is the proof the static→instance move didn't break the game-thread state machine.
4. **Full suite:** `make test` — confirms first-connect, lobby create/recreate, and the Steam-auth
   patches are unbroken (the patch-shim delegation is the highest-risk part).

## Scope / sequencing

- **Out of scope for the outage PR** (#this) — that PR is a bug fix; this is structural and touches
  every patch body, so it ships separately to keep the fix reviewable.
- **Pre-req read:** `harmony-patch-reachability.md` (patches only fire if their registering ctor
  runs — `GalaxyAuthService`'s ctor is unconditional, so the shim must stay there, NOT move into a
  conditionally-constructed service).
- **Estimated size:** large — ~8 patch bodies thinned + ~20 methods + ~12 fields moved onto an
  instance. Do it as one focused refactor with the outage repro as the gate, not piecemeal.
