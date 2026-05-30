# Plan: Relativize absolute repo-root paths in test-harness output

## Context

Test-harness log lines currently print absolute filesystem paths, e.g.:

```
[Client] 22:14:07.709 client-2 [Recording] client-2: retrieving to D:\Development\projects\stardew-dedicated-server\repos\server\TestResults\runs\2026-05-28T22-00-23Z_f0e43e8\containers\client-2\full_recording.mp4
```

These long machine-specific prefixes add noise and make output non-portable across machines. The goal is to rewrite any path **under the repo root** to a repo-root-relative form prefixed with `.` + separator, in **all output modes** (`--llm`, `--ci`, `--web`, and plain console):

```
[Client] 22:14:07.709 client-2 [Recording] client-2: retrieving to .\TestResults\runs\2026-05-28T22-00-23Z_f0e43e8\containers\client-2\full_recording.mp4
```

Paths **not** under the repo root (e.g. `C:\Windows\...`, in-container `/home/steam/...`) are left unchanged.

## Key findings from exploration

The harness has **two processes**: the `TestRunner` parent (net10.0) spawns the xUnit test-host **child** (`JunimoServer.Tests`, net10.0) via xUnit's out-of-process launcher. The child's stdout/stderr are inherited directly by the terminal.

There are **two distinct path surfaces**, and they need _opposite_ treatment:

**Surface A — free-text human-facing log lines (the target).** Split into two sub-surfaces by which process emits them:

- **A1 — child-process `TestLog` lines.** `[Server]/[Client]/[Test]` lines are emitted by `TestLog.Server/Client/Test` (`tests/JunimoServer.Tests/Infrastructure/TestLog.cs:9-16`), which write directly to `Console.Error`. They bypass the renderer entirely in every mode. `TestLog` is the choke point for these: every `[Recording]` container line funnels through it via `_logCallback` (e.g. `ClientPool.cs:459` / `TestResourceBroker.cs:1085` wire `msg => TestLog.Server/Client(...)`), and `ContainerRecorder._log` (`ContainerRecorder.cs:46,241`) is wired to the same callback (verified — its `[Recording]` lines are covered). The user's example originates at `GameClientContainer.cs:565` → `TestLog.Client`.

