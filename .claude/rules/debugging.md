---
paths:
  - "mod/**/*.cs"
  - "tests/test-client/**/*.cs"
---

# LogLevel.Error in mod code is test poison

Logging at `LogLevel.Error` (or any line matching `\b(ERROR|FATAL)\b`) from mod code triggers `ServerContainer`'s error cancellation. Use `LogLevel.Warn` or `LogLevel.Trace` for benign-but-noteworthy conditions.

**Why:** `tests/JunimoServer.Tests/Containers/ServerContainer.cs` regex-matches SMAPI log-line headers against `\b(ERROR|FATAL)\b`. A match cancels `_errorCancellation`, which `ManagedServer` ties into test cancellation. The mod has no in-band signal that a log line poisoned a test; the failure surfaces as a downstream timeout or assertion. Multiple times mod code has logged at Error for recoverable conditions and silently failed unrelated tests.

**How to apply:** Before logging at Error level, ask: is this an actual test-failure-worthy condition, or a recoverable warning? If the latter, use Warn or Trace. The detector is regex-based on the formatted log line — even a custom logger that emits `[ERROR]` in its own format will trip it.

## The scan is SERVER-side only — test-client Error is loud, not poison

The `\b(ERROR|FATAL)\b` cancellation scan lives **only** in `ServerContainer.cs:811`. The test-CLIENT container (`GameClientContainer.HandleLine`) forwards SDVD events and the log callback but does **not** regex for ERROR and has no `_errorCancellation`. So `LogLevel.Error` from **test-client mod code** (`tests/test-client/**`) is **not** test poison — it's only a loud log line. The established test-client tweaks (`SkipIntro`, `GodTool`, `ConvenienceTweaks`) deliberately log patch-install failures at `Error` as a *fail-loud* convention, and that's correct *because* it doesn't cancel the run — the poison rule above is the server-only case.

**How to apply:** When choosing a log level in `tests/test-client/**`, treat Error as "loud and conventional for real failures (patch-install, init)", not as a run-canceller. Don't downgrade a genuine test-client failure to Warn out of poison-fear (that fear only applies to `mod/**`), and don't assume a test-client Error will fail the run — it won't; CI/assertions are the gate there.