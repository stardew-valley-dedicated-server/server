---
paths:
  - "mod/JunimoServer/**"
---

# `HandleAutoPause` is the sole lifecycle writer of `IsPaused` — never leave it flipped for a tick from another service

Other services must not write `Game1.netWorldState.Value.IsPaused = false` to fix what a client sees. Only three shapes are allowed: (1) mask the flag around a single serialization that must read unpaused (prefix sets false, postfix restores the captured value, no game tick in between); (2) when a player has genuinely become present, let the presence count in `AlwaysOn.HandleAutoPause` unpause on the next tick; (3) at authentication success, unpause synchronously — the player is present by definition. `HandleAutoPause` stays the normal lifecycle writer in every case.

**Why:** The join path unpaused the world so the introduction packet would not carry `IsPaused=true`. `Game1.gameTimeInterval` is never reset by the pause, so every unpaused tick banks its elapsed milliseconds toward the next ten-minute tick — about 200 ms at `SERVER_TPS=5`, seven seconds per tick. An unauthenticated client, who never counts as present, could advance the clock ten in-game minutes with roughly 35 connections and burn the day with enough of them. The fix masks the flag inside `sendServerIntroduction` (registered unconditionally in `CabinManagerService`, so it also covers the passwordless default — see `harmony-patch-reachability.md`); the remaining synchronous unpauses happen at authentication success (the immediate unregister and the deferred post-transition path in `OnDayStarted`), where the player is present by definition.

**How to apply:** When a fix needs the world unpaused "just for a moment", map it to one of the three shapes above — a snapshot that must read a value (mask around the serialization), a player who now counts (presence handles it next tick), or authentication success (unpause synchronously). Reject anything else. A lobby, kick, or timeout path must leave the pause alone and keep its registrations until the disconnect actually lands, or the player counts as present for the ticks in between.
