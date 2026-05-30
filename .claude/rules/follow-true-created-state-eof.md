---
paths:
  - "tests/JunimoServer.Tests/Containers/**"
  - "tests/JunimoServer.Tests/Helpers/Docker*.cs"
---

# `GetContainerLogsAsync(Follow=true)` returns immediate EOF when the container is in `Created` state

A `follow=true` log stream against a container that has been *created* but not yet *started* does not block. The daemon returns 200 OK with an empty multiplexed body and closes the connection — the first `MultiplexedStream.ReadOutputAsync` returns `EOF=true, Count=0` rather than awaiting future bytes. Treat first-read EOF with no prior successful reads as "container not running yet" and retry the open, exactly like the open-exception (`InvalidOperationException` from `IContainer.Id`) path. Only treat EOF as terminal when at least one successful read has occurred.

**Why:** ~30 min lost during the streaming refactor (`05-stream-container-logs.md`). All three call sites (`ServerContainer`, `GameClientContainer`, `SharedSteamAuth`) launch the streaming task **before** `container.StartAsync` per the `SuppressFlow` pattern (`asynclocal-pitfalls.md`). Testcontainers' `IContainer.Id` getter unblocks during *create* (when the daemon assigns an ID), well before *start* — so the streaming task's first `GetContainerLogsAsync` call typically races into the small window between create and run. Initial reader treated the immediate EOF as natural end-of-life and exited; smoke test reported `1 passed` while every `container.log` was empty (zero bytes, zero `forwardedVia` events in `infrastructure.jsonl`). Verified via standalone repro: pre-start `Follow=true` reliably returns EOF; post-start works as documented.

**How to apply:** When opening a `Follow=true` stream from a long-lived background task that may run before the container is started (the standard pattern here — see the three call sites and `SuppressFlow` rule), the read loop must distinguish *clean-EOF-after-data* (container exited; stop) from *clean-EOF-with-no-data* (container not running yet; retry). The second case is structurally identical to the `IContainer.Id` `InvalidOperationException` retry path. Don't add this branch only inside `ContainerLogStreamReader` — any new `follow=true` consumer that opens before `StartAsync` needs the same handling, including any future probe/exec stream that follows the same launch ordering.
