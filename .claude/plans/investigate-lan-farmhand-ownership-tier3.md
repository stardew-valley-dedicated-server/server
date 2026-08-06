# Account-based farmhand ownership for direct-IP players (Tier 3)

## Goal

Close the last ownership gap: farmhands created over a direct IP connection. Every other transport already resolves to an authenticated identity, so its farmhands are owned and enforced. IP connections resolve to nothing — `LidgrenClient.getUserID()` returns `""` (`LidgrenClient.cs:29-32`) and `LidgrenServer` passes `""` at both server seams (`:194,276`) — so those farmhands sit in a free-for-all pool that anyone connecting by IP can claim.

The identity for those players is a **credential** (username + password), verified **out of band** through a web login and carried into the existing ownership machinery as an ordinary identity. A platform ID may be linked to an account so those players skip the login.

**Permanently out of scope** (evaluated and rejected — do not reintroduce):
- **IP-bind / web-pins-IP.** DHCP changes lock owners out; NAT collapses roommates onto one identity; dual-device breaks; and behind Docker's bridge NAT the server can see the gateway address for everyone. Not real auth.
- **Best-effort menu filter with no join-time enforcement.** `checkFarmhandRequest` looks up `farmhandData[client-sent UID]` (`GameServer.cs:542`) deserialized straight off the wire (`LidgrenServer.cs:273-283`), so a crafted type-2 packet can name any UID. A filter is UX; the gate is the control.
- **Invite-code or platform identity as the IP path's identity.** The point is that raw direct-connect works with real auth and no platform identity.

## Status

The client-side mechanics are confirmed by a working proof of concept: the menu hold, the in-game code carried to a browser, the post-login reveal repopulating the slot list, and per-identity farmhand visibility all behave as specified against a stock client.

This plan builds on the farmhand-ownership feature (`features/farmhand-ownership.md`), which is implemented and in testing on its own branch. Everything below assumes that has merged.

## What this builds on

The ownership feature already provides, in `mod/JunimoServer/`:

- **`Util/ConnectionTransport`** — `TryResolveIdentity(connectionId, out TransportIdentity)` plus the `SN_`/`GN_`/`L_` prefix constants and `GetTransportName`. `TransportIdentity` is `(Platform, Id)`, with `steam` and `galaxy` as the platform values today.
- **`Services/AuthService/FarmhandOwnershipService`** — the ownership store (`farmhandUid → FarmhandOwnerRecord{Platform, Id, Origin}`, per save folder), `TryGetOwner`/`RecordOwner`/`RemoveOwner`/`MarkReleased`/`IsReleased`, the `EvaluateClaim` decision matrix shared by the visibility filter and the gate, the `CheckFarmhandRequest_OwnershipGate_Prefix` enforcement gate, and `CanAssignTo_ProtectOwnedSlot_Postfix` protecting owned slots from save-load re-homing.

**The gate records ownership by wrapping the `approve` delegate**, at the exact approve moment. Do not detect approval any later: `Game1.otherFarmers` also contains the uid when vanilla rejects an "already in use" request, and postfixes still run after a prefix cancels, so a rejected request would overwrite the live owner's record with the requester's identity.

**`EvaluateClaim` already branches on `hasIdentity`, and the IP gap is one of its branches.** For a customized, unstamped, unowned farmhand it returns `RejectLanPoolOnly` when the connection has an identity and `Allow` when it does not — the free-for-all. Give a logged-in IP connection an identity and the existing matrix covers it: owner comparison, released slots and pool protection all begin working for IP players with no new decision logic.

## What this adds

1. An **account identity** — `TransportIdentity` with platform `account` and the username as its id — produced once a connection completes a web login.
2. A **credential store**, separate from the ownership store.
3. The **hold, login and reveal** flow that lets an IP player prove that identity before any farmhand is offered.
4. **Rejection of identity-less IP connections**, replacing today's `Allow`.
5. Operator **account administration** on the console.

## Configuration

