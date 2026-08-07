# Farmhand ownership: transport-scoped visibility + server-authoritative ownership enforcement

Fixes GitHub issue #2 (players see/select farmhands they don't own) and the save-import `--swap-host-to` bind. Supersedes the "blocked on platform-ID mapping" verdict in `../bugs/issue-2-farmhand-visibility.md` — the ownership map below IS that mapping.

## Context

On a mixed Steam + direct-IP deployment, both players could select any existing farmhand. Investigation findings (verified against decompiled vanilla + E2E run artifacts):

1. **Vanilla sends every offline farmhand to every client** — `GameServer.sendAvailableFarmhands` has no userID filter (`GameServer.cs:636-642`). The "can't take someone else's slot" experience comes from (a) client-side gray/click-block in `FarmhandMenu.FarmhandSlot` (`FarmhandMenu.cs:29-41,212-216`) and (b) join-time `authCheck` (`GameServer.cs:495-506`), whose ownership compare is skipped when **either** side's userID is `""`.
2. **LAN has no identity**: Lidgren passes `""` at both list and join (`LidgrenServer.cs:194,276`); LAN clients stamp nothing (`LidgrenClient.getUserID()` → `""`). So LAN can claim anything, and LAN-created farmhands are claimable by anyone — vanilla design.
3. **The ID-space split (empirically proven)**: vanilla Steam clients stamp their **GOG Galaxy pseudo-ID** (`SteamNetClient.getUserID` → `SteamNetHelper.GetUserID` → `GalaxyInstance.User().GetGalaxyID()`, falls back to `""` on Galaxy-auth failure). Run-artifact proof: a real Steam test client stamped `203778699715098839` (Galaxy `ID_TYPE_USER`, value >> 56 == 2), stable across runs 12 days apart — **not** the account's `7656119…` Steam64. The SDR server only knows the connection's Steam64. **There is no local mapping between the two spaces** (GOG's backend assigns the pseudo-account). Vanilla's own `SteamNetServer` therefore passes `""` to both `sendAvailableFarmhands` AND `checkFarmhandRequest` (`SteamNetServer.cs:234,442`); our `SteamGameServerNetServer` inherited this. Passing the Steam64 instead would lock every Steam player out of their own farmhand.
4. **Latent save-import bug** (decision: fix in this task): the `--swap-host-to` bind is stamped into `farmhand.userID` (`SaveImportXmlTransform.cs:203`, re-stamped `CabinManagerService.cs:1923`), but no transport ever presents a Steam64 to `authCheck`, and clients gray by Galaxy-space compare — so the bound owner sees their own farmhand **locked** on any Steam/GOG client; the only claim path was the LAN free-for-all this task closes.

**Requirements:** (1) fix the SDR join gap holistically; (2) never send IP-created farmhands to Steam users; (3) never send platform farmhands to IP clients; GOG gets exact-match narrowing; never break the "New Farmer" slot; one filter site, one enforcement seam; fix the save-import bind in-task.

## Design: `FarmhandOwnershipService` (new always-on ModService)

A server-authoritative **ownership map** keyed by transport-authenticated identity. This is the clean fix for SDR (the connection's Steam64 IS cryptographically authenticated by Steam; the stamps are not), subsumes the save-import bind, and is the same `checkFarmhandRequest` gate seam the Tier-3 LAN-accounts plan (`../investigate-lan-farmhand-ownership-tier3.md`) will extend later — one identity system, not two.

### Store
- `Dictionary<string saveId, Dictionary<long farmhandUid, OwnerRecord{Platform: "steam"|"galaxy", Id: string}>>` persisted via `helper.Data.Read/WriteGlobalData` (pattern: `PersistentOptions.cs:26-54`, `GameLoaderService.cs:22,74`); saveId = `Constants.SaveFolderName` (matches Tier-3's binding shape; survives reimports since import preserves farmhand UIDs; orphan saveIds harmless). Write-through on every mutation (approve-record, lifecycle clears, `release`/`rebind`) — cheap, and the crash window is then only "since last mutation".
- Game-thread-only access, no locks (matches the `_reservedFarmhands` invariant).
- Load-time self-heal: on SaveLoaded drop entries whose uid is no longer in `farmhandData`.

### Transport identity (one parser)
New `Util/ConnectionTransport.cs`: prefix constants + `TryResolveIdentity(connectionId)` (doc comment names `SteamGameServerNetServer.ConnectionDataToId` and vanilla `GalaxyNetServer.getConnectionId` as the format owners — keep-in-sync pointers per `one-parser-per-contract`):
- `SN_{steam64}_{connHandle}` (our SDR, `SteamGameServerNetServer.cs:556-559`) → steam/Steam64
- `GN_{galaxyUint64}` (vanilla `GalaxyNetServer.cs:192`) → galaxy/GalaxyId (same value vanilla passes as userId)
- `L_…` → none (LAN)
Rewire `FarmhandSenderService.GetTransportName` onto the same constants.

### Enforcement gate (always-on Harmony prefix on `GameServer.checkFarmhandRequest`)
Registered in the new service (unconditionally constructed — `harmony-patch-reachability`), priority **below** `NetworkTweaker`'s `Priority.High` SafeLookup prefix (`NetworkTweaker.cs:96-108`); hoist its private `RejectFarmhandRequestMethod` reflection handle to one shared internal member for rejects (one handle, not a second reflection copy — `one-parser-per-contract`). Gate can only reject-or-pass-through (never approves) — all vanilla guards still run. Look up the farmhand via `farmhandData.TryGetValue`. Matrix (F = target farmhand, I = transport identity from connectionId, C = claimed userID on the incoming root, S = stored stamp):

| F state | I = none (LAN) | I = steam (SDR) | I = galaxy (GOG) |
|---|---|---|---|
| map-owned | reject | allow iff map matches I | allow iff map matches I |
| unmapped, S≠"" (legacy platform) | **reject** | allow iff C == S (continuity bootstrap) | pass through (vanilla authCheck compares I vs S — same space) |
| unmapped, S=="", customized (IP farmhand) | allow (LAN pool, until Tier 3) | reject | reject |
| fresh (uncustomized, no stamp/map) | allow | allow | allow |

SDR keeps passing `""` to vanilla (`SteamGameServerNetServer.cs:313` unchanged) — enforcement lives in ONE place, the gate. Reject logs at `Debug` (never Error — `debugging.md`).

Gate robustness: Harmony runs ALL prefixes even when a higher-priority one cancels the original — the gate must take `bool __runOriginal` and no-op when the SafeLookup prefix already cancelled, and pass through on `farmer.Value == null`, uid missing from `farmhandData`, and `!isGameAvailable()` (mirror SafeLookup's early-returns, `NetworkTweaker.cs:280-302` — SafeLookup/vanilla own those states).

Steady-state property: the SN "can't narrow legacy stamped" row self-heals — once each farmhand's owner has joined once post-update, every platform farmhand is mapped, and SDR visibility converges to exact own-only narrowing too.

### Recorder (inside the gate prefix, by wrapping `approve`)
The gate prefix declares `ref Action approve` and, when I is present, wraps it: `approve = () => { map[saveId][uid] = I; originalApprove(); }`. Ownership is born from the **transport**, at the exact approve moment, never from client-declared data — "only gate-admitted identities ever reach the map" is then true by construction. Do NOT detect approval by postfix + `Game1.otherFarmers.ContainsKey(uid)` (the `PasswordProtectionService.cs:203` pattern, benign there): `otherFarmers` also contains the uid when vanilla *rejects* a request for a currently-online farmhand ("already in use", `GameServer.cs:557-560`), and postfixes run even after a prefix cancels — a rejected request would overwrite the online farmhand's owner record with the requester's identity. Precedent for record-inside-approve: `SteamGameServerNetServer.cs:335` raises `FarmhandAccepted` inside its approve callback for RoleService.

### Visibility filter (rewrite `IsFarmhandSelectableByUserId`, the single list site, `FarmhandSenderService.cs:~661` — line refs drift with the in-flight test spike; anchor on the method name)
Inputs: farmhand, transport identity I (from connectionId). The `userId` params become vestigial: change the SN list path (`SteamGameServerNetServer.cs:494-496`) to pass `""` like vanilla (`SteamNetServer.cs:234`) so nobody later mistakes the param for a trusted identity; the connect Info log reads the `ConnectionTransport`-parsed identity instead.

| F state | LAN | steam (SDR) | galaxy (GOG) |
|---|---|---|---|
| map-owned by I | — | show | show |
| map-owned by other | hide | hide | hide |
| unmapped, S≠"" | hide | show (can't narrow — Galaxy-space stamps; vanilla client grays others') | show iff S == I.id |
| unmapped, S=="", customized | show | hide | hide |
| fresh | show (enableFarmhandCreation; existing single-slot limiter + reservations unchanged) | same | same |

Rewrite the doc comment (current one claims "authCheck() verifies during join" — false on SDR) and fix the misleading `SteamGameServerNetServer.cs:493` comment.

### Lifecycle cleanup (map entry removal alongside every existing stamp clear)
- `CabinManagerService.TryClearAbandonedClaim` (`:870-901`) + `CleanupAbandonedCabinClaim` + `ClearAbandonedCabinClaimsOnLoad`: clear map entry too, **and restructure the guard**: an abandoned claim is now `!isCustomized && (stamp != "" || map entry exists)`. The current early-return on empty stamp would strand a **map-only claim** — a Galaxy-auth-failed Steam client stamps nothing, but the recorder maps them at approve; if they abandon, only the map holds the claim.
- `ApiService.ExecuteFarmhandDeletion` / `CabinManagerService.DestroyCabin`: remove entry.
- Operator commands (console, alongside `saves import`): `farmhand release <name|uid>` (clear map + stamp → slot returns to the LAN pool) and `farmhand rebind <name|uid> <platformId>` (operator-driven platform migration without an open window). Vanilla `/unlink` clears only the stamp, not the map — documented; `release` is the supported path.

### Config escape hatch
New `Server.EnforceFarmhandOwnership` in `ServerSettings.ServerRuntimeSettings` (server-settings.json — precedent: `AllowIpConnections`, `ServerSettings.cs:52`, consumed `IpConnectionService.cs:34`; NOT Env.cs, which is infra/env config), default **true**. When false: gate no-ops and the visibility filter reverts to the permissive behavior; the recorder KEEPS recording (claims are legal in that mode, so the map stays truthful for a later re-enable). Why it must exist: one legitimate use case is structurally incompatible with strict ownership until Tier-3 accounts — **one human playing their own farmhand from two transports** (Steam at the desk, a second device over direct IP — same network or remote). Strict mode locks the direct-IP device out of the steam-mapped farmhand (no identity to match; `rebind` can't express dual ownership). Operators with that shape disable enforcement; everyone else gets strict by default. Consumer-verified per `verify-documented-config-is-consumed` before the doc line lands.

### Save-import bind redesign
- **Layer A** (`SaveImportXmlTransform.ApplySwap`): stop stamping `<userID>` on the demoted owner (`:203`); keep `isCustomized=true`. **Remove** the XML userID-collision guard (`FindUserIdConflict`, `:186-193`): it guarded stamp-equality ambiguity that per-farmhand ownership records eliminate, it contradicts the multiple-farmhands-per-identity decision, and `farmhand rebind` can create the identical state uncheckedly anyway. A mis-typed bind is corrected via `farmhand release`/`rebind` — document that as the operator correction path.
- **Layer B** (`CabinManagerService` finalize step 8, `:1919-1923`): replace `owner.userID.Value = intent.UserId` with ownership-map write + `owner.userID.Value = ""` (clears any legacy stamp so vanilla graying can't lock the owner out). No bind-time uniqueness check: the map is per-farmhand, so a bind id that already owns another farmhand is the supported multiple-ownership case, not a conflict.
- **Bind id classification**: all-digit ulong (unchanged validation, per `abandoned-claim-is-steam-only`); Steam64 range `[76561197960265728, 76561202255233023]` → platform=steam, else → platform=galaxy. Result: bound owner connects via Steam invite → SDR Steam64 matches map → farmhand visible + join approved + clickable (no stamp to gray it).
- **Operator-discoverable ids**: a GOG "profile id" from the website is NOT the type-prefixed Galaxy uint64 the transport presents. Log each connection's transport identity at Info on connect (the connect log already dumps userId — ensure it's the copy-pasteable value) and document: "use the id shown in the server's connect log".

### Diagnostics (test assertion surface — `tests-assert-via-http-api`)
`/diagnostics/state`: add `HasOwner` + `OwnerPlatform` (bool/string, no raw IDs — matches the `OwnerHasUserId` precedent) to `FarmhandData[]` and `Cabins[]`; plumb through `ServerApiClient` schema types.

## Files touched
- **New**: `mod/JunimoServer/Services/AuthService/FarmhandOwnershipService.cs`, `mod/JunimoServer/Util/ConnectionTransport.cs`
- `mod/JunimoServer/Services/AuthService/FarmhandSenderService.cs` (filter rewrite + transport helper rewire)
- `mod/JunimoServer/Services/SteamGameServer/SteamGameServerNetServer.cs` (comment fixes only)
- `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs` (heal/sweep/destroy map-clear; Layer B finalize step 8)
- `mod/JunimoServer/Services/SaveImport/SaveImportXmlTransform.cs` + `SaveImportService.cs` (Layer A de-stamp; collision-guard removal; bind classification)
- `mod/JunimoServer/Services/Api/ApiService.cs` (+ deletion hook, diagnostics fields), `ApiService.TestEndpoints.cs` (if a `/test` ownership seed is needed for the visibility/ownership tests)
- Console command registration site for `farmhand release|rebind` (follow `saves import` precedent)
- `mod/JunimoServer/Services/Settings/ServerSettings.cs` + `ServerSettingsLoader.cs` (+ server-settings doc line): `Server.EnforceFarmhandOwnership`, default true
- Tests: `SaveImportTests.cs` (HasUserId→HasOwner assertions; collision test replaced by a multiple-ownership assertion — the same bind id may own several farmhands), `AbandonedClaimTests.cs` (heal also clears owner), **new** `FarmhandVisibilityTests.cs`
- Rules/docs: fix the false "SteamNetClient returns the 17-digit Steam64" claim in `.claude/rules/abandoned-claim-is-steam-only.md` (empirically Galaxy-space); update `save-import-layer-timing.md` Layer B (map bind, userID cleared) and `cabin-system.md` invariants 7/8 (clear = stamp + map entry); operator docs for `--swap-host-to` (Steam64 or Galaxy uint64) and the visibility/ownership semantics.

## Compatibility verification (adversarial pre-check, read-verified)
- **PasswordProtection/lobby**: its `checkFarmhandRequest` prefix ignores `userId` and only captures spawn data; postfix keys on approval. Gate+recorder are order-independent (reject-or-pass only). Lobby redirect operates on whatever list is sent. Unaffected.
- **Reservations / single-unclaimed-slot / EnsureAtLeastXCabins** (`FarmhandSenderService.cs:289-403`): operate downstream of the filter on fresh slots — the matrix keeps fresh slots for all transports.
- **Abandoned-claim flow**: uncustomized+stamped/mapped slots follow owner rules (resumable by ghost owner, hidden from LAN); heal/sweep clears both → slot returns to pool. `IsCabinAvailable` (`CabinManagerService.cs:807-855`) already excludes active/stamped owners; mapped-but-unstamped-uncustomized is transient (active while connected — `owner.isActive()` already excludes it, `:842` — healed on disconnect, swept on load), so no map check is needed there.
- **masterplayer/host**: host farmer is not in `farmhandData`; untouched.
- **LAN E2E suite**: test farmhands are LAN-created (unstamped, unmapped) → visible to LAN clients as before; `/test/stamp_claim` slots asserted via API, not client lists. `AbandonedClaim_OnDisconnect` selects an uncustomized slot on Steam → fresh-slot row.
- **Vanilla graying**: stamps stay client-written (never fight the client-authoritative Farmer root); Steam↔Steam graying keeps working.
- **Claim-path coverage**: `checkFarmhandRequest` is the single claim choke on all three transports, including crafted message-2 packets (`LidgrenServer.cs:273-283`, `GalaxyNetServer.cs:232-242`, `SteamGameServerNetServer.HandleFarmhandRequest`).

## Verification (runtime gates — `runtime-post-conditions-are-gates`)
1. `dotnet build mod/JunimoServer/JunimoServer.csproj` clean.
2. `make test FILTER=SaveImportTests` — bind assertions green against the new map semantics.
3. `make test FILTER=AbandonedClaimTests` — heal clears stamp AND owner.
4. New `FarmhandVisibilityTests`:
   - LAN client list excludes a stamped slot (seed via `/test/stamp_claim`, fresh LAN connect, assert `connect.Farmhands` lacks it but has the fresh slot).
   - `[TestServer(WithSteam=true)]` returning-Steam-player test: customize → disconnect (`WaitForPlayerRemovedById`) → reconnect → own farmhand visible → rejoin approved (first E2E of the resume path; also proves recorder+gate). Read the server log for the gate's decision lines (`passing-test-isnt-proof-the-scenario-ran`).
   - Mixed-direction test (Steam server + LAN second farmer): IP farmhand hidden from the Steam client's list; platform farmhand hidden from a fresh LAN client's list. (Verify at implementation that a WithSteam test server still accepts LAN connects — `Farmers.ConnectSecondFarmerAsync` path; if not, split across two servers.)
5. Full suite locally before PR.
6. *(Optional, documentation-grade)* Decompile the mobile builds to record how the mobile `FarmhandMenu`/`Program.sdk.Networking` handles greying. Not load-bearing: server-side enforcement covers all client builds.

## Honest boundaries (inherent, documented — not fixable server-mod-side)
- **In-world data exposure is vanilla-inherent**: `sendServerIntroduction` ships the full `NetWorldState` — including `farmhandData` with every offline farmhand and their stamps — to every client that completes a join (`GameServer.cs:398`). Menu filtering and the join gate are the enforceable surfaces; a modified client that gets in-world as ANY farmhand can read (not take) all farmhand data. Takeover stays blocked by the gate.
- **Lobby metadata publishes stamps (vanilla)**: `updateLobbyData` joins every non-empty `farmhand.userID` into the public `"farmhands"` lobby string (`GameServer.cs:798-801`) — readable by anyone who can resolve the lobby, without joining. Vanilla friend-join UX depends on this; left untouched. The ownership map never enters lobby metadata.
- **Bootstrap window**: the first post-update claim of each *legacy* stamped farmhand on SDR is admitted when the claimed userID on the incoming root equals the stored stamp. This is a courtesy fence, not authentication: the server itself delivers the stamp to every connecting client in the type-9 farmhand list, and a vanilla client only overwrites that field when its own `getUserID()` is non-empty (`Client.cs:186-196`) — so a client with no platform identity (e.g. a Steam client whose Galaxy auth failed, `SteamNetHelper.cs:113-116`) echoes the stamp back and passes for ANY legacy farmhand, which its menu also shows ungrayed (`FarmhandMenu.cs:33-40`). The row therefore only blocks clients whose working platform login presents a *different* id. Accepted because: strictly tighter than today's zero SDR enforcement, the window closes permanently at each farmhand's first claim, and a squatted claim is operator-recoverable via `farmhand release`/`rebind`.
- **Cross-transport lockout edge**: if a Steam-build client ever joins via the Galaxy fallback (SteamLobbyId lobby-metadata race), it gets mapped as platform=galaxy; a later SDR join by the same human mismatches → hidden/rejected until operator `rebind`. Rare (requires the race) and remediable; strict mapping is kept in preference to weakening the gate with client-claimed cross-links.
- **LAN↔LAN stays a free-for-all among IP-created farmhands** until Tier-3 accounts land; this plan builds the gate/store seam Tier-3 plugs into.

## Design decisions
- Multiple farmhands per platform identity allowed (vanilla parity, no cap) — consequently **no bind-time uniqueness checks** in save-import (both former collision guards removed; `release`/`rebind` is the correction path for a wrong bind).
- Platform migration is operator-driven (`farmhand release`/`rebind`), not self-service — closing the migration paths is the point of requirements 2/3.
- Legacy-farmhand bootstrap window on SDR accepted + documented honestly (a courtesy fence for sorting well-behaved clients, not authentication; one claim per farmhand, then strict).
- Enforcement toggle is a binary full-off (`Server.EnforceFarmhandOwnership`), serving dual-transport households including remote direct-IP devices; the recorder stays active while off.
- Tier-3 (LAN accounts) remains a separate future feature; this task builds the gate/store seam it plugs into.

## Related plans & issues
- **Supersedes** `../bugs/issue-2-farmhand-visibility.md` — its "blocked on platform-ID mapping" verdict is resolved by the ownership map; this plan closes issue #2.
- `../investigate-lan-farmhand-ownership-tier3.md` — account-based credential auth, extending the same gate + store (its §(e) gate and this service must merge into one, not two identity systems). It carries the ownership-mode setting (`Open`/`Automatic`/`Accounts`) that supersedes `Server.EnforceFarmhandOwnership` below, and the connectionId-derived identity parser both plans share.
- `../features/security-farmerdelta-auth-filter.md` — different concern (unauthenticated farmer-delta sanitization) on the same `checkFarmhandRequest` seam; coordinate Harmony prefix priorities if both land.
- `../features/server-discord-auth.md` — reads the `userId` arg captured at `checkFarmhandRequest` as "the platform ID"; that arg is **empty on SDR**. When implemented it should resolve identity via `ConnectionTransport`/this service instead.
- `../features/tests-fast-client-join.md` — relies on unstamped+customized farmhands being selectable by LAN clients; the visibility matrix preserves that row (its cited `FarmhandSenderService.cs` line numbers will drift).
