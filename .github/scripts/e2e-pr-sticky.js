// Shared helpers for the E2E PR sticky comment + maintainer authorization, used by
// the `gate` and `e2e` jobs in .github/workflows/e2e-tests.yml via actions/github-script.
//
// The sticky is ONE comment per PR, found by the hidden HTML marker `<!-- e2e-results -->`.
// Hidden markers also carry the run filter and the run-history JSON so a later event
// (a checkbox click) can recover them — the comment is the single source of truth, so the
// workflow holds no per-PR state of its own.
//
// Loop-safety: the bot edits this comment with GITHUB_TOKEN, and GITHUB_TOKEN-authored
// comment events do not retrigger workflows. The gate also ignores Bot authors and only
// reacts to an unchecked->checked transition of the re-run box.

const MARKER = "<!-- e2e-results -->";
const FILTER_PREFIX = "<!-- filter:";
const HISTORY_PREFIX = "<!-- run-history:";
// The exact label text of the re-run task-list item. The "(requested)" suffix is added
// transiently while a run is queued/in-flight, then removed when fresh results post.
const RERUN_LABEL = "🔁 Re-run E2E tests";
const RERUN_REQUESTED_LABEL = `${RERUN_LABEL} (requested)`;
// Cap the retained/rendered run history. The whole sticky (incl. the embedded
// run-history JSON marker) must stay under GitHub's 65536-char comment limit, or
// upsertSticky throws and results silently stop posting. ~40 rows is well within
// budget even with long filters/URLs; older rows are summarized as a count.
const MAX_HISTORY_ROWS = 40;

// --- marker (de)serialization -------------------------------------------------------

/**
 * Read the run filter the sticky recorded in its hidden `<!-- filter: … -->` marker.
 * @param {string} body - The sticky comment body.
 * @returns {string} The filter (empty string = full suite, or no marker present).
 */
function readFilter(body) {
    const m = body && body.match(/<!-- filter:([\s\S]*?)-->/);
    return m ? m[1].trim() : "";
}

/**
 * Read the retained run-history array from the sticky's `<!-- run-history: … -->` marker.
 * Corrupt or absent history yields `[]` (it must never break a run).
 * @param {string} body - The sticky comment body.
 * @returns {Array<object>} The history rows (newest appended last).
 */
function readHistory(body) {
    const m = body && body.match(/<!-- run-history:([\s\S]*?)-->/);
    if (!m) return [];
    try {
        const parsed = JSON.parse(m[1].trim());
        return Array.isArray(parsed) ? parsed : [];
    } catch {
        return []; // corrupt/absent history must never break a run — start fresh
    }
}

/**
 * Whether the re-run task-list checkbox is currently ticked (`- [x] 🔁 Re-run E2E tests`).
 * @param {string} body - The comment body to inspect.
 * @returns {boolean}
 */
function isReRunChecked(body) {
    if (!body) return false;
    return new RegExp(`^\\s*-\\s*\\[[xX]\\]\\s*${escapeRe(RERUN_LABEL)}`, "m").test(body);
}

/**
 * Reset a ticked re-run box back to unchecked, leaving the rest of the body untouched.
 * Used to "disarm" the sticky after a click we won't act on (e.g. a non-maintainer
 * ticked it) so it doesn't look stuck mid-request.
 * @param {string} body - The sticky comment body.
 * @returns {string} The body with the re-run box set to `- [ ]`.
 */
function resetReRunCheckbox(body) {
    if (!body) return body;
    return body.replace(new RegExp(`^(\\s*-\\s*\\[)[xX](\\]\\s*${escapeRe(RERUN_LABEL)})`, "m"), "$1 $2");
}

