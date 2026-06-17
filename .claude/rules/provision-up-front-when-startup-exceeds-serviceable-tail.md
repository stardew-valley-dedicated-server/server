---
paths:
  - "tests/JunimoServer.Tests/Infrastructure/**"
  - "tests/JunimoServer.TestRunner/**"
---

# Reacting harder to tail contention is a churn trap when startup cost exceeds the window the new resource can serve

When a resource is provisioned on-demand in response to load/contention (an extra server instance, a spun-up worker, a new container), weigh its **provisioning cost** against the **remaining window it could actually serve** before adding it. If the thing takes longer to come up than the time left for it to do useful work, "react faster to pressure" makes things *worse* — it pays the full startup cost to serve almost nothing. The fix for a late-arriving resource is usually to **provision it up front** (at prestart/warmup, when the cost overlaps idle time and the resource is guaranteed work), not to make the reactive trigger fire sooner.

**Why:** Investigating a slow E2E suite tail, the dominant test config funneled ~10 join-needing tests through a single server instance at the end of the run, each waiting behind the others on a per-instance join gate. The broker's reactive expansion (`TestResourceBroker.TryReuseFreedSlotAsync`) fired *correctly* the instant a slot freed — but a server boots in **~41s** (`server_started.durationMs` median), and the instance it created at the tail (`17:55:38`) served **exactly one** test before the run ended. Startup (~41s) dwarfs teardown (~4.6s `container_stopped.disposeDurationMs`), so the asymmetry is the trap: making expansion react harder just produces more 41s boots that serve 0–1 tests. The actual win was front-loading a second instance for the dominant config at **prestart** (`AllocateInstances`), where the boot overlaps warmup and the instance has the whole run's backlog to chew through — zero churn.

**How to apply:** Before "make the broker/expansion react more eagerly to contention," ask: how long does the new resource take to become useful, and how much serviceable work remains when the trigger would fire? If `provisioning_cost ≳ remaining_serviceable_window`, on-demand creation is net-negative — move the allocation **up front** (prestart/warmup) and size it for the dominant consumer, rather than reacting at the tail. Verify the costs from run data (`server_started` / `container_stopped` durations), don't assume them. Adjacent to `retry-is-evidence-of-root-cause.md` (a reactive band-aid often hides the real fix) and `test-broker-invariants.md` (the broker capacity/eviction invariants this provisioning timing interacts with).
