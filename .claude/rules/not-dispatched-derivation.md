---
paths:
  - "tests/JunimoServer.TestRunner/Rendering/Web/**"
---

# `notDispatched` derivation must subtract Skipped too

When deriving never-dispatched count from `ExpectedTestCount` in the runner, subtract every status that has a record: `notDispatched = max(0, ExpectedTestCount - (Passed + Failed + Canceled + Skipped))`. Skipped tests are part of the discovered total — they're not "never dispatched."

**Why:** Caught a clean-run bug where `summary.json` reported `skipped: 6, notDispatched: 6` simultaneously. Root cause: runner-side `DiscoverTestsViaReflection` does NOT filter `[Fact(Skip=...)]` tests, so they go into `_totalTests` (= `ExpectedTestCount`). xUnit then fires `OnTestSkipped`, which records them with `Status = "skipped"`. A derivation that only subtracts `Passed + Failed + Canceled` then double-counts them — once in `Skipped`, once in `notDispatched`. CTRF's `summary.tests` total compounds the same drift if `Skipped` isn't added back into `totalTests`.

**How to apply:** Any "tests not accounted for" delta computed in the runner must list every status that produces a record. The current set is `Passed | Failed | Canceled | Skipped`. If a new outcome status is added (e.g. a `Pending` bucket), include it in the subtraction at every site. Same logic in CTRF: `summary.tests = Passed + Failed + Skipped + other`, where `other = Canceled + notDispatched`.