/** Escape a string for safe interpolation into a `RegExp`. */
function escapeRe(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Shorten a commit SHA for display (the 7-char form used in headlines + history rows). */
function shortSha(sha) {
    return (sha || "").slice(0, 7);
}

// --- result derivation ---------------------------------------------------------------

/** The `skipped` clause shared by the pass/fail headlines (empty when none were skipped). */
function skippedClause(summary) {
    return summary.skipped ? `, ${summary.skipped} skipped` : "";
}

/**
 * Derive the final run result (status, emoji, headline) from the job status and the
 * runner's `summary.json`. Pure: no I/O — the caller reads `summary.json` and `job.status`
 * and passes them in. Precedence: a cancelled job (a maintainer preempted the run) wins
 * over any partial summary; then failures; then a clean pass; then a missing summary
 * (an early failure before the runner wrote one).
 *
 * Each branch yields `{ status, emoji, resultWord, headline }`. The status and result
 * emoji are always the same glyph, so callers get it aliased as both `statusEmoji` and
 * `resultEmoji` from the single `emoji` field below.
 * @param {object} args
 * @param {{ passed?: number, failed?: number, skipped?: number, aborted?: boolean }|null} args.summary
 *   The parsed `summary.json`, or null if none was written.
 * @param {string} args.jobStatus - GitHub's `job.status`: 'success' | 'failure' | 'cancelled'.
 * @param {string} args.sha - The (already-shortened) head SHA to name in the headline.
 * @param {string} [args.reportUrl] - Hosted report URL; when present, appended to the headline.
 * @returns {{ status: string, statusEmoji: string, resultEmoji: string, resultWord: string, headline: string }}
 */
function deriveResult({ summary, jobStatus, sha, reportUrl }) {
    let outcome;
    if (jobStatus === "cancelled" || (summary && summary.aborted)) {
        outcome = {
            status: "aborted",
            emoji: "⚪",
            resultWord: "aborted",
            headline: `Aborted (run cancelled) for \`${sha}\`.`,
        };
    } else if (summary && summary.failed > 0) {
        outcome = {
            status: "failed",
            emoji: "🔴",
            resultWord: "failed",
            headline: `**${summary.failed} failed**, ${summary.passed} passed${skippedClause(summary)} for \`${sha}\`.`,
        };
    } else if (summary) {
        outcome = {
            status: "passed",
            emoji: "🟢",
            resultWord: "passed",
            headline: `**${summary.passed} passed**${skippedClause(summary)} for \`${sha}\`.`,
        };
    } else {
        // No summary.json (early failure before the runner wrote it).
        outcome = {
            status: "failed",
            emoji: "🔴",
            resultWord: "no results",
            headline: `Run finished without a summary for \`${sha}\` (check the Actions log).`,
        };
    }

    const headline = reportUrl
        ? `${outcome.headline}\n\n**[▶ Open interactive report](${reportUrl})** (screenshots + videos)`
        : outcome.headline;

    return {
        status: outcome.status,
        statusEmoji: outcome.emoji,
        resultEmoji: outcome.emoji,
        resultWord: outcome.resultWord,
        headline,
    };
}

// --- body rendering -----------------------------------------------------------------

/**
 * Render the full sticky comment body: hidden markers (filter + run-history) for the
 * next event to recover, a status block, the re-run checkbox, and the collapsed
 * run-history table.
 * @param {object} state
 * @param {'queued'|'passed'|'failed'|'aborted'} state.status - Run status (drives wording).
 * @param {string} state.statusEmoji - Emoji shown in the heading.
 * @param {string} state.headline - Markdown headline line under the heading.
 * @param {string} state.filter - The run filter (empty = full suite).
 * @param {Array<object>} state.history - Run-history rows to render + re-embed.
 * @param {boolean} state.requested - When true, label the box "… (requested)".
 * @returns {string} The complete comment body.
 */
function renderBody(state) {
    const filter = state.filter || "";
    const filterLabel = filter ? `\`${filter}\`` : "_full suite_";
    const checkboxLabel = state.requested ? RERUN_REQUESTED_LABEL : RERUN_LABEL;

    // Keep only the most recent rows so the comment stays under GitHub's 65536-char
    // limit. Derive how many older runs are hidden from the run-number gap (the oldest
    // retained row's n), which stays accurate even though the marker only round-trips
    // the retained slice — array length alone would undercount across renders.
    const allHistory = state.history || [];
    const history = allHistory.slice(-MAX_HISTORY_ROWS);
    const oldestN = history.length ? history[0].n || history.length : 0;
    const droppedCount = Math.max(0, oldestN - 1);

    const lines = [
        MARKER,
        `${FILTER_PREFIX} ${filter} -->`,
        // Embed only the retained rows — the marker must round-trip what we render so the
        // next event reads back a bounded history (prevents unbounded marker growth too).
        `${HISTORY_PREFIX} ${JSON.stringify(history)} -->`,
        "",
        `### ${state.statusEmoji} E2E Tests`,
        "",
        state.headline,
        "",
        `**Filter:** ${filterLabel}`,
        "",
        `- [ ] ${checkboxLabel}`,
        "",
        "<sub>Maintainers: tick the box to re-run with the same filter, or comment " +
            "`/run-tests-e2e [filter]`.</sub>",
    ];

    if (history.length) {
        const summaryLabel =
            droppedCount > 0 ? `Run history (latest ${history.length}; ${droppedCount} older hidden)` : "Run history";
        lines.push("", `<details><summary>${summaryLabel}</summary>`, "");
        lines.push("| # | Result | SHA | Filter | When | Links |");
        lines.push("|---|--------|-----|--------|------|-------|");
        // Newest first.
        for (const h of [...history].reverse()) {
            const links = [h.runUrl ? `[run](${h.runUrl})` : "", h.reportUrl ? `[report](${h.reportUrl})` : ""]
                .filter(Boolean)
                .join(" · ");
            lines.push(
                `| ${h.n} | ${h.resultEmoji} ${h.result} | \`${h.sha}\` | ${h.filter ? `\`${h.filter}\`` : "full"} | ${h.when} | ${links} |`,
            );
        }
        lines.push("", "</details>");
    }

    return lines.join("\n");
}

// --- comment upsert -----------------------------------------------------------------

/**
 * Find the one E2E sticky comment on the PR by its hidden `MARKER` (paginates the thread).
 * @returns {Promise<object|null>} The comment object, or null if none exists yet.
 */
async function findSticky({ github, owner, repo, issue_number }) {
    // Paginate so the sticky is found even on a long PR thread.
    const comments = await github.paginate(github.rest.issues.listComments, {
        owner,
        repo,
        issue_number,
        per_page: 100,
    });
    return comments.find((c) => (c.body || "").includes(MARKER)) || null;
}

/**
 * Create-or-update the single E2E sticky comment with `body` (one comment per PR).
 *
 * Pass `existing` (the comment from a prior `findSticky`) when the caller already
 * located it to read its history — this makes the whole read-modify-write target ONE
 * comment id and avoids a second `listComments` fetch that could resolve a different
 * comment (lost-update race). Omit `existing` only when there was nothing to read first.
 * @param {object|null} [args.existing] - The sticky comment from a prior findSticky, or null.
 * @returns {Promise<number>} The comment id written.
 */
async function upsertSticky({ github, owner, repo, issue_number, body, existing }) {
    // `existing === undefined` → caller didn't look; fetch. `existing === null` → caller
    // looked and there's none → create. A truthy `existing` → update that exact comment.
    const target = existing === undefined ? await findSticky({ github, owner, repo, issue_number }) : existing;
    if (target) {
        await github.rest.issues.updateComment({ owner, repo, comment_id: target.id, body });
        return target.id;
    }
    const created = await github.rest.issues.createComment({ owner, repo, issue_number, body });
    return created.data.id;
}

// --- command parsing (trigger interpretation) ---------------------------------------

// Allowed characters in a run filter. An xUnit `--filter` is a fully-qualified
// class/method substring, so word chars, `.`, namespace/generic punctuation, spaces and
// parens are legitimate; everything else is rejected. This is also the security choke
// point that keeps a filter from breaking the hidden comment markers (a `-->` would
// truncate the run-history JSON and silently wipe history) or the markdown table (a `|`
// would split a cell). Keep this conservative — widen only with a matching test.
const FILTER_ALLOWED = /^[A-Za-z0-9_.+=() -]*$/;

/**
 * Parse a `/run-tests-e2e [filter]` PR comment into a command decision. Anchored to the
 * FIRST line so a quoted or mid-text mention (e.g. in a paragraph) is ignored. The filter
 * is normalized (collapsed whitespace) and validated against {@link FILTER_ALLOWED}.
 * @param {string} body - The raw comment body.
 * @returns {{ isCommand: boolean, filter: string, valid: boolean }}
 *   `isCommand` false → not our command (ignore). `valid` false → it was our command but
 *   the filter has disallowed characters (the caller should reject, not run).
 */
function parseCommand(body) {
    // Split on CRLF or LF and strip any trailing \r — GitHub comment bodies often arrive
    // with \r\n, and JS `.` does not match \r, so a bare `\n` split would leave the regex
    // unable to match `/run-tests-e2e Foo\r` (silently ignoring the command).
    const firstLine = (body || "").split(/\r?\n/, 1)[0].replace(/\r$/, "");
    const m = firstLine.match(/^\/run-tests-e2e(?:\s+(.*))?$/);
    if (!m) return { isCommand: false, filter: "", valid: false };
    const filter = (m[1] || "").trim().split(/\s+/).filter(Boolean).join(" ");
    return { isCommand: true, filter, valid: isValidFilter(filter) };
}

/** Whether a run filter is safe to embed in markers / the table and pass to xUnit. */
function isValidFilter(filter) {
    return FILTER_ALLOWED.test(filter || "");
}

/**
 * Decide whether an `issue_comment: edited` event is a genuine re-run tick: the box went
 * from unchecked to checked. A real box-tick always delivers the prior body in
 * `changes.body.from`; when that's absent we cannot prove a transition occurred, so we
 * fail SAFE (skip) rather than treat "no prior body" as unchecked — otherwise any text
 * edit while the box sits ticked would re-fire a run.
 * @param {object} args
 * @param {string|null|undefined} args.priorBody - `changes.body.from` from the edit event.
 * @param {string} args.body - The current comment body.
 * @returns {boolean} True only on a confirmed unchecked->checked transition.
 */
function shouldFireRerun({ priorBody, body }) {
    if (priorBody == null) return false;
    return isReRunChecked(body) && !isReRunChecked(priorBody);
}

// --- maintainer authorization (authoritative; author_association is NOT used) -------

/**
 * Whether `username` has write or admin permission on the repo (the authoritative
 * maintainer check; `author_association` is deliberately not used).
 *
 * The API collapses maintain->write and triage->read, so write|admin == "can push".
 * A non-collaborator makes getCollaboratorPermissionLevel throw 404 — treated as a DENY,
 * not an error. The endpoint needs only repo metadata read, which every GITHUB_TOKEN has
 * (the gate job also grants contents: read). A too-narrow token would throw 403, which we
 * rethrow so the gate FAILS CLOSED (red job, no run) — the safe direction for an auth check.
 * @returns {Promise<boolean>}
 */
async function isMaintainer({ github, owner, repo, username }) {
    try {
        const { data } = await github.rest.repos.getCollaboratorPermissionLevel({ owner, repo, username });
        return data.permission === "admin" || data.permission === "write";
    } catch (err) {
        if (err.status === 404) return false;
        throw err; // 403/5xx/rate-limit: fail loudly (closed), never silently allow
    }
}

module.exports = {
    MARKER,
    RERUN_LABEL,
    RERUN_REQUESTED_LABEL,
    readFilter,
    readHistory,
    shortSha,
    isReRunChecked,
    resetReRunCheckbox,
    deriveResult,
    renderBody,
    findSticky,
    upsertSticky,
    isMaintainer,
    parseCommand,
    isValidFilter,
    shouldFireRerun,
};
