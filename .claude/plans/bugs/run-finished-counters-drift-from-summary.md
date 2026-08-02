# run_finished counters disagree with summary.json's per-test tally

## Symptom

On a StopOnFail-aborted run, the `run_finished` event's counters lump cascade cancels into
`failed` and disagree with the authoritative per-test statuses. Run
2026-07-20T14-52-51Z_52d55e3 (local): `run_finished` reported
`passed:144, failed:15, canceled:12, skipped:6` (sum 177), while `run_summary`/`summary.json`
per-test statuses were `144 / 2 / 13 / 6` (sum 165 = expectedTestCount). At-a-glance triage of
the run tail then starts from a wrong failure count.

## Localization hint

PR #425 fixed cascade misclassification at the summary/classifier seams (`BuildAcquisitionFault`,
`ThrowIfServerError` → canceled), so the per-test statuses are right. The `run_finished` event
apparently aggregates its own live counters at a different seam — a test can seemingly bump both
`failed` (at first error) and `canceled` (at final classification), and the counter is never
reconciled against the final statuses. Two producers of one number = drift
(one-writer-per-artifact): derive `run_finished`'s counts from the same final per-test statuses
`summary.json` uses, or drop the independent counters and emit the reconciled tally.

## Repro

Any StopOnFail abort with queued tests (e.g. the newgame-504 race,
`.claude/plans/bugs/newgame-504-after-forced-reload.md`, aborts a full local run) — compare the
`run_finished` line against `summary.json`.
