---
paths:
  - "tests/JunimoServer.Tests/**/*.cs"
  - "mod/JunimoServer/Services/CabinManager/**"
  - "mod/JunimoServer/Services/Lobby/**"
---

# The abandoned-claim bug is Steam/GOG-only — LAN is structurally immune

The abandoned "New Farmer" slot bug requires `Farmer.userID` to be non-empty. That stamp happens in vanilla `Client.sendPlayerIntroduction` **only when `getUserID() != ""`**. `LidgrenClient.getUserID()` (LAN) returns `""`, so **LAN never stamps userID and is structurally immune to the bug**. Only Steam/GOG transports stamp a real platform ID.

**Consequence for E2E tests:** a live-client reproduction of the abandoned claim (connect → select "New Farmer" → reach character menu → disconnect) on the **default LAN harness** silently produces no stuck claim — the assertion "a farmhand has userID set but isCustomized=false" never becomes true. The default `[TestServer]` config is LAN; Steam requires `[TestServer(WithSteam = true)]` (method-level applicable).

The save-load sweep can still be exercised on LAN by **injecting** a synthetic stuck userID via a test-only endpoint; only the **disconnect-path live reproduction** of a real client-stamped claim needs `[TestServer(WithSteam = true)]`.

**How to apply:** Use the transport-aware `Connect.WithRetryAsync(ct)` (`ConnectionRetryHelper` — branches on `Lease.RequiresSteamConnection`, stops at the FarmhandMenu without customizing), not hand-rolled LAN/invite-code connects (which fail "Invite codes not supported" on LAN). Any test needing a real client-stamped `userID` (platform identity, userID, ban-by-userID) must be `[TestServer(WithSteam = true)]`.

## A returning-player userID is NOT always a 17-digit Steam64 — don't validate it as one

`authCheck` matches a returning player to a farmhand by raw **string equality** on `farmhand.userID.Value == getUserID()` (`GameServer.cs:495-506`) — there is no platform-prefix check, and the stored value is whatever that player's client returns. Both platform transports stamp **Galaxy-space** values: `GalaxyNetClient.getUserID()` returns the GOG Galaxy ID's uint64 (`GalaxyNetClient.cs:54` → `GalaxyInstance.User().GetGalaxyID().ToUint64()`), and `SteamNetClient.getUserID()` (`SteamNetClient.cs:104` → `Program.sdk.Networking.GetUserID()` → `SteamNetHelper.GetUserID`, `SteamNetHelper.cs:106-117`) ALSO returns `GalaxyInstance.User().GetGalaxyID().ToUint64()` — the account's **GOG Galaxy pseudo-id**, NOT the 17-digit Steam64 `7656119…` (empirically verified against a real Steam client's stamp; falls back to `""` when Galaxy auth fails). The connection-level Steam64 exists only server-side (the SDR connection identity) and shares **no id space** with client stamps — which is why the ownership map (`FarmhandOwnershipService`) keys on the transport identity instead of stamps.

**Why:** A `saves import --swap-host-to <id>` plan validated the operator-supplied bind id as a `7656119…`-prefixed Steam64. That would reject every legitimate GOG player (their Galaxy uint64 has no Steam prefix), a fail-fast that bars a valid input (the new-rejection check in `universal/verify-claims.md`).

**How to apply:** When validating or binding a player userID (auth, ban-by-id, identity-resume binds), accept **any non-empty all-digit `ulong`**, not a Steam64-prefixed value — Steam64 and Galaxy-uint64 both qualify, and equality is all `authCheck` enforces. Name such flags/args platform-neutrally (`--swap-host-to`, not `--steamid`). LAN's `""` still means "no enforcement" (the rule above).
