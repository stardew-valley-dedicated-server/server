---
paths:
  - "mod/**/*.cs"
---

# Server→client control primitives — what the server can make a vanilla client do

Farmhand clients are unmodded vanilla (CLAUDE.md), so any feature that needs a client to move, see, or do something must map onto net-synced server-authoritative state or one of these verified vanilla messages — there is nothing else:

1. **Passout message** (`Multiplayer.passout`) — the ONLY server-initiated player warp: the client fades and warps to the named location+tile (`Farmer.performPassoutWarp`, `Farmer.cs:5841`). **Fee-gated**: unless the player's *source* location is `FarmHouse`-derived, `Cellar`, or `PassOutSafe`, the client charges the pass-out fee and sends pass-out mail (`Farmer.cs:5869-5877`). `FarmerExtensions.WarpHome` is fee-free only because its callers have the player standing in a lobby cabin when the message arrives — its docstring doesn't say so.
2. **`GameLocation.warps` mutations** are net-synced and client-effective: the lobby's exit-warp removal (`LobbyService.cs:1687`) and the hidden-cabin exit rewrite (`CabinExtensions.SetWarpsToFarmFarmhouseDoor`) are production proof. Per-location, shared by all players — no per-player warp targets on a shared location.
3. **Forced events (message 4) cannot serve as warps**: the client resolves the event id against its *own* content (custom events don't exist there) and returns the player to the pre-event location afterward (`Multiplayer.cs:1616-1642`).
4. **Message 5 observes every client warp** server-side (`GameServer.cs:698-724`) — observation is free; intervention is not.

**Why:** The per-player-farms investigation first designed farmhand warp routing around client-side patching, which the vanilla-client fact invalidates wholesale; the workable design had to be rebuilt from exactly these primitives (hub geometry instead of arrival redirects). The passout fee gate is the sharpest residual trap: `WarpHome`'s comment ("does a screen fade and then warps the player") reads like a general-purpose teleport, and reusing it while the player stands outdoors silently charges them the pass-out fee and sends pass-out mail.

**How to apply:** When a design needs vanilla clients to change location or behavior, map it to a primitive above before writing anything; if none fits, the design changes, not the client. Before reusing `WarpHome`/the passout message, check what location type the player occupies when the message arrives — from outdoors, expect fee + mail. Sibling of [`harmony-patch-reachability.md`](harmony-patch-reachability.md), which covers why client-side patching is off the table at all.
