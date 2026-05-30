# JunimoServer Mod - Security & Anti-Cheat Audit

Focused analysis on exploitation, cheating, and abuse vectors. Assumes a malicious player with a modified Stardew Valley client or access to the server's network port.

---

## 1. Critical Exploits (immediate action required)

### S1. `Program.enableCheats = true` -- vanilla debug commands available on server
- **File:** `Services/ChatCommands/ChatCommands.cs:35`
- **What:** The mod sets `Program.enableCheats = true` globally. While vanilla `/` commands are processed client-side (not sent to server), this flag enables `ChatCommands.AllowCheats` on the server's ChatBox. Any code path that processes debug commands on the server game loop (console, WebSocket relay, future changes) will have full debug access.
- **Key insight:** The `/` prefix is processed client-side and returns before sending to server. A **vanilla unmodified** client cannot exploit this remotely. However, a **modified client** that patches `ChatBox.textBoxEnter()` to forward `/` commands as server messages, or any code that invokes `ChatBox.runCommand()` server-side, has access to ALL debug commands: `/money`, `/time`, `/debug Sleep`, `/debug ClearFarm`, `/debug RemoveBuildings`, `/debug Item`, `/debug Warp`, `/debug Event`, etc.
- **Risk level:** Critical for modified clients; moderate for vanilla clients.
- **Fix:** Remove `Program.enableCheats = true`, or add a Harmony prefix on `DebugCommands.TryHandle` that rejects debug commands unless from server console.

### S2. `farmerDelta` (type 0) allowed for unauthenticated players -- full farmer state manipulation
- **File:** `Services/PasswordProtection/PasswordProtectionService.cs:200-204`
- **What:** The password protection whitelist allows `farmerDelta` messages from unauthenticated players. The comment says "this contains farmer creation data (name, appearance)" but `farmerDelta` carries **all** Farmer NetField deltas. A modified client can craft deltas to modify:
  - **Money** (`_money` field)
  - **Inventory** (`Items` -- inject any item including Prismatic Shards, Galaxy Swords)
  - **Skills** (`experiencePoints` -- set all to level 10)
  - **Health/Stamina** (`health`, `maxHealth`, `stamina`, `maxStamina`)
  - **Position** (`position` -- escape the lobby)
  - **`isCustomized`** -- set to `false` repeatedly to prevent auth timeout from starting (PasswordProtectionService.cs:363 checks this)
- **Critical detail:** `farmerDelta` is a **broadcast type** (`Multiplayer.isClientBroadcastType` returns true for type 0). The server rebroadcasts the crafted delta to ALL connected players, propagating corrupted state.
- **Exploitation steps:**
  1. Connect to password-protected server
  2. Before authenticating, craft a `farmerDelta` message setting `_money = 999999999`
  3. Server allows it through whitelist and applies via `readObjectDelta`
  4. Server rebroadcasts to all players
  5. Authenticate normally. Money is set.
- **Fix:** Block `farmerDelta` entirely for unauthenticated players except during the `IsNewPlayer` character creation phase. Or implement a sub-field filter that only allows appearance fields.

### S3. API has no authentication by default -- all endpoints publicly accessible
- **File:** `Env.cs:54` -- `ApiKey` defaults to `""`
- **File:** `Services/Api/ApiService.cs:408` -- `_authEnabled = !string.IsNullOrEmpty(Env.ApiKey)` = `false`
- **What:** When `API_KEY` environment variable is not set (the default), every API endpoint is accessible without authentication. The API is enabled by default (`API_ENABLED` defaults to `true`).
- **Accessible destructive operations without auth:**
  - `DELETE /farmhands?name=X` -- permanently destroys a player's cabin, inventory, and all progress
  - `POST /time?value=2600` -- forces immediate day transition, can corrupt save during festivals/events
  - `POST /roles/admin?name=X` -- grants persistent admin role to any player
  - `POST /rendering?enabled=false` -- blinds VNC monitoring
  - `GET /status` -- leaks both Steam and GOG invite codes