- **`Server.MaxFarmhandsPerPlayer`** (int, default `1`, `0` = unlimited, negatives clamp to `0` with a warning as `ClampBroadcastPeriod` does). Vanilla places no limit on how many farmhands carry one identity — `sendAvailableFarmhands` sends every available farmhand with no ownership filter (`GameServer.cs:636-642`), and the client-side lock only greys out farmhands belonging to *someone else* (`FarmhandMenu.cs:36-40`) — so `0` restores vanilla behavior. Counts farmhands owned by the identity in the current save.
- **Requiring a login** is gated by a setting that converges with the shipping `Server.EnforceFarmhandOwnership` at implementation time, once the ownership branch has merged. The behavior to express: enforcement off entirely; enforcement on with IP players logging in and platform players recognized automatically; and enforcement on with everyone logging in unless their platform ID is linked to an account. Read once at startup — the login listener, the hold driver and the gate are wired from it, so a mid-run settings reload must not change it.
- **`Server.LoginPagePort`** and **`Server.LoginPageUrl`** — see (d).

## Flow, end to end

### Returning player

| # | Player action | Server state |
|---|---|---|
| 1 | Joins by IP (needs `AllowIpConnections=true`). | `sendAvailableFarmhands` runs; `TryResolveIdentity` yields nothing for `L_`. |
| 2 | Sees a status message with a login address and a short code; no slots. | Hold recorded per connection; **no type-9 sent**. |
| 3 | Opens the address, enters username, password and the code. | Credentials verified, then the connection is bound to an account identity on the game thread. |
| 4 | Slots appear; picks their farmhand. | Reveal re-enters the prefix; the visibility filter now sees an identity and shows what that identity owns. |
| 5 | Joins. | The existing gate evaluates the claim against the ownership store. |

### New player

Identical, with registration at the login page instead of a login. No farmhand is provisioned: the player is offered one unowned slot, goes through the normal vanilla character creator, and the gate records ownership when the join is approved. `Game1.options.enableFarmhandCreation` stays at its default — the gate already rejects any request from a connection with no identity, so the customization surface is unreachable for anyone unauthenticated, and an owner customizing their own new character is the intended signup path.

## Components

### (a) Credential store — `AccountAuthService` (new, always-on)

- **Own JSON file**, separate from the ownership store, alongside the other server-side state. Written temp-then-atomic-replace so a crash cannot tear it, under a single lock covering both the game thread and the login page's HTTP threads, with the most restrictive file permissions the runtime allows.
- **Passwords are never stored or logged in any recoverable form.** PBKDF2-SHA256 via `Rfc2898DeriveBytes.Pbkdf2` (net6.0 static API), 16-byte random salt, 32-byte key, ~210k iterations **stored per record** so the cost can be raised later without invalidating existing hashes, compared with `CryptographicOperations.FixedTimeEquals` (in-tree precedent: `PasswordProtectionService`).
- **Record:** username (normalized: trimmed, lowercased, length- and charset-validated), salt, iterations, hash, creation timestamp, and linked platform IDs.
- **Ownership is not duplicated here.** Which farmhand an account owns lives in the ownership store, keyed by farmhand UID with `Platform = "account"` and `Id = username`. One fact, one home.
- **Linked platform IDs** map `platformId → username`, written when a platform client logs in, so that client's next connect is recognized without the login page.
- **Session state:** `connectionId → account identity`, game-thread only, never touched from an HTTP thread.

**Do not route account ids through the platform-id helpers.** `IsValidPlatformId` is a strict `ulong.TryParse` (`NumberStyles.None`, invariant) that a username fails, and `ClassifyPlatformId` returns `steam` for the Steam64 individual-account range and **`galaxy` for everything else** — so a username passed to it is silently tagged `galaxy`. Both exist for *operator-supplied bind ids* only (`CabinManagerService`, `FarmhandCommand`, `SaveImportService`); the live connection path takes its platform tag from `ConnectionTransport.TryResolveIdentity` and never touches them. Account code constructs `("account", username)` directly.

