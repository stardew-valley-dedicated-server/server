---
paths:
  - "mod/**/*.cs"
---

# `OneSecondUpdateTicked` fires every 60 game ticks, not every real second — at low SERVER_TPS it's 12× too slow

SMAPI's `GameLoop.OneSecondUpdateTicked` fires every **60 game ticks**, not every real second. The server pins the tick rate to `SERVER_TPS` (`ServerOptimizer.cs` sets `Game1.game1.TargetElapsedTime = 1000/Env.ServerTps` ms), so at the proven-stable `SERVER_TPS=5` the loop runs 5 ticks/sec and `OneSecondUpdateTicked` fires every **12 seconds**, not 1. Every per-second host-automation handler in that loop therefore runs ~12× slower than its name implies. For idempotent state-reconcilers (close a stray menu, click an empty-mailbox dialog) a 12s delay is harmless; for a **latency-sensitive handler that must fire repeatedly in sequence** — stepping a multi-box cutscene, anything where each invocation unblocks the next — 12s/step is a functional failure.

**Why:** The wedding-host "play-through" attempt drove dialogue-clicking from `OnOneSecondUpdateTicked` and took ~70s (watchdog-tripping) purely because the handler fired every 12s — confirmed in the server log (clicks exactly 12s apart). The name "OneSecond" actively misleads: it's a real bug that it isn't one second at `SERVER_TPS=5`, but the cadence is SMAPI's (60-tick) and can't be changed from mod code. (`server-tps-headless.md` establishes 5 is the stable headless TPS; this rule is the orthogonal "what that does to per-second handlers" trap.)

**How to apply:** When adding or moving a host-automation handler, ask whether its latency matters. If it just reconciles a one-shot state and a multi-second delay is acceptable, `OnOneSecondUpdateTicked` is fine. If it must fire promptly or step a sequence, put it in `OnUpdateTicked` (per-tick — fires reliably even when in-game time is frozen, which is why the festival and wedding handlers live there) and do any "once per N seconds" gating yourself off wall-clock (`DateTime.UtcNow`) or a tick counter scaled by `Env.ServerTps` (e.g. `BannerDelaySeconds * Env.ServerTps`), never off the `OneSecond` cadence. Don't assume `OneSecondUpdateTicked` ≈ 1s on this server.