- **Fix:** Either require `API_KEY` to be set when `API_ENABLED=true`, or disable the API by default.

### S4. Wildcard CORS allows cross-site API exploitation
- **File:** `Services/Api/ApiService.cs:1571`
- **What:** `Access-Control-Allow-Origin: *` is set on every JSON response. Any website a server admin visits can silently make API calls to the server. Combined with S3 (no auth by default), a malicious webpage can delete farmhands, grant admin, change time, and read invite codes -- all via JavaScript fetch requests in the admin's browser.
- **Fix:** Remove CORS wildcard. Either restrict to specific origins or require a same-origin proxy.

### S5. Galaxy and Steam lobbies forced public -- server always discoverable
- **File:** `Services/ServerOptim/ServerOptimizerOverrides.cs:111-116` -- Galaxy lobby forced to `ServerPrivacy.Public` with 150 member limit
- **File:** `Services/AuthService/AuthService.cs:549-552` -- Steam lobby always set to `"public"` regardless of config
- **What:** Both lobby types are hardcoded to public, making the server discoverable in Galaxy/Steam lobby browsers. The invite code is stored in lobby metadata. A "private" server is impossible -- anyone can find it, obtain the invite code from lobby data, and attempt to connect.
- **Fix:** Respect the server operator's privacy configuration. Only force public when explicitly configured.

---

## 2. High-Severity Exploitation Vectors

### S6. No server-side validation of game protocol messages from authenticated players
- **What:** The `MessageInterceptorService.HandleIncoming()` (MessageInterceptorService.cs:57) is a complete no-op -- all incoming message interception code is commented out. Authenticated players' messages go directly to vanilla `GameServer.processIncomingMessage` with no mod-level validation. A modified client can:
  - **Duplicate items** via crafted `farmerDelta` setting inventory contents
  - **Set money** via `farmerDelta` modifying `_money`
  - **Max all skills** via `farmerDelta` modifying `experiencePoints`
  - **Teleport anywhere** via `warpFarmer` messages (type 5) or position deltas
  - **Modify world objects** via `locationDelta` (type 6) -- place/remove objects, terrain
  - **Send chat as any player** -- `chatMessage` (type 10) contains a `FarmerID` field
- **Fix:** Implement server-side validation in the message interceptor, at minimum: validate that `farmerDelta` money changes are within expected bounds, validate inventory changes against game rules, reject `warpFarmer` to restricted locations.

### S7. Admin privilege escalation -- any admin can lock out the server owner
- **File:** `Services/Commands/RoleCommands.cs:38-69` and `Services/Commands/BanCommand.cs`
- **What:** The `IsServerHost()` protection only guards the automated bot player (`Game1.player`), not the human server operator. Attack scenario:
  1. Server owner promotes Player A to admin
  2. Player A runs `!unadmin OwnerName` (succeeds -- owner is not the "server host")
  3. Player A runs `!ban OwnerName` (succeeds)
  4. Player A promotes accomplices: `!admin PlayerB`
  5. Server owner is permanently banned with no recourse except save editing
- **Fix:** Protect players listed in `ADMIN_STEAM_IDS` from `!unadmin` and `!ban`. Add an "owner" tier above admin.

### S8. `!login password` is broadcast to all connected players
- **What:** When a player types `!login secretpassword`, the chat message is whitelisted by `PasswordProtectionService.IsAllowedChatCommand` and passed to `GameServer.processIncomingMessage`. The vanilla handler at decompiled `GameServer.cs:739` calls `rebroadcastClientMessage(message, num)`. If the recipient is `AllPlayers` (the default chat target), the plaintext password is broadcast to every connected player.
- **Additionally:** The password is logged at Trace level (ChatCommands.cs:132) and Debug level (PasswordProtectionService.cs:318) in server logs.
- **Fix:** Intercept `!login` messages in a Harmony **prefix** on `processIncomingMessage` to prevent rebroadcast. Redact password content from all log messages.

