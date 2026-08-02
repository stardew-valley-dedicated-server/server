# RecreateSteamLobby stamps the Galaxy lobby from a background task

## Symptom (latent — no observed failure yet)

`GalaxyAuthService.RecreateSteamLobby` calls `UpdateGalaxyLobbyWithSteamLobbyId()` directly
(`AuthService.cs`, recreate path), and both of its call sites are catch-blocks inside lobby API
operations that run in `Task.Run`. `UpdateGalaxyLobbyWithSteamLobbyId` calls
`galaxyServer.setLobbyData(...)` — a Galaxy SDK call, and the file's own rule
(`_pendingGalaxyLobbyUpdate` doc: "Galaxy SDK calls are not thread-safe") is why the CREATE
path defers exactly this call to the next game tick instead of making it from the background
task. The recovery path skips that deferral.

## Risk

Steam-lobby-lost recovery (NoMatch) racing the game thread's own Galaxy pumping
(`ProcessData`, lobby heartbeats) — worst case a native-side corruption/crash in GalaxyCSharp
during a recovery that is itself rare. Never observed; severity is low-frequency ×
hard-to-diagnose.

## Fix sketch

In `RecreateSteamLobby`, after committing the new `_steamLobbyId`, set
`_pendingGalaxyLobbyUpdate = true` instead of calling `UpdateGalaxyLobbyWithSteamLobbyId()`
directly — the existing main-thread pump (`ProcessData` tick handler) applies it next tick,
identical to the create path. One line. The sibling Steam HTTP retries in the same catch-blocks
are sidecar HTTP calls and stay as they are.

Note: `_steamLobbyPublished` (the S-code gate) is already cleared/set inside
`UpdateGalaxyLobbyWithSteamLobbyId`, so the deferral changes nothing for the gate — the S-code
simply stays hidden one tick longer during recovery, which is the gate working as intended.

## Verification

No E2E coverage exists for the lobby-lost recovery path. Minimum: code-review the deferral +
confirm the pump's tick handler is active in the recovery scenario (it is the same handler that
serves the create path). A live repro would need Steam to drop the lobby (NoMatch) mid-session —
out of scope for the harness today.
