---
paths:
  - "tests/JunimoServer.Tests/**/*.cs"
---

# To repro "class B leases the server class A just used", delay B's InitializeAsync — and verify the order from artifacts

When a bug needs one test class to deterministically inherit another's pool server (same config
hash, e.g. state the first class left behind), don't rely on dispatch order — xUnit dispatches all
classes concurrently and the broker serves whoever's acquire completes first. Force the order with
a scratch (never-committed) override on the inheriting class:

```csharp
public override async ValueTask InitializeAsync()
{
    await Task.Delay(TimeSpan.FromSeconds(240)); // outlast the producer class's full acquire
    await base.InitializeAsync();                // the server lease happens in here
}
```

Size the delay against the producer's *full acquire latency*, not its dispatch time — acquisition
includes client-slot waits, so a 2-client class can take well over a minute to even claim its
exclusive turn. Then confirm the order actually happened from run artifacts (`queueDurationMs` per
test, server-log timestamps), never by assumption.

**Why:** Reproducing a CI failure where PacingProbeTests leased the post-wedding server burned ~5
runs. A combined `FILTER="A|B"` run put the probes first twice; a 60s delay still lost the race
because WeddingTests' acquire (2 client slots) took longer than that; 240s worked. A tempting
shortcut — driving the server's `/test/*` endpoints directly mid-scenario from a shell — was not a
faithful substitute: the immediate post-event window had frozen location updates (the probe entity
neither settled nor was deleted), measuring a different world state than the real next-lease
scenario with the inheriting class's own connected client.

**How to apply:** Use the delay override only as a disposable repro/verification harness (apply on
both the pre-fix and fixed builds, then revert). Confirm each run's actual execution order before
trusting its result — a green run whose ordering silently flipped is a void run, not evidence. If
you must probe a live server mid-run instead, treat any measurement taken during event teardown or
other paused windows as suspect.
