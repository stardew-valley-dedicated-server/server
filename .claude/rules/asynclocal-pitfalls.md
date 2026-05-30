---
paths:
  - "mod/**/*.cs"
  - "tests/**/*.cs"
  - "tools/**/*.cs"
---

# AsyncLocal flows through awaits, not through external queue pumps

Use `AsyncLocal<T>` (not `ThreadLocal<T>`) for any ambient correlation identifier that must flow through `await` in async code. But `AsyncLocal` is scoped to `ExecutionContext`; it does NOT flow across boundaries where your work is pumped by an external loop you don't `await` — the game-loop thread, SteamKit callback threads, any `Queue<Action>` drained by someone else.

For those boundaries, capture the current value at enqueue-time and re-bind inside the queued work:

```csharp
var captured = SomeContext.Current;
_pendingActions.Enqueue(() =>
{
    using var _ = SomeContext.Begin(captured);
    action();
});
```

**Boundaries that break flow in this codebase:**
- `ApiService.RunOnGameThreadAsync` / `TestApiServer.ExecuteOnGameThread` → game-loop queue.
- SteamKit `CallbackManager.RunCallbacks()` → library-owned thread.
- HttpListener handlers without `ConfigureAwait(false)` (continuation may resume on a different thread-pool thread; `ThreadLocal<T>` silently loses the value).
- xUnit v3 `IAsyncLifetime`: captures the EC *before* `InitializeAsync` runs and re-uses it for the test method body. A custom AsyncLocal mutated inside `InitializeAsync` doesn't reach the body. Use `Xunit.TestContext.Current` (xUnit's own ambient) for test-time identity instead of rolling your own.

**Why:** Each boundary above is anchored to a real "field is null on this event" bug. The HttpListener case lost `requestId` on every `http_served` event for months; the game-thread queue lost it on every `cabin_*` and `client_chat_sent` event until capture+rebind was added; the xUnit case lost test attribution on ~3000 `http_request` and 295 `/wait/*` envelopes. All looked correct in code — the bugs were entirely about execution-context boundaries.

**How to apply:**
- For new ambient-context types, default to `AsyncLocal<T>`. Use `ThreadLocal<T>` only if the consumer is strictly synchronous end-to-end with no awaits.
- Before emitting a structured event that reads `SomeContext.Current` from a non-trivial call path, trace the path. If it crosses `Queue<Action>.Enqueue` / `ConcurrentQueue<T>.Enqueue` / `CallbackManager.Subscribe` / a `Task.Run` that's pumped externally, the current value will NOT flow. Capture at the caller, re-bind in the callee.
- For test-time identity, prefer `Xunit.TestContext.Current` over a custom AsyncLocal.
- `Task.Run` without an external pump *does* flow ExecutionContext — don't add capture+rebind to every task.
- When diagnosing "field X is null on event Y", check the execution-context boundary first, not the emit code. To quantify a coverage gap: `grep -c '"event":"<name>"' infra.jsonl` vs `grep '"event":"<name>"' infra.jsonl | grep -cE '"<field>":[ ]*\{'`.

## Long-lived background `Task.Run` must suppress flow, not capture+rebind

For background tasks that **outlive the calling test** (process-wide stats collectors, per-server health watchdogs, per-container log streamers, broker prestart kicked off by the first test that touches the singleton), the *opposite* problem applies: the EC flowing in poisons every event the task ever emits with whichever test happened to first wake the singleton. Wrap such `Task.Run` calls in `using (ExecutionContext.SuppressFlow()) { ... }` so they start with a clean EC. Inside, `TestContext.Current` is null and structured emits route to the no-test path correctly.

**Why:** Caught 2026-04-30. `TestResourceBroker` constructor did `Task.Run(StartPrestart)`. The first test to touch `Instance` (xUnit dispatched all 98 simultaneously) seeded the EC. Result: every `container_started`, `client_created`, `health.check_failed`, and `instance_stats` event for the rest of the run was attributed to `FarmhandManagementTests.DeleteFarmhand_WhenOffline_Succeeds` — making the diagnostic log unreadable. ~430 misattributed events on that one test alone. The same shape applied to `ContainerStatsCollector.Start()` (1s emission loop), `ManagedServer.StartHealthWatchdog()` (5s health probe), and the per-container `StreamLogsAsync` Task.Runs in `ServerContainer`/`GameClientContainer`.

**How to apply:** When you write `Task.Run(...)` in test infrastructure, ask: does the task's lifetime equal or outlive the calling test? Long-lived emitters (stats loops, watchdogs, stream pumps), singleton initializers (broker prestart, image build, network create), and anything triggered from a constructor that any test can wake first — all need `SuppressFlow`. Short-lived per-test work (prewarm called from the test's body, an awaited helper) should NOT suppress flow — losing test identity there breaks per-test attribution of normal work.
