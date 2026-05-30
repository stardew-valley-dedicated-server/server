---
paths:
  - "tests/JunimoServer.Tests/Fixtures/**"
  - "tests/JunimoServer.TestRunner/Rendering/**"
---

# summary.json / ctrf-report.json are written by the runner, not the fixture

The durable run artifacts (`summary.json`, `ctrf-report.json`, `latest.txt`) are written by `TestRunArtifactWriter` in the parent TestRunner process, fed by `RunArtifactView` materialized from `TestRunState.GetArtifactView`. `TestSummaryFixture` runs in the test-host child process and owns per-test outcome state plus `flakiness.jsonl` — it does NOT write the run artifacts.

**Why:** Editing `TestSummaryFixture` looks plausible because it still hosts helpers like `ClassifyFailure` / `ExtractMethodFromTestName` and region markers (`#region summary.json` / `#region ctrf-report.json`) — but those helpers feed the live `test_enrichment` IPC path, not artifact writing, and edits to the fixture's region bodies are silent no-ops for the durable artifacts.

**How to apply:** When changing what appears in `summary.json` or `ctrf-report.json`, edit `tests/JunimoServer.TestRunner/Rendering/Web/TestRunArtifactWriter.cs` and any required projections in `RunArtifactView` / `TestRunState.GetArtifactView`. If the data isn't on `RunArtifactView` yet, you may need to thread it through the IPC pipeline (see `runner-ui-pipeline-plumbing.md`). Editing `TestSummaryFixture` for these artifacts will be a no-op.
