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

const MARKER = '<!-- e2e-results -->';
const FILTER_PREFIX = '<!-- filter:';
const HISTORY_PREFIX = '<!-- run-history:';
// The exact label text of the re-run task-list item. The "(requested)" suffix is added
// transiently while a run is queued/in-flight, then removed when fresh results post.
const RERUN_LABEL = '🔁 Re-run E2E tests';
const RERUN_REQUESTED_LABEL = `${RERUN_LABEL} (requested)`;

// --- marker (de)serialization -------------------------------------------------------

/**
 * Read the run filter the sticky recorded in its hidden `<!-- filter: … -->` marker.
 * @param {string} body - The sticky comment body.
 * @returns {string} The filter (empty string = full suite, or no marker present).
 */
function readFilter(body) {
  const m = body && body.match(/<!-- filter:([\s\S]*?)-->/);
  return m ? m[1].trim() : '';
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
  return new RegExp(`^\\s*-\\s*\\[[xX]\\]\\s*${escapeRe(RERUN_LABEL)}`, 'm').test(body);
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
  return body.replace(
    new RegExp(`^(\\s*-\\s*\\[)[xX](\\]\\s*${escapeRe(RERUN_LABEL)})`, 'm'),
    '$1 $2',
  );
}

/** Escape a string for safe interpolation into a `RegExp`. */
function escapeRe(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
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
  const filter = state.filter || '';
  const filterLabel = filter ? `\`${filter}\`` : '_full suite_';
  const checkboxLabel = state.requested ? RERUN_REQUESTED_LABEL : RERUN_LABEL;

  const lines = [
    MARKER,
    `${FILTER_PREFIX} ${filter} -->`,
    `${HISTORY_PREFIX} ${JSON.stringify(state.history || [])} -->`,
    '',
    `### ${state.statusEmoji} E2E Tests`,
    '',
    state.headline,
    '',
    `**Filter:** ${filterLabel}`,
    '',
    `- [ ] ${checkboxLabel}`,
    '',
    '<sub>Maintainers: tick the box to re-run with the same filter, or comment ' +
      '`/run-tests-e2e [filter] [--abort-current]`.</sub>',
  ];

  const history = state.history || [];
  if (history.length) {
    lines.push('', '<details><summary>Run history</summary>', '');
    lines.push('| # | Result | SHA | Filter | When | Links |');
    lines.push('|---|--------|-----|--------|------|-------|');
    // Newest first.
    for (const h of [...history].reverse()) {
      const links = [
        h.runUrl ? `[run](${h.runUrl})` : '',
        h.reportUrl ? `[report](${h.reportUrl})` : '',
      ].filter(Boolean).join(' · ');
      lines.push(
        `| ${h.n} | ${h.resultEmoji} ${h.result} | \`${h.sha}\` | ${h.filter ? `\`${h.filter}\`` : 'full'} | ${h.when} | ${links} |`,
      );
    }
    lines.push('', '</details>');
  }

  return lines.join('\n');
}

// --- comment upsert -----------------------------------------------------------------

/**
 * Find the one E2E sticky comment on the PR by its hidden `MARKER` (paginates the thread).
 * @returns {Promise<object|null>} The comment object, or null if none exists yet.
 */
async function findSticky({ github, owner, repo, issue_number }) {
  // Paginate so the sticky is found even on a long PR thread.
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner, repo, issue_number, per_page: 100,
  });
  return comments.find((c) => (c.body || '').includes(MARKER)) || null;
}

/**
 * Create-or-update the single E2E sticky comment with `body` (one comment per PR).
 * @returns {Promise<number>} The comment id written.
 */
async function upsertSticky({ github, owner, repo, issue_number, body }) {
  const existing = await findSticky({ github, owner, repo, issue_number });
  if (existing) {
    await github.rest.issues.updateComment({ owner, repo, comment_id: existing.id, body });
    return existing.id;
  }
  const created = await github.rest.issues.createComment({ owner, repo, issue_number, body });
  return created.data.id;
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
    return data.permission === 'admin' || data.permission === 'write';
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
  isReRunChecked,
  resetReRunCheckbox,
  renderBody,
  findSticky,
  upsertSticky,
  isMaintainer,
};