### S9. Massive outgoing message leak to unauthenticated players
- **File:** `Services/PasswordProtection/PasswordProtectionService.cs:239-272`
- **What:** The outgoing message filter only blocks 2 of 34 message types (`newDaySync` and `startNewDaySync`). Unauthenticated lobby players receive:
  - `teamDelta` (type 13): Team money, individual balances, achievements
  - `farmerDelta` (type 0): All other players' positions, inventories, health, stats
  - `locationDelta` (type 6): World map state changes
  - `chatMessage` (type 10): All player chat
  - `playerIntroduction` (type 2): Full farmer data for new joiners
  - `serverIntroduction` (type 1): Complete serialized game state
- **Impact:** An unauthenticated player can sit in the lobby and observe the complete game state: everyone's inventory, money, positions, chat, and world changes.
- **Fix:** Block all outgoing messages except `serverIntroduction` (needed for join) and types explicitly needed for the lobby experience.

### S10. Players with spaces in names are immune to moderation
- **File:** `Services/Commands/BanCommand.cs:20`, `KickCommand.cs:20`, `RoleCommands.cs`
- **What:** All name-based commands (`!kick`, `!ban`, `!admin`, `!unadmin`) expect exactly 1 argument. `ArgUtility.SplitBySpace` splits the name on spaces, so `!kick John Smith` is parsed as 2 arguments and rejected.
- **Impact:** A griefer can name their character "A B" and be completely immune to kick/ban via chat commands.
- **Fix:** Join all arguments as the player name: `var name = string.Join(" ", args)`.

### S11. Name collision attacks -- wrong player targeted
- **File:** `Util/ModHelperExtensions.cs:22-29`
- **What:** `FindPlayerIdByFarmerNameOrUserName` returns `FirstOrDefault()`. If two players share a name, `!ban`, `!kick`, and `!admin` always target the first match, which may be the wrong player. A griefer can name their character identically to an innocent player -- any moderation action hits the innocent player.
- **Fix:** When multiple matches exist, reject the command and show the ambiguity. Consider targeting by player ID instead.

### S12. WebSocket auth bypassed when no API_KEY
- **File:** `Services/Api/ApiService.cs:742-748`
- **What:** When `_authEnabled` is false (no API_KEY set), WebSocket clients are auto-authenticated and added to `_wsClients`. They can inject chat messages via `chat_send` with any author name.
- **Fix:** Require authentication for WebSocket connections even when API key is not set, or disable WebSocket when auth is disabled.

---

## 3. Medium-Severity Issues

### S13. API key comparison is not constant-time (timing side-channel)
- **File:** `Services/Api/ApiService.cs:485` -- uses `==` string comparison
- **Contrast:** PasswordProtectionService.cs:573 correctly uses `CryptographicOperations.FixedTimeEquals`
- **Fix:** Use the same `FixedTimeEquals` pattern for API key validation.

### S14. No rate limiting on API endpoints
- **File:** `Services/Api/ApiService.cs:556`
- **What:** Every request spawns a new `Task.Run` with no concurrency or rate limits. The `_pendingGameActions` queue is unbounded. An attacker can flood requests to exhaust the thread pool or saturate the game-thread action queue.
- **Fix:** Add per-IP rate limiting and a maximum queue depth for `_pendingGameActions`.

### S15. `!changewallet` exploitable for economy manipulation
- **File:** `Services/Commands/ChangeWalletCommand.cs:23-32`
- **What:** No rate limiting. Repeated rapid `SeparateWallets()` calls divide money further each time, potentially draining all players to near-zero. Calling during day transition or shop transactions can cause duplication or loss.
- **Fix:** Add cooldown. Block during day transitions.

### S16. `!cabin` has no bounds checking
- **File:** `Services/Commands/CabinCommand.cs:50`
- **What:** Cabin placed at `farmer.Tile + (1,0)` with no validation. Can place cabins out of bounds, overlapping buildings, or in water. `ClearTerrainBelow()` destroys terrain at the new position.
- **Fix:** Validate against farm map bounds and building collision layers.

