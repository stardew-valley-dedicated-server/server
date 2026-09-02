# `RecreateSteamLobby` updates the Galaxy lobby off the game thread

**Status:** ready-to-implement
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** server
**Related:** none
**Observed:** not observed, found by reading
**Next step:** replace the direct `UpdateGalaxyLobbyWithSteamLobbyId()` call in `RecreateSteamLobby` with the `_pendingGalaxyLobbyUpdate` deferral

## Symptom

`RecreateSteamLobby` can update the Galaxy lobby from a background task.

Both call sites for `RecreateSteamLobby` are in catch blocks inside lobby API operations that run through `Task.Run`. The recovery path calls `UpdateGalaxyLobbyWithSteamLobbyId()` directly, which eventually calls `galaxyServer.setLobbyData(...)`.

Galaxy SDK calls are not thread-safe. The normal lobby creation path already handles this by setting `_pendingGalaxyLobbyUpdate` and letting the game-thread update loop apply the change on the next tick. The Steam lobby recovery path skips that step and makes the SDK call directly from the background task.

This creates a low-frequency risk when Steam lobby-loss recovery (`NoMatch`) happens while the game thread is also processing Galaxy data or lobby heartbeats. The worst case would be native-side corruption or a crash in `GalaxyCSharp`. There has been no observed failure; this is a latent thread-safety issue.

## Fix

In `RecreateSteamLobby`, after committing the new `_steamLobbyId`, set `_pendingGalaxyLobbyUpdate = true` instead of calling `UpdateGalaxyLobbyWithSteamLobbyId()` directly.

The existing game-thread pump will then call `UpdateGalaxyLobbyWithSteamLobbyId()` on the next tick, matching the behavior already used by the normal create path.

The Steam HTTP retries in the same catch blocks are separate and should remain unchanged.

`_steamLobbyPublished` does not need any additional changes. It is already cleared and set inside `UpdateGalaxyLobbyWithSteamLobbyId()`, so deferring the call only means the S-code remains hidden for one extra tick during recovery. That is consistent with the existing gate behavior.

## Verification

Verify that the recovery path now uses the same main-thread deferral as the normal create path.

Also confirm that the existing game-thread pump remains active during Steam lobby-loss recovery. It is the same pump that handles the create-path update.

There is currently no E2E coverage for this recovery path. A live reproduction would require forcing Steam to return `NoMatch` and drop the lobby during an active session, which is outside the current test harness.