### (b) Hold and reveal — `FarmhandSenderService.SendAvailableFarmhands_Prefix`

The hold is a **pure status hold**: push a type-11 `connectionMessage` and send **no type-9**. Load-bearing in two directions:

- An empty type-9 is not a silent message. `receiveAvailableFarmhands` always builds a non-null list (`Client.cs:150`) and nulls `connectionMessage` (`:164`), so the menu falls to its `CoopMenu_NoSlots` branch and the status text is erased.
- Any type-9, empty or not, clears `gettingFarmhands`/`approvingFarmhand`, and `checkListPopulation` only rebuilds slots while one of those is set (`FarmhandMenu.cs:154-195`). A pure status hold leaves the latch armed, so the **first** type-9 after login repopulates clickable slots. Sending one during the hold would permanently prevent the reveal from rendering.

Placement: after the existing `!isGameAvailable()` deferral, which still defers first. The reveal re-enters the prefix and reaches the existing filter and send path. Concurrent waiters are isolated by `connectionId`, mirroring the existing `_reservedFarmhands` pattern, and the same stale-prune drops codes for dead connections.

**Visibility rule:** the farmhands this identity owns, plus one unowned slot when it owns fewer than `MaxFarmhandsPerPlayer`. At the default of `1` a returning player sees exactly their character and a new player sees exactly one free slot — and the existing single-unclaimed-slot limiting already produces that shape.