### S17. Auth timeout bypass via `isCustomized` manipulation
- **File:** `Services/PasswordProtection/PasswordProtectionService.cs:363`
- **What:** The auth timeout only starts counting when `farmer.isCustomized.Value` is true. Since `farmerDelta` is whitelisted (S2), a modified client can repeatedly set `isCustomized = false`, preventing the timeout from ever starting.
- **Impact:** Unauthenticated player remains connected indefinitely, observing all game state (S9).
- **Fix:** Use a server-side timestamp from connection time, not a client-controllable field.

### S18. Desync kicker exploitable for day-transition griefing
- **File:** `Services/NetworkTweaks/DesyncKicker.cs`
- **What:** A malicious client can intentionally not complete sleep/day barriers, forcing the desync kicker to wait the full `barrierDesyncMaxTime` (20 seconds) before kicking. By repeatedly reconnecting, a griefer can delay day transitions indefinitely.
- **Fix:** Track repeated barrier failures per player and reduce the grace period for repeat offenders.

### S19. Ban evasion is trivial
- **What:** Bans use `server.getUserId()` which returns a platform-specific ID (Steam/Galaxy). Evasion methods:
  - Different Steam account (Family Sharing)
  - Switching between Galaxy and Steam connections
  - `Game1.bannedUsers` is a runtime dictionary -- bans may not persist across restarts
- **Fix:** Consider IP-based bans as a supplement. Ensure bans are persisted to save data.

### S20. `playerIntroduction` (type 2) replayable
- **File:** `Services/PasswordProtection/PasswordProtectionService.cs:217-219`
- **What:** No check prevents multiple `playerIntroduction` messages from the same connection. This is a broadcast type -- replayed introductions go to all players. Could inject duplicate farmer state or crash clients.
- **Fix:** Track whether a player has already sent their introduction and reject duplicates.

### S21. Chat spam via `!login`/`!help` prefix
- **File:** `Services/PasswordProtection/PasswordProtectionService.cs:305`
- **What:** `IsAllowedChatCommand` uses `StartsWith("!login")` and `StartsWith("!help")`. Messages like `!login SPAM_TEXT_HERE` or `!help BUY CHEAP GOLD AT...` pass the filter and are rebroadcast to all players.
- **Impact:** Unauthenticated players can spam chat.
- **Fix:** Block rebroadcast of `!login` and `!help` messages. Process them server-side only.

### S22. Invite code exposed to all authenticated players
- **File:** `Services/Commands/ServerCommand.cs:53` and `Services/Commands/InviteCodeCommand.cs`
- **What:** `!info` and `!invitecode` have no admin check. Any authenticated player can retrieve and share the invite code.
- **Fix:** Add admin role requirement for invite code access.

---

## 4. Recommended Security Hardening (Priority Order)

### Immediate (before next release)
1. **Block `farmerDelta` for unauthenticated players** (S2) -- this is the most exploitable vulnerability
2. **Require API_KEY when API is enabled** (S3) or disable API by default
3. **Remove CORS wildcard** (S4)
4. **Remove `Program.enableCheats = true`** (S1) or add a debug command blocker
5. **Prevent `!login` password rebroadcast** (S8)

### Short-term (next 1-2 releases)
6. **Filter outgoing messages to unauthenticated players** (S9)
7. **Fix name-based command targeting** (S10, S11) -- join args for spaces, handle ambiguity
8. **Protect admin operators from admin-on-admin attacks** (S7)
9. **Use constant-time comparison for API key** (S13)
10. **Add API rate limiting** (S14)
11. **Use server-side timestamp for auth timeout** (S17)

### Medium-term
12. **Implement server-side game protocol validation** (S6) -- at minimum for `farmerDelta` money/inventory
13. **Respect lobby privacy settings** (S5)
14. **Persist bans to save data** (S19)
15. **Add command rate limiting** (S15, S16)
16. **Block `playerIntroduction` replay** (S20)
17. **Prevent chat spam via auth command prefixes** (S21)
18. **Add admin check for invite code commands** (S22)
