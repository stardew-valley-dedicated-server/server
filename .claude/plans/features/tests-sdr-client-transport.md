# Tests: SDR client transport — dial the server's SDR listener from test clients

Closes the one E2E transport gap: client-side Steam SDR is never dialed in the harness, so the
`SN_` half of `SteamGameServerNetServer` (accept flow, `HandleFarmhandRequest`, `SN_` identity
parsing/gating) is only exercised in production. Status: **planned, not started** — written up
after the farmhand-ownership work surfaced the gap (see
`docs/developers/architecture/steam-auth.md` § "Auth vs Transport").

## Context (verified)

- Vanilla's `SteamNetClient` is the single user-session-gated operation in the system:
  `SteamAPI.Init()` (`SteamHelper.cs:60`) → `SteamMatchmaking.JoinLobby`
  (`SteamNetClient.cs:204`) → `SteamMatchmaking.GetLobbyGameServer` (`:163`) →
  `SteamNetworkingSockets.ConnectP2P` (`:131`) — all user-mode Steamworks, requiring a running
  Steam client daemon. Containers can't have one; the test-client mod therefore reroutes
  Hybrid (S-code) joins to `GalaxyNetClient` (`ClientAuthService.CreateClient_Prefix`).
- The GameServer door is headless by design and its sockets interface is symmetric:
  `SteamGameServerNetworkingSockets` has `ConnectP2P` just like the user-mode variant. Our
  server already proves anonymous GameServer sessions get full SDR relay access
  (`SteamGameServerNetServer`, `LogOnAnonymous`, ephemeral `90…` IDs — observed changing per
  boot in run artifacts).
- Discovery without `SteamMatchmaking`: the sidecar already knows the server's gameserver
  SteamID — `POST /lobby` receives `game_server_steam_id` and stores it in lobby metadata
  (`tools/steam-service/Program.cs:629,767`, `SteamAuthService.cs:1961-1998`,
  `_currentGameServerSteamId`). Expose it read-only; the test client already talks to the
  sidecar (`STEAM_AUTH_URL`).

## Design

New test-client-mod transport: `SdrGameServerClient : Client` (or a `HookableClient` sibling),
selected instead of the Galaxy reroute when an env flag is set (e.g.
`SDVD_TEST_SDR_TRANSPORT=true`), so the default harness behavior is unchanged and the SDR
variant is opt-in per test run.

1. **Session**: in the test-client process, `GameServer.Init()` + `LogOnAnonymous()` (mirrors
   `SteamGameServerService`; the game never calls `SteamAPI.Init` in auth mode, so the modes
   don't collide). Pump `GameServer.RunCallbacks()` from the mod's update hook.
2. **Discovery**: new sidecar endpoint `GET /lobby/game-server` → `{ steam_id }` (the value it
   already stores). The dialer polls until non-zero.
3. **Dial**: `SteamGameServerNetworkingSockets.ConnectP2P(identity(serverSteamId), 0, opts)`;
   connection-status callback via `Callback.CreateGameServer`.
4. **Framing**: mirror `SteamGameServerNetServer`'s wire format exactly — messages are
   `OutgoingMessage.Write` payloads compressed with `netCompression.CompressAbove`
   (`SteamConstants.CompressionThreshold`) and decompressed with `DecompressBytes` on read.
   Reuse `SteamSocketUtils` where applicable; the read loop mirrors
   `SteamNetClient.receiveMessages` shape (poll → decompress → `IncomingMessage.Read`).
5. **Selection seam**: extend `CreateClient_Prefix` — Hybrid + flag set → `SdrGameServerClient`
   targeting the sidecar-resolved gameserver ID; Hybrid + flag unset → existing
   `GalaxyNetClient` reroute (unchanged default).

### Identity caveat (accepted, documented)

The connection presents the **anonymous gameserver ID** of the test client's session (`90…`),
not a user Steam64 — relay identities are session-authenticated and unforgeable (the same
property the ownership gate relies on). Consequences:

- Ownership gate/recorder/visibility treat it as a first-class steam-platform identity —
  full mechanics coverage of the `SN_` rows.
- Stable within one container lifetime (one logon session): select → disconnect → resume works
  inside a test. NOT stable across container restarts — no cross-lifecycle resume realism.
- Account-resume realism (same human, same Steam64 across sessions) stays covered by the
  Galaxy-path tests with real accounts. 100% parity would require a real Steam client daemon
  in the container; explicitly out of scope.

## Files touched

- **New**: `tests/test-client/Networking/SdrGameServerClient.cs` (+ session bootstrap helper)
- `tests/test-client/Auth/ClientAuthService.cs` (selection seam in `CreateClient_Prefix`)
- `tools/steam-service/Program.cs` + `SteamAuthService.cs` (`GET /lobby/game-server`)
- `tests/JunimoServer.Tests/…`: new `SdrTransportTests` (opt-in config), `WaitName` entries
- Docs: update the steam-auth.md "Auth vs Transport" section's harness note once landed

## Verification (runtime gates)

1. Server log shows `steam_p2p_connect_started`/`steam_p2p_connected` and
   `Client connected via Steam SDR (platform id 90…)` — the first-ever harness occurrence.
2. Join approved through `HandleFarmhandRequest`; `/diagnostics/state` shows
   `HasOwner=true, OwnerPlatform="steam"` for the claimed farmhand (recorder on the `SN_`
   branch).
3. Resume within the same container: disconnect → reconnect → same farmhand visible +
   rejoin approved (gate match on the `SN_` identity).
4. Full suite with the flag OFF is byte-identical in behavior (default path untouched).

## Open questions / risks

- Steamworks.NET GameServer init inside the game-client process alongside SMAPI: expected fine
  (server process does exactly this), but the first spike should confirm callback pumping
  doesn't fight the game loop at `CLIENT_TPS`.
- SDR relay egress from client containers: server containers already reach Valve relays from
  the same network; assumed symmetric.
- One steam server account constraint is untouched (this uses no account at all), but the
  anonymous session still needs the Steamworks SDK redistributables in the test-client image
  (`steamclient.so` — the image already links the Steam SDK for the auth path; verify the
  GameServer entry points resolve).
