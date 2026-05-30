---
paths:
  - "tests/**/*.cs"
---

# `docker exec` is cheap idle but degrades under load — minimize exec COUNT in hot paths, and cut diagnostic-only execs

A `container.ExecAsync` round-trip costs ~0.24s on an idle daemon but degrades ~24× (to ~6s) under parallel-startup load on Windows Docker Desktop. So the real cost knob in a per-test or per-container hot path is the *number* of execs, not per-exec latency. Prefer one in-container loop over N C#-side poll execs (put the wait inside the shell — sub-second `sleep` costs no round-trip). And never issue a diagnostic exec whose result feeds no consumer.

**Why:** A recorder-startup investigation found a ~144s gap that was ~100 `docker exec` round-trips per recorder × ~6s each under load (measured: idle ~0.24s/exec, 8× CPU load ~0.7s, real parallel run ~6s). Every fix cut exec *count*, not per-exec cost: a 20-sample clock calibration burst collapsed to 1 exec (it was lock-serialized, so the first recorder's 20×6s≈120s blocked all others), and an 80-iteration C# poll loop (`Task.Delay(100)` + one exec each) collapsed to a single in-container `sleep 0.1` loop. Separately, a per-test finalize `MeasureCurrentOffsetAsync` exec cost ~6.4s × 53 tests purely to emit an `offsetDriftMs` field that no consumer read — and the event catalog itself said the field "does NOT translate to clip mis-placement; investigate only when cross-clip burn-in alignment also regresses." All of this is invisible locally because exec looks free on an idle daemon.

**How to apply:** When adding or reviewing code in a per-test/per-container hot path that calls `ExecAsync`: (1) if you're polling for an in-container state change, do the wait loop *inside one shell exec* (`for i in $(seq ...); do <check>; sleep 0.1; done`) rather than N C#-side execs — the per-iteration round-trip is what's expensive under load, not the in-shell sleep. (2) Before adding a diagnostic exec, grep for a consumer of the field it populates; if its result flows only into an `InfrastructureEventLog.Emit` field that nothing downstream reads, don't add it (and cut it if you find one). Distinct from `test-timing.md` (which warns against *overstating* savings from per-test overhead summing): this rule is about the exec-count cost mechanism under load and the unconsumed-diagnostic-exec cut.
