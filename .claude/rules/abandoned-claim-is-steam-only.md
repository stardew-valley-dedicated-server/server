---
paths:
  - "tests/JunimoServer.Tests/**/*.cs"
  - "mod/JunimoServer/Services/CabinManager/**"
  - "mod/JunimoServer/Services/Lobby/**"
---

# The abandoned-claim bug is Steam/GOG-only — LAN is structurally immune

The abandoned "New Farmer" slot bug requires `Farmer.userID` to be non-empty. That stamp happens in vanilla `Client.sendPlayerIntroduction` **only when `getUserID() != ""`**. `LidgrenClient.getUserID()` (LAN) returns `""`, so **LAN never stamps userID and is structurally immune to the bug**. Only Steam/GOG transports stamp a real platform ID.

**Consequence for E2E tests:** a live-client reproduction of the abandoned claim (connect → select "New Farmer" → reach character menu → disconnect) on the **default LAN harness** silently produces no stuck claim — the assertion "a farmhand has userID set but isCustomized=false" never becomes true. The default `[TestServer]` config is LAN; Steam requires `[TestServer(WithSteam = true)]` (method-level applicable). `STEAM_ACCOUNTS` is configured in `.env.test`, so Steam tests do run in this environment.

The save-load sweep can still be exercised on LAN by **injecting** a synthetic stuck userID via a test-only endpoint; only the **disconnect-path live reproduction** of a real client-stamped claim needs `[TestServer(WithSteam = true)]`.

**How to apply:** Use the transport-aware `Connect.WithRetryAsync(ct)` (`ConnectionRetryHelper` — branches on `Lease.RequiresSteamConnection`, stops at the FarmhandMenu without customizing), not hand-rolled LAN/invite-code connects (which fail "Invite codes not supported" on LAN). Any test needing a real client-stamped `userID` (platform identity, userID, ban-by-userID) must be `[TestServer(WithSteam = true)]`.
