---
paths:
  - "mod/**/*.cs"
---

# LogLevel.Error in mod code is test poison

Logging at `LogLevel.Error` (or any line matching `\b(ERROR|FATAL)\b`) from mod code triggers `ServerContainer`'s error cancellation. Use `LogLevel.Warn` or `LogLevel.Trace` for benign-but-noteworthy conditions.

**Why:** `tests/JunimoServer.Tests/Containers/ServerContainer.cs` regex-matches SMAPI log-line headers against `\b(ERROR|FATAL)\b`. A match cancels `_errorCancellation`, which `ManagedServer` ties into test cancellation. The mod has no in-band signal that a log line poisoned a test; the failure surfaces as a downstream timeout or assertion. Multiple times mod code has logged at Error for recoverable conditions and silently failed unrelated tests.

**How to apply:** Before logging at Error level, ask: is this an actual test-failure-worthy condition, or a recoverable warning? If the latter, use Warn or Trace. The detector is regex-based on the formatted log line — even a custom logger that emits `[ERROR]` in its own format will trip it.