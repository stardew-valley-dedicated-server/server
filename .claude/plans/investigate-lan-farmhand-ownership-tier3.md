# Investigate: enforced per-player farmhand ownership on raw IP/LAN (Tier 3)

## Goal

Give IP / direct-connect (LAN) players **enforced, real-credential, per-player farmhand ownership**, server-mod-only, against a **stock client**: a returning player resumes only *their* farmhand and cannot hijack another's — not even with a crafted packet. Steam/GOG already work natively (their `getUserID()` is a stable platform ID that vanilla `authCheck` enforces); raw IP is the only broken transport because `LidgrenClient.getUserID()` returns `""`.

This is an investigation + decision plan, not yet an implementation plan. It ends with a concrete design, the seams to build on, the honest residual limitations, and the live experiments that gate committing.

**Scope is deliberately narrow.** Three alternatives were investigated and **rejected by the user**, and are out of scope here — do not reintroduce them:
- **Raw IP-bind / web-pins-IP** — IP changes (DHCP) lock owners out; NAT collapses roommates to one IP; dual-device breaks; Docker bridge NAT makes the server see the gateway IP. Not real auth.
- **Best-effort menu-filter + soft session-lock** — too weak; doesn't stop a determined offline-by-name hijack.
- **Invite-code / Galaxy / Steam identity for the IP path** — "inherently not what we are trying to get working." The whole point is to make *raw IP / direct-connect* work with real auth, with no platform identity.

