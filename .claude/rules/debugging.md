---
paths:
  - "mod/**/*.cs"
  - "tests/test-client/**/*.cs"
---

# LogLevel.Error in mod code is test poison

Logging at `LogLevel.Error` (or any line matching `\b(ERROR|FATAL)\b`) from mod code triggers `ServerContainer`'s error cancellation. Use `LogLevel.Warn` or `LogLevel.Trace` for benign-but-noteworthy conditions. The scan also treats an unprefixed `Process terminated.` line (.NET FailFast, e.g. missing libicu) as fatal, so a runtime hard-crash fails startup instead of masquerading as a transport timeout.

**Why:** `tests/JunimoServer.Tests/Containers/ServerContainer.cs` regex-matches SMAPI log-line headers against `\b(ERROR|FATAL)\b`. A match cancels `_errorCancellation`, which `ManagedServer` ties into test cancellation. The mod has no in-band signal that a log line poisoned a test; the failure surfaces as a downstream timeout or assertion. Multiple times mod code has logged at Error for recoverable conditions and silently failed unrelated tests.

**How to apply:** Before logging at Error level, ask: is this an actual test-failure-worthy condition, or a recoverable warning? If the latter, use Warn or Trace. The detector is regex-based on the formatted log line — even a custom logger that emits `[ERROR]` in its own format will trip it.

## The scan is SERVER-side only — test-client Error is loud, not poison

The `\b(ERROR|FATAL)\b` cancellation scan lives **only** in `ServerContainer.cs` (`HandleLine`); `GameClientContainer.HandleLine` has no scan and no `_errorCancellation`. So `LogLevel.Error` from test-client mod code (`tests/test-client/**`) is just a loud log line — the established tweaks (`SkipIntro`, `GodTool`, `ConvenienceTweaks`) deliberately log patch-install failures at Error as a fail-loud convention. Don't downgrade a genuine test-client failure to Warn out of poison-fear, and don't expect a test-client Error to fail the run — assertions are the gate there.