---
paths:
  - "tests/JunimoServer.TestRunner/**"
  - "tests/JunimoServer.Tests/Fixtures/**"
---

# When two code paths produce the same artifact, merge upstream state, not downstream files

If you find yourself writing a second producer for an artifact (a JSON file, a report, a summary) that an existing writer already produces, stop. The right move is to feed the second mode's data into the existing writer's *upstream* in-memory state model and let the one writer serialize it. Re-implementing the schema in a new producer guarantees silent drift the first time someone adds a field to one path and not the other.

**Why:** Surfaced during the distributed-runner work. `TestRunArtifactWriter` writes `summary.json` and `ctrf-report.json` from `RunArtifactView` (a projection of `TestRunState`) in local mode. The distributed `ResultAggregator` independently parsed each worker's `summary.json` from disk and re-serialized a merged version with its own schema code. Any new field added to `TestRunArtifactWriter.WriteSummaryJson` would have been silently dropped at the merge — same drift mode as the runner→UI plumbing rule, but one layer up: about *artifact producers*, not field plumbing. The structural fix is to merge worker state into the coordinator's `TestRunState` and run the same `TestRunArtifactWriter` over it; the aggregator's job shrinks to file-level merges (copy per-test artifact dirs, concat append-only logs sorted by `run_ms`) and provides coordinator-only diagnostic inputs (lost workers, dropped events) to the same writer.

**How to apply:** Before writing a producer for an output file, grep for any existing writer of that file. If one exists, the new path must reach it via shared upstream state — never by parsing the existing writer's output back from disk and re-serializing. Aggregators / mergers should own *file-level* concatenation only (copying directories, appending logs); schema-level aggregation belongs on the in-memory model that the single writer serializes. If the writer can't be reached because of an architectural split, surface that as the design problem to fix, not as license to fork the schema.
