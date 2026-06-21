/**
 * Single source of truth for the inline definitions surfaced as native `title=`
 * tooltips on status badges, filter pills, and annotation-source chips. Keyed by
 * the status/source slug so every surface (StatusBar, TestTree, OutputPanel,
 * OverviewPanel) shows the same wording. A term that needs a glossary page is a
 * term that failed — so the definition lives on the label, not in prose.
 */
export const TERM_HELP: Record<string, string> = {
    // ── Test outcomes ──
    passed: "The test ran and all assertions held.",
    failed: "The test ran and an assertion or unexpected exception failed it.",
    canceled:
        "The test was cut short, not failed — the run was stopping (stop-on-fail cascade or operator stop), so its result is unknown rather than bad.",
    skipped: "The test was intentionally not run (e.g. a [Fact(Skip=…)] or an unmet condition).",
    notDispatched:
        "Discovered but never started — the run ended (aborted or stopped) before the scheduler reached it. Derived as expected − (passed + failed + canceled + skipped).",
    aborted: "The run was torn down mid-flight; this test never reached a final result.",
    queued: "Scheduled and waiting for a free test server/client before it can start.",
    running: "Currently executing.",
    pending: "Discovered, not yet queued.",

    // ── Annotation sources (per-line origin in the output log) ──
    body: "Log line written from inside the test method body.",
    broker: "Log line from the resource broker that pools and assigns the test's server/client containers.",
    recording: "Log line from the screen-recording pipeline (capture, extraction, encoding).",
    mod: "Log line relayed from the server/client SMAPI mod inside the container.",
    setup: "Log line from a setup phase (image build, container start, prestart) that ran before the test.",
};
