## Problem

When a run stops after a failure, the `run_finished` event can report the wrong number of failed and canceled tests.

Some tests that are canceled because of StopOnFail are first counted as failed when the error happens. They are later correctly classified as canceled, but the original failed count is not corrected.

This means `run_finished` can disagree with `summary.json`, even though `summary.json` has the correct final test statuses.

## What to change

Use the final per-test statuses as the single source of truth for the `run_finished` counts.

Do not maintain a separate set of counters that can get out of sync with the final classifications.

The final counts for passed, failed, canceled, and skipped should match the counts in `summary.json`.

## Testing

Add or update a test covering a StopOnFail run with tests that are still queued when the run is aborted.

Verify that:

* `run_finished` reports the same counts as the final per-test statuses.
* Tests canceled because of StopOnFail are counted as canceled, not failed.
* The four counts add up to the expected test count.
* The existing behavior for normal completed runs is unchanged.

The test should reproduce the original failure mode without depending on a specific deleted run.