- **A2 — parent-process direct `Console.Write*` lines (found in adversarial review).** The parent `TestRunner` writes several `[WebUI]`/`[ImageTransfer]`/`[GameData]` lines that interpolate an **absolute path variable** directly to `Console.Error`/`Console.Out`, bypassing `TestLog` (which lives in the child's `Tests` assembly). These print to the same terminal in `--web`/`--report` (and `--ci` for the screenshot line) and were missed by a TestLog-only fix. Path-bearing sites (verified by reading each):
    - `Rendering/Web/ReportGenerator.cs:68` — `Static report generated: {reportPath}`
    - `Rendering/Web/ReportGenerator.cs:97` — `Screenshot not found: {screenshotPath}`
    - `Rendering/Web/ReportGenerator.cs:103` — `Large screenshot (...): {resolvedPath}`
    - `Rendering/Web/ReportGenerator.cs:151` — `Artifact not found: {originalPath}`
    - `Rendering/Web/ReportGenerator.cs:179` — `Exported N artifacts to {mockArtifactsDir}`
    - `Rendering/WebRenderer.cs:515` — `Mock data written to {mockPath}`
    - `Rendering/CIRenderer.cs:122` — `   Screenshot: {f.ScreenshotPath}` (written to stdout `_out`, shown on a failing test in `--ci`/default mode)

    The remaining ~25 parent `Console.Error.WriteLine` lines carry only `{ex.Message}` / IDs / URLs (no repo path) and are left untouched.

**Surface B — structured event path fields (must NOT be relativized).** `InstanceRecordingEvent.RecordingPath`, `RecordingCapturedEvent.RecordingPath`, and `ScreenshotCapturedEvent.ScreenshotPath` carry absolute paths over IPC to the web UI. These are **load-bearing** — the UI builds artifact URLs as `` `/artifacts/${path}` `` (`tests/test-ui/src/composables/useScreenshotCache.ts:26,46`), and the runner's `/artifacts/{**path}` handler resolves `Path.GetFullPath(Path.Combine(Path.GetFullPath("TestResults"), path))` (`tests/JunimoServer.TestRunner/Rendering/WebRenderer.cs:120-128`). Today the absolute field works because `Path.Combine(base, rootedSecondArg)` returns the rooted arg unchanged. If rewritten to repo-relative `.\TestResults\...`, `Path.Combine` would produce `…/TestResults/TestResults/...` → 404, breaking UI videos/screenshots and the static report (`ReportGenerator.cs:280-294`). These fields are never shown to humans verbatim (UI uses `recordingPath` only as a boolean signal). **Leave them absolute.**

Decisions confirmed with the user: **Surface A only** (both A1 and A2); `infrastructure.jsonl` (machine diagnostic log, consumed by `make test-events`) **stays absolute**; cover the parent A2 sites with a **targeted scrub** at the path-bearing lines (not a parent-wide log-helper migration).

**Why not a single blanket `Console` decorator** (considered and rejected): wrapping `Console.Error`/`Console.Out` in a scrubbing `TextWriter` once at startup would be the broadest choke point, but it cannot distinguish human log lines from structured output on the _same_ stream — `LLMRenderer` writes JSONL to `Console.Out` (`LLMRenderer.cs:20,256`) and `CIRenderer` writes rendered results to `Console.Out` (`CIRenderer.cs:41,69`). A stdout decorator would scrub the LLM JSONL (whose surface-B path fields we deliberately keep absolute) and risk corrupting structured JSON; a stderr-only decorator would miss the CIRenderer stdout line. Targeted scrubbing at the known human-facing sites is the correct, lower-risk shape.

### Existing utilities reused

- `tests/JunimoServer.Tests/Helpers/ProjectRoot.cs` — `ProjectRoot.Path` resolves the repo root by walking up from `AppContext.BaseDirectory` to the `Directory.Build.props` marker (verified present at repo root, exactly one file). Lazy-cached; works in both processes. Authoritative root source.
- `RendererBase.CleanProjectPath` (`tests/JunimoServer.TestRunner/Rendering/RendererBase.cs:316-330`) — existing stack-trace-specific path cleaner with a _different_ output contract (forward-slash normalization, no `.\` prefix). **Left unchanged** — refactoring it to delegate would change stack-trace formatting for no requested benefit and couple unrelated concerns (`simplest-solution.md`, `no-refactor-history-in-code.md`).
- TS `relativeRunPath` (`useTestStore.ts:98-103`) — strips `runDir` to the `runs/` segment for `/artifacts/` URLs; never displays a path verbatim. **No change.**

## Implementation

### 1. New shared utility — `tests/JunimoServer.Tests/Helpers/PathDisplay.cs`

`public static class PathDisplay` in `JunimoServer.Tests.Helpers`. This assembly is referenced by both the child test code and the parent `TestRunner` (`Program.cs` already does `using JunimoServer.Tests.Helpers;`), so a single utility serves both. Reuses `ProjectRoot.Path` rather than adding new root detection.

```csharp
public static string ScrubMessage(string message)
```

Algorithm (single native-separator prefix-replace — the simplest correct transform for free text containing a path):

1. null/empty → return unchanged.
2. Resolve the repo-root prefix **once**, lazily cached in a static field: `_prefix = ProjectRoot.Path + Path.DirectorySeparatorChar` (e.g. `D:\…\server\` on Windows, `/…/server/` on POSIX). Wrap the resolution in try/catch → on failure cache `null` so a root-resolution failure can never throw on a logging hot path (defensive: `ProjectRoot.Path` throws if the `Directory.Build.props` marker is absent).
3. If `_prefix` is null → return unchanged.
4. `message.Replace(_prefix, "." + Path.DirectorySeparatorChar, comparison)` where `comparison` is `OrdinalIgnoreCase` on Windows (drive-letter/case drift), `Ordinal` on POSIX. Yields `.\` on Windows / `./` on POSIX.
5. Absent prefix → no-op (covers non-repo paths and idempotency: a `.\TestResults\…` re-fed in has no absolute prefix to strip).

**Separator correctness (validated):** the only repo-root paths that appear in messages are built by `Path.Combine` via `TestArtifacts` (`GetContainerDir`/`RunDir`), which always uses the **native** separator. So matching the single native-separator prefix is sufficient — there is no forward-slash repo-root form to catch on Windows (in-container paths like `/recordings/...` are not repo-root paths). An `AltDirectorySeparatorChar` branch would be dead code (`simplest-solution.md`), so it is omitted.

**Root coincidence (validated):** the recording `destPath` is rooted at `TestArtifacts.RepoRoot` = `Path.GetFullPath(Combine(AppContext.BaseDirectory, "..×5"))`, while the scrub strips `ProjectRoot.Path` = the `Directory.Build.props`-marker dir. Climbing 5 levels from `…\tests\JunimoServer.Tests\bin\Debug\net10.0` lands on `…\server`, which is exactly where the marker lives — both resolve to the identical `…\server` string. (If they ever diverge, the scrub silently no-ops rather than misbehaving — fail-safe.) `ProjectRoot.Path` is chosen as the anchor because it is the public, purpose-built "repo root" accessor; `TestArtifacts.RepoRoot` is private.

> A whole-message prefix-replace (not per-token path parsing) is correct because the input is free text — it rewrites `…\server\TestResults\…` → `.\TestResults\…` wherever the prefix appears and is otherwise inert. This mirrors the established `ChatRedaction.MaskSecrets(string)` static-scrub precedent (`mod/JunimoServer.Shared/ChatRedaction.cs`).

### 2. Apply the scrub at `TestLog` — `tests/JunimoServer.Tests/Infrastructure/TestLog.cs`

Add `using JunimoServer.Tests.Helpers;` and wrap the interpolated message in all three methods:

```csharp
internal static void Server(string message) =>
    Console.Error.WriteLine($"[Server] {DateTime.UtcNow:HH:mm:ss.fff} {PathDisplay.ScrubMessage(message)}");
internal static void Client(string message) =>
    Console.Error.WriteLine($"[Client] {DateTime.UtcNow:HH:mm:ss.fff} {PathDisplay.ScrubMessage(message)}");
internal static void Test(string message) =>
    Console.Error.WriteLine($"[Test]   {DateTime.UtcNow:HH:mm:ss.fff} {PathDisplay.ScrubMessage(message)}");
```

This one edit point covers the example and every current/future free-text path leak in `[Server]/[Client]/[Test]` lines (A1), across all modes.

### 3. Scrub the parent-process path-bearing sites (A2)

Wrap the interpolated path variable in `PathDisplay.ScrubMessage(...)` at the seven sites listed under Surface A2 above. `PathDisplay` is in `JunimoServer.Tests.Helpers`, already referenced by the runner. Each edit is a one-token wrap, e.g.:

```csharp
// ReportGenerator.cs:68
Console.Error.WriteLine($"[WebUI] Static report generated: {PathDisplay.ScrubMessage(reportPath)}");
// CIRenderer.cs:122
_out.WriteLine($"   Screenshot: {PathDisplay.ScrubMessage(f.ScreenshotPath)}");
```

Add `using JunimoServer.Tests.Helpers;` to `ReportGenerator.cs`, `WebRenderer.cs`, and `CIRenderer.cs` as needed (some may already have it).

> `ScrubMessage` is used here (not a single-path helper) for uniformity with A1 and because each message is free text containing one path; it is a no-op when the value isn't under the repo root, so wrapping a value that's occasionally non-repo (e.g. an out-of-tree screenshot) is safe.

## Explicitly NOT changed (with reasons)

- **Surface-B event fields** (`SetupEventBus.EmitInstanceRecording/EmitRecording/EmitScreenshot`, the `destPath` emit at `GameClientContainer.cs:576` / `ServerContainer.cs:787`, `TestRunState.Apply*`) — relativizing breaks `/artifacts/` serving (see Context). The adjacent free-text `[Recording]` lines are already covered by the `TestLog` scrub.
- **`infrastructure.jsonl`** path fields (`ContainerRecorder` `recording_full_retrieved` etc.) — machine diagnostic log; user opted to keep absolute.
- **`RendererBase.CleanProjectPath`** and **TS `relativeRunPath`** — unrelated contracts; no churn.

## Files

- `tests/JunimoServer.Tests/Helpers/PathDisplay.cs` — **new** (the `ScrubMessage` utility)
- `tests/JunimoServer.Tests/Infrastructure/TestLog.cs` — A1: wrap messages in `PathDisplay.ScrubMessage`; add `using`
- `tests/JunimoServer.TestRunner/Rendering/Web/ReportGenerator.cs` — A2: wrap paths at lines 68, 97, 103, 151, 179
- `tests/JunimoServer.TestRunner/Rendering/WebRenderer.cs` — A2: wrap path at line 515
- `tests/JunimoServer.TestRunner/Rendering/CIRenderer.cs` — A2: wrap path at line 122
- `tests/JunimoServer.Tests/Helpers/ProjectRoot.cs` — reused (root source), not modified

Net change: 1 new file + 3 one-line wraps in `TestLog` + 7 one-token wraps across 3 runner files + `using`s. No TS change, so no `make build-test-ui` needed.

## Verification

Build: `dotnet build tests/JunimoServer.TestRunner/JunimoServer.TestRunner.csproj` (compiles both projects).

Run a recording test in each mode (the "retrieving to" line fires at container disposal). Use a small recording-enabled test as `FILTER`.

- **Plain console / `--ci`** (`make test FILTER=<RecordingTest>`): (A1) terminal `[Recording] ... retrieving to` line shows `.\TestResults\runs\...` (Windows) / `./TestResults/runs/...` (POSIX) — no `D:\...\server\TestResults` prefix. (A2) on a **failing** test, the CIRenderer `   Screenshot: ...` line (stdout) is also scrubbed. This is a **runtime gate** per `runtime-post-conditions-are-gates.md`: confirm by reading actual terminal output, not the diff.
- **`--llm`** (`make test-llm FILTER=<RecordingTest>`): same scrubbed `[Server]/[Client]/[Test]` lines on stderr. Confirm the stdout JSONL `recordingPath`/`screenshotPath` fields remain **absolute** (intended — surface B; proves the scrub did not touch structured stdout).
- **`--web` / `--web --report`** (`make test-web FILTER=<RecordingTest>` and `make test-web-report ...`): (1) A1 stderr lines scrubbed; (2) A2 — the `[WebUI] Static report generated: ...`, `Mock data written to ...`, and any `Screenshot/Artifact not found: ...` lines show `.\TestResults\...`; (3) open a finished container in the UI inspector and confirm the **video still plays** and screenshots load (proves surface-B field intact and `/artifacts/` still resolves); (4) confirm `TestResults/report/index.html` still links/inlines artifacts.
- **Non-repo / idempotency**: a non-repo path in a log line (e.g. an in-container `/home/steam/...` path) passes through unchanged; feeding a `.\TestResults\...` string back through `ScrubMessage` is a no-op.

Acceptance: every mode's terminal shows `.\TestResults\...` / `./TestResults/...` for repo-root paths in human-facing lines (A1 child + A2 parent); web UI videos/screenshots and the static report still load; `infrastructure.jsonl` and stdout JSONL event fields untouched.
