# Server-error abort is reported as silent cancellation, never a failure

**Status: traced, not fixed. Own PR — independent of the Debian 13 base bump it was found under.**

When a server dies *after* becoming ready (e.g. a fatal mod ERROR during deferred init, once
tests are already running against it), the run ends as
`passed: 3, failed: 0, canceled: 164, aborted: false, abortReason: null` — zero failures, cause
visible only in a `wait` diagnostic. An operator reading the summary sees a mysteriously canceled
run, not a failing server. A server that never becomes ready is the *other* shape and already
surfaces correctly, as real `failed` readiness-timeout entries — the silent shape is specific to
post-ready deaths, where every victim is a cancellation: the first test's CT cancels, stopOnFail
fans out via `TestResourceBroker`'s `_runCts`, and later acquisitions fail through
`BuildAcquisitionFault` (whose `_stopOnFailNotified && !host.IsPoisoned` branch labels them as
collateral rather than cause).

## Traced mechanism

Test-side symbols, all in `tests/JunimoServer.Tests` unless noted:

- `ServerContainer` scans server logs for `\b(ERROR|FATAL)\b` (`IgnoredErrorPatterns` has only
  `"XACT"`); `FlushError` → `_errorCancellation.Cancel()`.
- `TestLifecycle` treats any cancelled test CT as stopOnFail → `NotifyStopOnFail()` →
  `TestResourceBroker`'s `_runCts.Cancel()` → mass cancel; each victim re-triggers. Because the
  trigger is a cancellation, nothing is recorded as failed (`TestRunState.ApplyTestFailed`
  classifies purely by exception type — `TaskCanceledException`/`OperationCanceledException` →
  `"canceled"`).
- `abortReason` is runner-side only (`RunRecorder.SetAbortReason`). `TestSummaryFixture.SetAborted()`
  is called on the prestart-failure path but nothing reads it — a genuinely missing hop.
- Machinery that already exists and should be reused: `RendererBase.ReclassifyCanceledAsFailed`
  (wired to `test_enrichment` in CIRenderer/LLMRenderer/WebRenderer),
  `ManagedServer.PoisonReasonCode.ServerLogError`, `Lease.IsPoisoned`/`Lease.ErrorToken`,
  `RecordTestFailure`'s `"infrastructure"` stamp, and the `instance_poisoned` IPC event.

## Open questions

- **Which path actually cancelled the observed runs** was never identified: the prestart path did
  not fire (no `Pre-start failed` text) and `run_stall_watchdog_tripped` was absent. Broker
  `TestLog` output goes to the console, not into `TestResults/` — capture the console output when
  reproducing.
- Repro needs a server that turns unhealthy after becoming ready (see the shape distinction
  above) — e.g. a mod command/flag that logs a fatal ERROR on demand mid-test.

## Dead end — do not retry

Subscribing `OnErrorDetected → PoisonServer(ServerLogError)` in the `ManagedServer` constructor
plus a `RecordServerErrorFailure()` hop in TestBase/TestLifecycle was tried and reverted: it
produced no poison events, and it cannot cover the cancellation victims — they die inside
acquisition and never hold the `Lease` that hop requires.
