---
paths:
  - "tests/**/*.cs"
---

# Don't infer wall-clock cost from per-test numbers

Per-test timing in a parallel suite isn't wall-clock cost. Each host's `serverSlots` (in `SDVD_DOCKER_HOSTS`) caps concurrent server containers — typical local config is 3 — and xUnit dispatches all ~96 methods concurrently against those slots. Artifact phases overlap with other tests' bodies. Two specific traps follow:

1. **Per-test overhead × N overstates savings.** Summing `N_tests × per_test_overhead` is wrong unless the overhead is on the critical path of the longest-running slot. Proven 2026-04-17: removing the 3s `Task.Delay` in `TestBase.DisposeAsync` dropped per-test artifacts wall-time from ~4.9s to ~1.6s, but total run duration was unchanged (552s vs baseline 538s–643s). The ~230s–276s "savings" on paper were ~0s in practice.

2. **`queueDurationMs` is xUnit dispatch-wait, not a broker bottleneck.** Per-test `queueDurationMs` (and `queueDurationTotalMs` in `summary.json`) is the gap between xUnit dispatching the test method and the broker handing it a server slot. With ~96 tests and 3 slots, most sit waiting — the ~26× queue/active ratio is xUnit's design, not something to attack. Raising it would mean either bumping each host's `serverSlots` (hard system-load cap) or rewriting orchestration.

**Why:** Both traps caught by user pushback during real perf reviews. The interesting signal is the **critical path** — the longest-running test on the slowest slot at any moment — not per-test sums.

**How to apply:**
- Before estimating speedup from removing per-test overhead, check whether it's on the critical path. Does run duration ≈ `sum(test_durations) / parallel_slots` plus setup? If so, the overhead is already overlapped. Instrument the longest test per slot, not the average. Run with every host's `serverSlots: 1` to see serial cost, then back out what parallelism absorbs.
- Use `queueDurationTotalMs` only to confirm slot saturation, not as a broker-efficiency metric.

**Corollary — check secondary correctness when removing "useless" delays.** At `SERVER_FPS=1`, removing the 3s `TestBase.DisposeAsync` delay also broke video recordings (final frames missing) because ffmpeg's x11grab sample schedule isn't phase-locked with the game's draw schedule. The delay was doing real work beyond what its comment claimed.