So: **the credential (username + password) is the identity.** It is verified **out of band** (a web login on the mod's existing HTTP server). IP is never the identity — at most an optional, off-by-default confirmation hint. All file:line refs below were verified against source (`decompiled/sdv-1.6.15-24356/`, `mod/JunimoServer/`) during the investigation.

## The problem, precisely (verified)

- Vanilla `authCheck` admits any farmhand when the connecting `userID == ""` **or** the farmhand's `userID == ""` (`GameServer.cs:495-506`, the `&&` short-circuits to `return true`). The LAN call site hardcodes `userId = ""` (`LidgrenServer.cs:276`), and `LidgrenClient.getUserID()` returns `""` (`LidgrenClient.cs:29-32`). So on raw IP, **every client passes auth for every farmhand** — the hole.
- `checkFarmhandRequest` looks up `farmhandData[client-sent UID]` (`GameServer.cs:542`) — the requested UID is deserialized straight from the wire (`LidgrenServer.cs:273-283` → `readFarmer`). So a menu filter is **advisory only**; a crafted `type-2` packet can name any UID. Real enforcement must live in a `checkFarmhandRequest`/`authCheck` prefix.
- The stock `FarmhandMenu` has a built-in "belongs to another player" lock (grey at 0.5 alpha, click→cancel, "Farmhand_Locked" tooltip — `FarmhandMenu.cs:29-41,57-64,212-216`) but it's gated on `Program.sdk.Networking.GetUserID() != ""`, which is **dead on LAN** — which is *why* LAN clients can pick anyone.
- IP connections are **disabled by default today** (`IpConnectionService` sets `Game1.options.ipConnectionsEnabled = Server.AllowIpConnections`, default false) precisely because "IP clients don't provide user IDs - farmhand ownership may not work correctly." Tier 3 is what lets an operator safely turn raw IP back on.

## The architecture (3 pillars)

1. **Hold at the menu.** Keep the stock client waiting at the `FarmhandMenu` (no farmhand selected → zero in-world manipulation surface) showing a status message, until out-of-band auth completes; then reveal **only** the authenticated account's farmhand(s).
2. **Enforce at the join.** An always-on Harmony prefix on `GameServer.checkFarmhandRequest` rejects any farmhand request whose target UID isn't owned by the authenticated identity for that connection. The menu filter is UX; this is the security gate.
3. **Close the new-player window.** Set `enableFarmhandCreation = false` so no client is ever handed the in-game `CharacterCustomization` window (the manipulation surface); new players are provisioned **out of band** at registration.

## The flow, end to end

### Returning player

| # | Player action | Server message / state | Source |
|---|---|---|---|
| 1 | Enters server IP in **Join LAN Game** (needs `Server.AllowIpConnections=true`). | `LidgrenServer` reads `userId=""`, `connectionId="L_"+RemoteUniqueIdentifier`; calls `sendAvailableFarmhands("", connectionId, …)`. | `LidgrenServer.cs:276,296-298` |
| 2 | FarmhandMenu opens, "Connecting…". | `SendAvailableFarmhands_Prefix` (always-on) sees game-available **but** `!IsAuthed(connectionId)` → mint/reuse a per-connection code, push **type-11** status (code + URL), defer the real list via `whenGameAvailable(reveal, () => IsAuthed(connectionId) && isGameAvailable())`, `return false` (no type-9). | `FarmhandSenderService.cs:127`; `GameServer.cs:472` |
| 3 | Reads *"Code 4F9A2 — log in at http://&lt;ip&gt;:8080/login"*. | Client: `connectionMessage = LoadString(payload)` (`Client.cs:129`); `getStatusText` returns it verbatim (`FarmhandMenu.cs:322-324`). No slots, **45s timer still armed** (see Risk 1). | `Client.cs:129`; `FarmhandMenu.cs:322-324` |
| 4 | Browser → URL → enters **username + password + code**. | `POST /login` (HTTP thread): rate-limit → PBKDF2 verify → `await RunOnGameThreadAsync(() => TryBind(code, account))`: resolve `code→connectionId`, assert `isConnectionActive`, resolve account's farmhand UID for this `saveId`, set `_authedByConn[connectionId]`, consume code. | `ApiService.cs:1680-1708,1989-1995` |
| 5 | Menu auto-updates. | Next tick: `receiveMessages` re-evaluates the deferred predicate (`GameServer.cs:268-279`), `IsAuthed` now true → reveal fires → re-enters the prefix, filter restricts type-9 to **only this account's UID(s)**, sends it (first type-9 for this connection). | `GameServer.cs:268-279` |
| 6 | Their farmhand slot(s) appear. | `receiveAvailableFarmhands` builds the list, `hasHandshaked=true`, `connectionMessage=null`, early-returns at the menu (no auto-join); `checkListPopulation` fills slots. | `Client.cs:150,163-167`; `FarmhandMenu.cs:174-178` |
| 7 | Clicks their slot. | `FarmhandSlot.Activate`: one `loadForNewGame`, `Game1.player=Farmer`, `sendPlayerIntroduction` (type-2). | `FarmhandMenu.cs:43-54` |
| 8 | Loads into their cabin. | type-2 → `checkFarmhandRequest("", connectionId, farmer, …)` → SafeLookup prefix (High) → **Tier-3 gate** (lower priority): validate wire UID against `_authedByConn[connectionId]` → match → vanilla `authCheck` (`userID==""`→true) → assign + `approve` → `peers[uid]=peer`. | `GameServer.cs:495-505`; `LidgrenServer.cs:281` |

### New player

Identical from step 2. The difference is **out-of-band registration** before/at first login: POST username+password (+ chosen name) → create a **global** PBKDF2 account → marshal to the game thread and provision a farmhand (`BuildNewCabin*Returning` → claim the `CreateFarmhand` placeholder → stamp `isCustomized=true` + Name + default appearance) → record `Bindings[saveId][username].Add(uid)`. **Do not stamp `farmer.userID`** (stays `""` on LAN; the durable binding is the mod-side map).

**Load-bearing:** the provisioned farmhand MUST be `isCustomized=true` before reveal — else the joining client pops `new CharacterCustomization(Source.NewFarmhand)` in-world after join (`Client.cs:226-228`), reopening the exact surface Tier 3 closes.

## The five components (seams verified in current code)

### (a) Hold + reveal — `FarmhandSenderService`

**Use the deferred-type-9 variant, not an empty type-9.** An empty type-9 sets `availableFarmhands=new List()` (Count 0, non-null) and `connectionMessage=null` (`Client.cs:150,164`), driving `getStatusText` to the `CoopMenu_NoSlots` branch (`FarmhandMenu.cs:330`) and **erasing the status text**. The deferred variant pushes type-11 only and withholds the list.

Add an auth gate at the top of `SendAvailableFarmhands_Prefix` (`FarmhandSenderService.cs:127`), mirroring the existing `!isGameAvailable()` deferral (`:153-191`) but using the **two-arg** `whenGameAvailable(action, customCheck)`:

```csharp
// after the existing !isGameAvailable() block (game-not-ready still defers first)
if (IsLanConnection(connectionId) && !IsAuthed(connectionId)) {
    sendMessage(new OutgoingMessage(11, Game1.player, BuildStatusPayload(connectionId))); // type-11
    if (!_pendingAuth.Contains(connectionId)) {
        _pendingAuth.Add(connectionId);
        whenGameAvailable(
            action: () => { _pendingAuth.Remove(connectionId);
                            if (isConnectionActive(connectionId))
                                SendAvailableFarmhands_Prefix(__instance, userId, connectionId, sendMessage); },
            customAvailabilityCheck: () => IsAuthed(connectionId) && __instance.isGameAvailable());
    }
    return false; // no type-9 during hold
}
```

The reveal re-enters the prefix and reaches the existing filter/serialize/send body (`:261-373`); constrain the filter (replace `IsFarmhandSelectableByUserId`, `:496-506`, for the LAN path) to serialize **only** the bound account's UID(s). N waiters are isolated by `connectionId` (distinct codes, closures, predicates), mirroring the existing `_reservedFarmhands` per-connection pattern.

**Status payload shape (verified, load-bearing):** the type-11 payload runs through `Game1.content.LoadString` → `parseStringPath`, which **throws `ContentLoadException` with no colon** and splits on the **first** colon (`LocalizedContentManager.cs:694-697`). A bogus asset before the colon also throws. Use a **real** asset with a non-existent key: `"Strings\\UI:Code 4F9A2 — log in at http://<ip>:8080/login"` (the `http://` colon is fine; only the first splits; missing key returns the path verbatim via `?? path`). Keep it ASCII/Latin (`chat-font-language-tag.md` — the menu font has no glyph fallback). Bake values server-side (no `{0}` substitution on this path).

### (b) Correlation — code-in-game → typed-in-browser

The browser session and the game socket are unrelated channels; the proof is a **secret the player carries from screen to browser**. The reverse direction (type a code at the menu) is impossible — the menu has no text input. So the code flows **game → browser**; credentials are entered in the browser.

**Maps (game-thread-only, no locks — matches the `_reservedFarmhands` invariant, `FarmhandSenderService.cs:45`):**
- `_connByCode: Dictionary<string,string>` (code → connectionId).
- `_waitingByConn: Dictionary<string,(string Code, DateTime IssuedAt)>` (TTL / reissue, keyed by stable `connectionId`).
- `_authedByConn: Dictionary<string,BoundAccount>` (the binding the gate reads).

**Code:** CSPRNG (`RandomNumberGenerator.GetBytes`, **not** `Game1.random`), Crockford base32 minus ambiguous glyphs (`23456789ABCDEFGHJKMNPQRSTUVWXYZ`), 5 chars ≈ 28.6M space, rejection-sampled, collision-checked, single-use (consumed on bind), ~10-min TTL backstop. Stable per connection (re-pushed type-11s reuse it); new only on reconnect.

**`POST /login`:** add to `isPublicEndpoint` (`ApiService.cs:1989`) — works before any API key, carries its own credential. Body (not query) `{username,password,code}`. Handler: rate-limit by IP+username → PBKDF2 verify on the HTTP thread → on success `await RunOnGameThreadAsync(() => TryBind(...))`. Generic 401 on any failure (don't reveal which of user/pass/code failed).

**Cross-thread (`asynclocal-pitfalls.md`):** only the bind + reveal-trigger marshal to the game thread, inside **one** `RunOnGameThreadAsync` (it already captures+rebinds correlation context, `ApiService.cs:1686-1691`). The maps are never touched from the HTTP thread.

### (c) Enforcement — always-on `checkFarmhandRequest` prefix

Register a **second** prefix on `GameServer.checkFarmhandRequest` at a priority **below** the existing `Priority.High` `CheckFarmhandRequest_SafeLookup_Prefix` (`NetworkTweaker.cs:96-108,272-320`), so existence + homeLocation are settled first. Co-locate in `NetworkTweaker` (owns the `checkFarmhandRequest` patch stack + the `RejectFarmhandRequestMethod` reflection, `:21-24,297-300`) or a new always-on `AccountAuthService`.

```csharp
public static bool CheckFarmhandRequest_Tier3Gate_Prefix(GameServer __instance, string userId,
    string connectionId, NetFarmerRoot farmer, Action<OutgoingMessage> sendMessage)
{
    // Scope: raw-IP/LAN ONLY. Steam(SN_)/GOG(GN_) carry non-empty userId → vanilla authCheck owns them.
    if (!(string.IsNullOrEmpty(userId) && connectionId != null && connectionId.StartsWith("L_"))) return true;
    if (farmer.Value == null) return true;                       // SafeLookup/vanilla handles null
    var requestedUid = farmer.Value.UniqueMultiplayerID;         // wire-controlled — must validate, never trust
    if (!AccountAuth.TryGetBoundAccount(connectionId, out var bound) || !bound.OwnsFarmhand(requestedUid)) {
        RejectFarmhandRequestMethod.Invoke(__instance, new object[]{ userId, connectionId, farmer, sendMessage });
        return false;                                            // reject (re-lists; no kick) — Debug log, never Error
    }
    return true;                                                 // owned → vanilla runs its other guards
}
```

The gate can only **reject or pass through** — it never calls `approve`, so it strictly tightens; every vanilla guard (already-in-use `:557-560`, availability, cabin-assign) still runs. The double discriminator (`userId==""` **and** `L_`) mirrors the source of truth (`mirror-target-component-resolution.md`). Log at `Debug`, never `Error` (`debugging.md` — server-side Error is test poison).

**Crafted-packet closure (traced through `LidgrenServer.cs:254-294`):** an unadmitted attacker isn't in `peers` (written only inside `approve`, `:281`); a crafted type-2 fails the in-world fast-path (`:269`) and routes to `checkFarmhandRequest` (`:273`); `readFarmer` deserializes the attacker-chosen UID; vanilla `authCheck` would `return true` for `userId==""` — **but the Tier-3 gate runs first and rejects** because the connection owns nothing (or not that UID). Hole closed.

### (d) New-player provisioning

Set **`Game1.options.enableFarmhandCreation = false`** — a genuinely new write (engine default `true`, `Options.cs:719`; the mod's only reference is a *read* at `FarmhandSenderService.cs:501`). Apply it in an always-on one-shot options-applier on SaveLoaded/new-game (alongside `NetworkTweaker.HandleNetworkSettings`, or as `IpConnectionService` does for `ipConnectionsEnabled`). Effects (verified): `authCheck` rejects uncustomized farmhands (`GameServer.cs:497-500`); the menu filter hides unclaimed slots (`FarmhandSenderService.cs:499-501`); the lobby stops advertising `newFarmhands` (`GameServer.cs:801`). Returning customized players unaffected.

Provision out of band: `BuildNewCabinReturning`/`BuildNewCabinVisibleReturning` (`CabinManagerService.cs:969/1019`) mint a cabin whose `Cabin.CreateFarmhand` placeholder is a full-schema `Farmer` (initialTools, quest "9", homeLocation, registered in farmhandData — `Cabin.cs:46-66`); claim it, stamp `isCustomized=true` (precedent `NetworkTweaker.cs:357`) + Name + default appearance; record the binding. Do **not** reuse the SaveImport XML transform (it demotes an existing master; a new player has none).

**Honest appearance limitation (verified):** a default-appearance farmhand plays fine, but in-game **full** re-customization is gated, not free — `Source.Dresser` is dead in 1.6.15 (referenced only inside `CharacterCustomization`, never constructed; there is no cabin dresser that opens it). The only wired full-appearance path is the **Wizard's Shrine** (`Source.Wizard`, `GameLocation.cs:12193-12198` — 500g, post-Dark-Talisman, mid/late game). So either set honest expectations ("change your full look later at the Wizard's Shrine") **or** add an optional web appearance form (all appearance fields are plain serialized scalars settable server-side). **Do not** tell players they can re-customize at a cabin dresser — that's false.

### (e) Account store + lifecycle — new always-on `AccountAuthService`

- **Store: global** (`ReadGlobalData`/`WriteGlobalData`, survives new-game/import — modeled on PersistentOptions/SaveImport, **not** RoleService's per-save `ReadSaveData`). PBKDF2-SHA256 via `Rfc2898DeriveBytes.Pbkdf2` (net6.0 static API), 16-byte salt, 32-byte key, ~210k iterations stored per-record, `CryptographicOperations.FixedTimeEquals` (already used `PasswordProtectionService.cs:1171`). Username normalized (trim+lower) as key.
- **Bindings:** `(saveId, username) → [farmhand UIDs]` in the same global blob; `saveId = Constants.SaveFolderName` (the discriminator the import finalizer trusts, `CabinManagerService.cs:1252`). Swap-host import **preserves UIDs** (the XML transform stamps a fresh UID only on the blank master, `SaveImportXmlTransform.cs:248-251`; existing farmhands keep theirs) → bindings survive a swap with no rebind. New game → new saveId → old bindings are harmless orphans. Lazy self-heal at gate time: drop any bound UID no longer in `farmhandData` (`TryGetValue`, `netdictionary-public-surface.md`) and re-persist.
- **Never `farmer.userID`:** `updateLobbyData` joins every non-empty `userID` into the **public** lobby string (`GameServer.cs:798-800`), and the codebase assumes `userID` is a numeric platform ID (`abandoned-claim-is-steam-only.md`). Empty `userID` + mod-side map is consistent with the existing LAN regime: `TryClearAbandonedClaim` skips empty-userID *and* customized farmhands (`CabinManagerService.cs:855-863`); `IsCabinAvailable` reads a customized farmhand as taken via the `isCustomized` flag (`:807-810`).
- **Per-connection auth state:** `_authedByConn` keyed by `connectionId` (pre-join identity; no peer UID at menu time), modeled on `PasswordProtectionService._playerAuthData` but a plain game-thread dict.
- **Disconnect cleanup:** ride the always-on choke `CabinManagerService.OnPlayerDisconnected_Postfix` (`:317-320`) — add `AccountAuth.ClearAuth(connectionId)`. `connectionId` is new per reconnect → **re-login every session** (the desired property: a reconnect from another machine has no auth entry and is rejected). No stock-client-compatible token persistence exists; IP-as-token is rejected; re-login-per-session is the honest model.

## Reachability + scoping groundwork (load-bearing)

- **Always-on placement (`harmony-patch-reachability.md`):** every piece lives in an unconditionally-constructed service — `FarmhandSenderService` (always-on, returns false), `NetworkTweaker` (5 unconditional patches), `CabinManagerService` (disconnect postfix), and a new `AccountAuthService : ModService` (auto-discovered + unconditionally constructed). **Nothing** rides `PasswordProtectionService`, which early-returns without `SERVER_PASSWORD`.
- **LAN-only gate:** `userId=="" && connectionId.StartsWith("L_")`. Steam (`SN_`) / GOG (`GN_`) carry non-empty userId → gate no-ops → vanilla `authCheck` enforces their ownership natively, byte-for-byte unchanged.
- **Coexist with lobby/password:** `PasswordProtectionService` gates *lobby→world* by `playerId` post-join; the Tier-3 gate gates *which farmhand* pre-join by `connectionId`. Different stages, different keys; both prefixes only reject-or-pass (order-independent).
- **Coexist with save-import finalizer:** the finalizer re-stamps the demoted owner's *platform* `userID` (Steam/GOG resume) in Layer B; Tier-3 bindings are a separate global map (credential resume). Independent identity channels, different fields. Import preserves UIDs → bindings stay valid.

## Security posture

**Defended:**
- **Crafted-packet hijack — CLOSED.** The always-on gate runs on the universal `checkFarmhandRequest` choke before vanilla's LAN short-circuit. Unauthenticated → owns nothing → every named UID rejected; authenticated → constrained to its own UID set.
- **Password never on the game wire.** Entered in the browser, checked in `POST /login` — never in any Lidgren message. Structurally better than the existing `!login` lobby password, which *is* transmitted over the game connection and sniffable on LAN.
- **The code is a low-risk ephemeral binder, not a credential.** `/login` rejects on bad credentials *before* consulting the code. Brute-forcing a live code is ≈10⁻⁴ over its whole TTL (CSPRNG + 28.6M space + single-use + ~10-min TTL + per-IP/per-username rate-limit) **and** still requires valid credentials in the same request.

**Honest residuals (no papering over):**
- **(a) Web password needs HTTPS.** `ApiService` is raw `HttpListener` on plain HTTP:8080. Without an operator TLS-terminating reverse proxy, the password is **cleartext over the network** — on an untrusted network this is NOT better than the lobby password. The off-the-game-wire win is real; full protection needs HTTPS in front, POST-body (not query, to stay out of access logs), and salted-hash storage. Document for operators.
- **(b) Appearance limitation.** Default-appearance new players re-customize fully only at the Wizard's Shrine (500g, mid/late unlock) — *not* a cabin dresser (dead in 1.6.15). Mitigate with an optional web appearance form, or set expectations.
- **(c) Re-login per session.** No client-side token possible with a stock client; IP-as-token rejected. One web login per game session.
- **(d) Physical screen-peek of the code.** Out of scope. Worst case if a live code is peeked + brute-forced: the attacker binds the *victim's* connection to the *attacker's own* account (a self-defeating nuisance — victim sees the wrong character, reconnects for a fresh code); no account compromise, no data access (attacker still lacks the victim's password).

## Why this has none of the prior dealbreakers

| Prior dealbreaker | Why it's gone |
|---|---|
| IP-bind → DHCP lockout | Identity is code+password; bind keys on `connectionId` + account, never an address. |
| NAT collision (Docker collapses clients to gateway IP) | Codes are per-connection, never per-IP. The optional IP *hint* is OFF by default. |
| Dual-device break | Code shown on the game device, typed into the browser device — no shared address required. |
| Best-effort / not real auth | `POST /login` verifies a salted-PBKDF2 credential; the gate rejects any UID not matching the bind. |
| In-session manipulation surface | Held at the menu (no farmhand selected) until auth; `enableFarmhandCreation=false`; new players provisioned out-of-band. |
| Invite-code identity (rejected) | No invite codes / platform identity for the IP path. The credential is the identity; the code is only a per-connection binder. |

## Risks / open questions — live experiments that MUST pass before committing

Per `runtime-post-conditions-are-gates.md`, none of these may be marked done by build/grep. All §components are confirmed at source *as code mechanics*; these four are runtime gates static reading cannot settle.

**Experiment 1 — THE 45s HOLD TIMEOUT (run FIRST; highest risk).** The vanilla 45s connect timeout is set at connect (`Client.cs:84`) and cleared **only** when `hasHandshaked` becomes true, which happens **only** in `receiveAvailableFarmhands` (the type-9 handler, `Client.cs:91-93,163`). Lidgren's own transport timeout (`ConnectionTimeout=30f`) is kept alive by `PingInterval=5f` keepalives once connected (`LidgrenClient.cs:49-50`), so the transport survives a hold — but the **application 45s timer stays armed** during a pure-defer hold. *(This corrects an earlier claim that "the 45s ceiling is a myth" — it dies only AFTER a type-9.)* Confirm whether a real stock client disconnects after 45s at the held menu. Then choose:
  - **(i)** "Log in within 45s, else reconnect for a fresh code" UX (the only purely-stock option — nothing sets `hasHandshaked` without a type-9); OR
  - **(ii) Hybrid:** send a single 0-farmhand type-9 to kill the timer, then re-push type-11. This relies on `getStatusText` showing `connectionMessage` **before** the empty-list `CoopMenu_NoSlots` branch (verified: `FarmhandMenu.cs:322` precedes `:330`), and the type-11 arriving **after** the type-9 (since `receiveAvailableFarmhands` nulls `connectionMessage`, `Client.cs:164`), and firing the 0-list **before** the 45s mark (else `client.timedOut` shows "Failed", which overrides everything, `FarmhandMenu.cs:318-320`). **The type-11-after-type-9 re-show MUST be confirmed live** — this is the one load-bearing timing interaction the whole hold rests on.

**Experiment 2 — Provisioned farmhand playable.** Server-side build cabin + claim placeholder + `isCustomized=true` + default appearance; connect a stock client, reveal, click, confirm it loads into its cabin and plays with **no** client-side `CharacterCustomization` popup (validates the `Client.cs:226-228` stamp closure).

**Experiment 3 — Reveal repopulates the held menu.** After a status-only hold, confirm the first real type-9 populates clickable slots via the `approvingFarmhand` latch (`FarmhandMenu.cs:174-178`).

**Experiment 4 — Bind + marshal end to end.** Code in type-11 → typed in browser with credentials → `/login` consumes it → `RunOnGameThreadAsync` flips auth → reveal fires within one tick. Confirm cross-thread correctness and that two concurrent waiters stay isolated.

**Experiment 5 (lower risk) — Status text renders.** Confirm the `Strings\\UI:<verbatim>` carrier renders ASCII URL+code cleanly at the menu (carrier-asset throw path + font).

## Config knobs (unprefixed `Env.cs` convention; opt-in defaults)

- `Server.AllowIpConnections` (exists, default **false**) — gates raw IP at all.
- `Server.RequireAccountAuth` (new, default **false**) — master enable for Tier 3. When on with raw IP enabled, the hold + gate + provisioning activate.
- Code TTL / rate-limit start as constants (no knob until an operator needs one — `holistic-or-explicit-todo.md`); per `verify-documented-config-is-consumed.md`, add any knob to docs only once code reads it.

## Implementation ordering (most value, least risk)

1. **`AccountAuthService` (new, always-on)** — global PBKDF2 store, `Bindings` keyed by `Constants.SaveFolderName`, the three game-thread maps, `TryGetBoundAccount`/`TryBind`/`IsAuthed`/`ClearAuth`/code-gen. *(Store/crypto + binding lifecycle across new-game/import verified.)*
2. **`FarmhandSenderService.cs:127`** — Tier-3 hold branch (type-11 + two-arg `whenGameAvailable` + `return false`); restrict the reveal filter (`:496-506` LAN path) to bound UIDs; extend the stale-prune (`:209-246`) to drop dead-connection codes. **Gated by Experiments 1 & 3.**
3. **`NetworkTweaker.cs:~108`** — register the Tier-3 gate prefix at `Priority.Normal`/`Low` (after the High SafeLookup), reading `AccountAuth.TryGetBoundAccount`, reusing `RejectFarmhandRequestMethod`. *(Seam/ordering/reject reflection verified.)*
4. **`ApiService.cs:1989`** — add `/login` (+ a registration route) to `isPublicEndpoint`; `POST /login` handler (HTTP-thread PBKDF2 → `RunOnGameThreadAsync(TryBind)`); web HTML via `WriteHtmlAsync`; per IP+username rate-limit. **Gated by Experiment 4.**
5. **Provisioning** — registration route `RunOnGameThreadAsync(() => provision)`: `BuildNewCabin*Returning` → claim placeholder → `isCustomized=true` + Name + default appearance → add binding. Handle "farm full" (None position cap) gracefully. **Gated by Experiment 2.**
6. **`enableFarmhandCreation=false`** — new write in an always-on SaveLoaded/new-game options-applier. *(No existing write-site; effects verified.)*
7. **Disconnect cleanup** — one-line `AccountAuth.ClearAuth(connectionId)` in `CabinManagerService.OnPlayerDisconnected_Postfix` (`:317-320`).
8. **Orphan reaper (concrete TODO, do not skip)** — registered-but-never-played accounts create a customized farmhand+cabin the abandoned-claim sweeps skip (they only clear *uncustomized*). Add a TTL reaper keyed on the account's registration timestamp that calls `DestroyCabin` (`CabinManagerService.cs:1090`) + deletes the binding. Acute under None's position cap. Per `holistic-or-explicit-todo.md`, ship wired or with an explicit TODO naming the gap.

## Bottom line

Tier 3 is buildable entirely server-side against a stock client: hold-via-deferred-type-9 + verbatim type-11 status, an always-on `checkFarmhandRequest` ownership gate that closes the crafted-packet hole, out-of-band web credential auth bound by a per-connection ephemeral code, server-side farmhand provisioning, and a global PBKDF2 account store with saveId-keyed bindings — all in always-on services, scoped LAN-only, coexisting cleanly with Steam/GOG, lobby/password, and save-import. Honest residuals: HTTPS-for-the-web-password, a mid-game-gated appearance fix, and re-login-per-session — none dealbreakers, all named. **Run Experiment 1 first** — whether the vanilla 45s connect timeout disconnects a slow browser-login during the pure-defer hold decides between "log in within 45s" UX and the hybrid 0-list-then-type-11 sequence, and it's the one place static reading can't settle the design.
