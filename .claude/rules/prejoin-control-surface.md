---
paths:
  - "mod/JunimoServer/Services/AuthService/**"
  - "mod/JunimoServer/Services/Lobby/**"
---

# Pre-join control surface: two benign text levers, no input channel, no durable LAN identity

A vanilla client parked at `FarmhandMenu` pre-join (gameMode 0; `serverHost`/`chatBox`/`currentLocation` all null) was swept against every SDV multiplayer message type (`Multiplayer.cs:46-115`). Full result — don't re-run the sweep or hunt for hidden levers:

- **Usable, benign:** type 11 `connectionMessage` (centered status text — the only server-controlled visible text pre-join), type 9 `availableFarmhands` (slot list; also sets `hasHandshaked`, cancelling the client's 45s connection timeout), type 23 `forceKick` (clean disconnect), type 1 `serverIntroduction` (forces the client into gameMode 3 without a slot pick — powerful but half-initialized/crash-prone).
- **Dead positive levers (verified against `Game1.cs`):** type 20 achievement — `getAchievement` early-returns on `gameMode != 3` (`Game1.cs:10619`); type 21 global/HUD message — the HUD draw block is gated out at gameMode 0 (`Game1.cs:13791`); chat (10/15) and join/leave info are hard-guarded by `Game1.chatBox != null`. Everything else is a guaranteed NRE (types 4, 8, 13, 14, 16, 17, 19, 25, 29 deref `serverHost.Value`/`SourceFarmer`/null location/`teamRoot`) or a silent no-op.
- **No input surface:** the menu reads only `connectionMessage`, `availableFarmhands`, `timedOut`. No network handler constructs a text-input menu, no vanilla password prompt exists, and the IP-field text never reaches the server. A pre-join back-channel must be out-of-band — e.g. Discord plus a per-connection code shown via type 11.
- **LAN `connectionId` is session-scoped, not a machine key:** `"L_" + RemoteUniqueIdentifier` (`LidgrenServer.cs:296-299`) hashes the client's ephemeral local port + MAC (`NetPeer.InitializeNetwork`, `NetPeer.cs:681-690`; the port is OS-assigned each launch because the client binds with port 0). Stable within one game session and its reconnects, fresh on every restart; the MAC cannot be recovered from it. Source IP (`conn.RemoteEndPoint.Address`) is the only cross-restart transport identifier on LAN, with all its NAT/shared-IP caveats.

**Why:** Both sweeps were done for the LAN farmhand-ownership investigation and are expensive to re-derive (every message type traced through decompiled handlers; the Lidgren hash inputs read from `NetPeer` source). The recurring temptation they refute: "surely there's some pre-join message that renders more UI / prompts for input / identifies the machine" — there isn't.

**How to apply:** When designing lobby/auth/pre-join features, treat type 11 text + type 9 slot-list timing as the entire in-game surface for an unjoined vanilla client, and don't key durable per-client state on the LAN connectionId (it dies at restart — correlate out-of-band and bind to a synthetic `userID`, or accept source-IP semantics). If a design needs a richer pre-join channel, it must be out-of-band by construction, not a hunt for an unused message type.