**Status payload (load-bearing).** The type-11 payload runs through `Game1.content.LoadString` → `parseStringPath`, which throws with no colon and splits on the **first** colon (`LocalizedContentManager.cs:694-697`). Use a real asset with a non-existent key — `"Strings\\UI:…"` — so the remainder returns verbatim via `?? path`; a bogus asset name before the colon throws. Design around, rather than fight: the `Strings\UI:` prefix is **visible** and cannot be hidden (SpriteText clamps draw-x to `>= 0`) or moved up (the block anchors at the viewport's vertical centre); line breaks are `^`, not `\n`; keep the text ASCII/Latin (`chat-font-language-tag.md` — the menu font has no glyph fallback); bake all values server-side.

**The hold must expire.** The client's 45s connect timeout (`Client.connectionTimeout = 45000`, `Client.cs:12`) is armed at connect (`:84`) and cleared only when `hasHandshaked` is set, which happens only in the type-9 handler (`:163`). A pure status hold cannot clear it and nothing stock can extend it, so **the login must complete inside that window** and the status text counts down. Expire a few seconds early and drop the client with a type-23 `forceKick` (`Multiplayer.receiveForceKick` → `Disconnect` + `returnToMainMenu`, `Multiplayer.cs:1513-1520`), which needs no farmerId — a held client has none, so `kick` is unusable. Letting the client's own timer fire instead shows a bare "Failed to connect" with no explanation.

### (c) Correlation — code in the game, credentials in the browser

The browser session and the game socket are unrelated channels; the proof is a secret the player carries between them. The reverse direction is impossible — the menu has no text input.

Maps `code → connectionId` and `connectionId → (code, issuedAt)`, game-thread only. The code is CSPRNG (`RandomNumberGenerator.GetBytes`, **not** `Game1.random` — a predictable code lets a third party bind someone else's pending connection), Crockford base32 minus ambiguous glyphs (`23456789ABCDEFGHJKMNPQRSTUVWXYZ`), 5 characters ≈ 28.6M space, rejection-sampled, collision-checked, single-use and consumed on bind. Stable per connection, new on reconnect; its lifetime is bounded by the connect timeout, so no separate TTL is needed.

### (d) Login page — its own listener and port

A dedicated player-facing HTTP listener on `Server.LoginPagePort`, **not** an `ApiService` route. The two surfaces have opposite exposure models: the API is operator-facing and guarded by `API_KEY`, while this page must be reachable by every IP player. Serving login from the API port would mean publishing the admin surface to players, and `API_ENABLED=false` (`ApiService.cs:1043`) would silently make the server unjoinable. A separate port is also independently proxyable for TLS.

`Server.LoginPageUrl` is the address shown on the connect screen; the server cannot know its own reachable address. Empty falls back to localhost on the configured port, resolved in one place so the address shown and the port bound cannot drift.

Routes: the form, `POST /login`, and `POST /register`. Both take credentials in the body, never the query string, so they stay out of access logs. Handling: rate-limit by IP and username → verify on the HTTP thread → `RunOnGameThreadAsync` for the bind, which is the only game-thread marshal and already rebinds correlation context (`asynclocal-pitfalls`). Failures return one generic message that never distinguishes username, password or code.

**Registration requires a live hold code.** Without that, anyone who can reach the page can create an account and consume a cabin slot. Requiring a code means only someone currently sitting at this server's connect screen can register, which already presupposes the address or invite code, and stacks with `SERVER_PASSWORD` when set.

### (e) Feeding the identity into the gate

Once a connection is bound, `sendAvailableFarmhands` and `checkFarmhandRequest` resolve its identity as `("account", username)` rather than "none". Three changes to the resolution path:

- Identity resolution consults the session map first, then `ConnectionTransport.TryResolveIdentity`, so an account identity takes precedence for a connection whose transport also carries one (a platform player logged into an account).
- An `L_` connection with **no** session, in a mode that requires login, resolves to no identity and is rejected — replacing the pool `Allow`. It should never reach the gate, since the hold precedes any reveal, but the gate is the control and must not depend on the filter.
- **`EvaluateClaim` needs an explicit `account` arm in its legacy-stamp branch.** For a farmhand carrying a platform `userID` stamp but no ownership record, the current non-Steam path returns `Allow` at join regardless of the stamp. That is correct for Galaxy only because GOG passes a non-empty userId at `checkFarmhandRequest` (`GalaxyNetServer.cs:236`), leaving vanilla `authCheck` to compare the stamp itself. LAN passes `""` (`LidgrenServer.cs:276`), where `authCheck` short-circuits to `true` — so an account identity inheriting that arm would be admitted to a platform-stamped farmhand it does not own. An account id can never match a numeric platform stamp, so the account arm rejects at both list and join.

### (f) Session lifecycle

Clear a connection's session on disconnect via a postfix on `GameServer.onDisconnect(string connectionID)` (`GameServer.cs:117`); every transport routes through it (`Server.onDisconnect`, `Server.cs:100-102`). `CabinManagerService.OnPlayerDisconnected_Postfix` is **not** the seam — it receives a farmerId, and a held connection has no farmer. Prune defensively by `isConnectionActive` as well, so a missed callback cannot strand a session.

`connectionId` is session-scoped: Lidgren derives `RemoteUniqueIdentifier` from MAC and ephemeral local port, and the Steam form embeds a per-session connection handle. A reconnect is therefore a new session with no carried-over auth, which is the desired property — a reconnect from another machine has no session and is not recognized. With no stock-client-compatible token, the model is **one web login per game session**.

### (g) Operator administration — console commands

An `accounts` command family on the SMAPI console, matching `SavesCommand`, `CabinsConsoleCommand` and `SettingsCommand`: list, add, remove, reset password, unbind and transfer. Console rather than chat, because a reset typed in chat lands in every client's chat log and the server log; console rather than the API, because it needs no published surface and no endpoint without a reader. Unbind and transfer call the ownership service's existing `RemoveOwner`/`MarkReleased`/`RecordOwner` with the operator origin, so account administration and platform administration share one code path.

These commands bind an owner by **username**, whereas the existing `FarmhandCommand` binds by platform id — two entry points to `RecordOwner` with different id shapes. The account commands validate the username against the credential store and build `("account", username)`; they must not reuse the platform-id helpers (see (a)).

## Security posture

**Properties this maintains:**
- **Crafted-packet hijack stays closed.** Enforcement is the existing gate on the universal `checkFarmhandRequest` choke; this plan only supplies it with an identity for a transport that had none.
- **No password on the game wire.** Credentials are entered in the browser, never in a Lidgren message — structurally better than the `!login` lobby password, which is sent over the game connection and is sniffable on a LAN.
- **No plaintext credentials anywhere** — not in the store, logs, error text or HTTP responses. The login page serves only its own routes and never the store.
- **The code is an ephemeral binder, not a credential.** Credentials are verified before the code is consulted, so it is not an oracle; brute-forcing a live code is ≈10⁻⁴ across its whole lifetime (CSPRNG, 28.6M space, single-use, sub-45s window, rate limit) and still requires valid credentials in the same request.
- **Registration is not open** — it requires a live hold code (d).

**Residuals, named not papered over:**
- **The login page needs HTTPS.** It is raw `HttpListener` over plain HTTP; without an operator-supplied TLS-terminating proxy the password crosses the network in cleartext, which on an untrusted network is no better than the lobby password. Log a startup warning when no proxy is indicated, and document it for operators.
- **Login must fit the connect timeout** — one web login per session, inside 45s; a slow login means reconnecting for a fresh code.
- **Screen-peek of a code.** An attacker who peeks a live code *and* holds their own valid credentials binds the victim's connection to the attacker's account — the victim sees the wrong character and reconnects. No account compromise, no data access.

## Runtime gates

Per `runtime-post-conditions-are-gates`, none may be closed by build or grep.

1. **Enforcement rejects a crafted request.** Drive a type-2 naming a UID the connection does not own; confirm the gate rejects and re-lists without disconnecting.
2. **An IP player's farmhand is protected from another IP player.** Two direct-IP clients, two accounts: the second cannot see or claim the first's farmhand — the behavior that does not exist today.
3. **An account identity cannot take a platform-stamped farmhand.** Log in over IP and request a farmhand carrying a Steam or Galaxy `userID` stamp; the join must be rejected, not admitted through the legacy-stamp branch (e).
4. **Concurrent waiters stay isolated.** Two clients held at once bind to their own accounts and reveal their own farmhands.
5. **`MaxFarmhandsPerPlayer` holds.** At `1` a returning owner is offered no additional slot; above `1` they are, up to the cap.
6. **Ownership survives reconnect and restart.** New session, new `connectionId`, same account, same farmhand.
7. **E2E harness can drive a login.** The suite connects over LAN, so tests in a login-requiring mode need a helper that recovers the code and posts it with credentials; tests that do not care run with enforcement off.

## Implementation ordering

1. **`AccountAuthService`** — hardened JSON credential store, PBKDF2, username normalization, linked platform IDs, session map, code generation.
2. **Mode plumbing** — converge with `Server.EnforceFarmhandOwnership`, plus `MaxFarmhandsPerPlayer`, `LoginPagePort`, `LoginPageUrl`; read once at startup. Add the E2E harness knob, defaulting to enforcement off.
3. **Identity resolution** (e) — session-first resolution, the account arm of the legacy-stamp branch, and rejection of identity-less IP connections (gates 1, 2, 3).
4. **Hold and reveal** (b) — countdown and `forceKick` expiry, visibility rule with the cap (gates 4, 5).
5. **Login page** (d) — own listener, login and code-gated registration, rate limiting.
6. **Session lifecycle** (f) — `onDisconnect` postfix and defensive pruning (gate 6).
7. **Console commands** (g).
8. **Test-side login helper** (gate 7) and coverage for each mode.

## Related plans

- [`features/farmhand-ownership.md`](features/farmhand-ownership.md) — the ownership store, identity parser, decision matrix and enforcement gate this plan extends to a third identity source.
- [`bugs/issue-2-farmhand-visibility.md`](bugs/issue-2-farmhand-visibility.md) — the visibility symptom both plans resolve.
- [`bugs/name-injection-item-grant-exploit.md`](bugs/name-injection-item-grant-exploit.md) — player-chosen names are sanitized at the `NetString` boundary, which covers names chosen in the vanilla character creator here.
