---
paths:
  - "mod/JunimoServer/**"
---

# `HandleAutoPause` is the only writer of `IsPaused` — never flip it for a tick from another service

Other services must not write `Game1.netWorldState.Value.IsPaused = false` to fix what a client sees. If a serialized snapshot must read unpaused, mask the flag around that one serialization (prefix sets false, postfix restores the captured value, no game tick in between); if a player has genuinely become present, let the presence count in `AlwaysOn.HandleAutoPause` unpause on the next tick.

**Why:** The join path unpaused the world so the introduction packet would not carry `IsPaused=true`. `Game1.gameTimeInterval` is never reset by the pause, so every unpaused tick banks its elapsed milliseconds toward the next ten-minute tick — about 200 ms at `SERVER_TPS=5`, seven seconds per tick. An unauthenticated client, who never counts as present, could advance the clock ten in-game minutes with roughly 35 connections and burn the day with enough of them. The fix masks the flag inside `sendServerIntroduction` only, and the one remaining synchronous unpause happens at authentication success, where the player is present by definition.

**How to apply:** When a fix needs the world unpaused "just for a moment", ask which of the two shapes it is: a snapshot that must read a value (mask around the serialization) or a player who now counts (presence handles it). Reject any third shape. A lobby, kick, or timeout path must leave the pause alone and keep its registrations until the disconnect actually lands, or the player counts as present for the ticks in between.
