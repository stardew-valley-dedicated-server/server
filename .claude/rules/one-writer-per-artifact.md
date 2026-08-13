---
paths:
  - "tests/JunimoServer.TestRunner/**"
  - "tests/JunimoServer.Tests/Fixtures/**"
---

# When two code paths produce the same artifact, merge upstream state, not downstream files

If you find yourself writing a second producer for an artifact (a JSON file, a report, a summary) that an existing writer already produces, stop. The right move is to feed the second mode's data into the existing writer's *upstream* in-memory state model and let the one writer serialize it. Re-implementing the schema in a new producer guarantees silent drift the first time someone adds a field to one path and not the other.

**Why:** Surfaced during the distributed-runner work. `TestRunArtifactWriter` writes `summary.json` and `ctrf-report.json` from `RunArtifactView` (a projection of `TestRunState`). The distributed mode's first-cut aggregator instead parsed each worker's `summary.json` back from disk and re-serialized a merged version through its own schema code — any field later added to `TestRunArtifactWriter.WriteSummaryJson` would have been silently dropped at the merge. Same drift mode as the runner→UI plumbing rule, one layer up: *artifact producers*, not field plumbing. The structural fix: merge worker state into the coordinator's `TestRunState` and run the same `TestRunArtifactWriter` over it, leaving the aggregation step only file-level work (copying per-test artifact dirs, concatenating append-only logs).

**How to apply:** Before writing a producer for an output file, grep for any existing writer of that file. If one exists, the new path must reach it via shared upstream state — never by parsing the existing writer's output back from disk and re-serializing. Aggregators / mergers should own *file-level* concatenation only (copying directories, appending logs); schema-level aggregation belongs on the in-memory model that the single writer serializes. If the writer can't be reached because of an architectural split, surface that as the design problem to fix, not as license to fork the schema.
