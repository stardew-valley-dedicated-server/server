// Tests for e2e-pr-sticky.js — the pure helpers behind the E2E PR sticky comment.
// Run with `npm test` (or `node --test .github/scripts/`). No dependencies: uses Node's
// built-in test runner (node:test + node:assert), so it works anywhere Node 18+ is present.
//
// These cover the logic that is awkward to exercise through a real workflow run: marker
// round-tripping, the re-run checkbox state machine, the run-history cap (which guards
// GitHub's 65536-char comment limit), and the find-once upsert contract (no lost-update
// re-fetch). The GitHub API calls are exercised against a tiny in-memory fake.

const { test } = require('node:test');
const assert = require('node:assert/strict');
const helper = require('./e2e-pr-sticky.js');

// A minimal fake of the Octokit surface the helper touches, recording the calls made so
// tests can assert "found once, wrote to that exact id" without a network.
function fakeGithub(existingComment) {
  const calls = [];
  return {
    calls,
    paginate: async () => {
      calls.push('list');
      return existingComment ? [existingComment] : [];
    },
    rest: {
      issues: {
        listComments: {}, // only referenced by paginate's signature
        updateComment: async ({ comment_id }) => { calls.push(`update#${comment_id}`); },
        createComment: async () => { calls.push('create'); return { data: { id: 999 } }; },
      },
    },
  };
}

const sticky = (id, extraBody = '') => ({ id, body: `${helper.MARKER}\n${extraBody}` });

// --- marker (de)serialization ------------------------------------------------------

test('readFilter round-trips the filter marker', () => {
  const body = helper.renderBody({ status: 'queued', statusEmoji: '🟡', headline: 'x', filter: 'PasswordProtection', history: [], requested: false });
  assert.equal(helper.readFilter(body), 'PasswordProtection');
});

test('readFilter is empty when no filter / no marker', () => {
  const body = helper.renderBody({ status: 'queued', statusEmoji: '🟡', headline: 'x', filter: '', history: [], requested: false });
  assert.equal(helper.readFilter(body), '');
  assert.equal(helper.readFilter('no marker here'), '');
});

test('readHistory round-trips and tolerates corruption', () => {
  const rows = [{ n: 5, result: 'failed', resultEmoji: '🔴', sha: 'deadbee', filter: 'Bar', when: 't', runUrl: 'http://r/5', reportUrl: '' }];
  const body = helper.renderBody({ status: 'failed', statusEmoji: '🔴', headline: 'x', filter: 'Bar', history: rows, requested: false });
  assert.deepEqual(helper.readHistory(body), rows);
  assert.deepEqual(helper.readHistory('<!-- run-history: NOT JSON -->'), []);
  assert.deepEqual(helper.readHistory('no marker'), []);
});

test('shortSha takes the first 7 chars and is null-safe (empty/undefined → "")', () => {
  assert.equal(helper.shortSha('abcdef0123456'), 'abcdef0');
  assert.equal(helper.shortSha(''), '');
  assert.equal(helper.shortSha(undefined), '');
});

// --- re-run checkbox state machine -------------------------------------------------

test('isReRunChecked detects only the ticked box', () => {
  const idle = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: '', history: [], requested: false });
  assert.equal(helper.isReRunChecked(idle), false);
  const ticked = idle.replace('- [ ] 🔁 Re-run E2E tests', '- [x] 🔁 Re-run E2E tests');
  assert.equal(helper.isReRunChecked(ticked), true);
  assert.equal(helper.isReRunChecked(''), false);
});

test('isReRunChecked works on the "(requested)" variant', () => {
  const req = helper.renderBody({ status: 'queued', statusEmoji: '🟡', headline: 'x', filter: '', history: [], requested: true });
  assert.ok(req.includes('Re-run E2E tests (requested)'));
  const ticked = req.replace('- [ ] 🔁 Re-run E2E tests (requested)', '- [x] 🔁 Re-run E2E tests (requested)');
  assert.equal(helper.isReRunChecked(ticked), true);
});

test('resetReRunCheckbox unchecks while preserving markers, and is a no-op when unchecked', () => {
  const rows = [{ n: 1, result: 'passed', resultEmoji: '🟢', sha: 'abc', filter: 'Foo', when: 't', runUrl: 'r', reportUrl: '' }];
  const idle = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: 'Foo', history: rows, requested: false });
  const ticked = idle.replace('- [ ] 🔁 Re-run E2E tests', '- [x] 🔁 Re-run E2E tests');
  const reset = helper.resetReRunCheckbox(ticked);
  assert.equal(helper.isReRunChecked(reset), false);
  assert.equal(helper.readFilter(reset), 'Foo');
  assert.deepEqual(helper.readHistory(reset), rows);
  assert.equal(helper.resetReRunCheckbox(idle), idle); // already unchecked → unchanged
});

test('RERUN_REQUESTED_LABEL starts with RERUN_LABEL (the checkbox regexes match on the prefix)', () => {
  // isReRunChecked/resetReRunCheckbox match `RERUN_LABEL` as a prefix so they work on both
  // the plain and "(requested)" variants. If the requested label ever stops starting with
  // the base label, that prefix match breaks silently — pin the invariant here.
  assert.ok(helper.RERUN_REQUESTED_LABEL.startsWith(helper.RERUN_LABEL));
});

// --- run-history cap (guards GitHub's 65536-char comment limit) ---------------------

test('history is capped and stays well under the comment limit across many runs', () => {
  // Simulate 200 runs the way the workflow does: read back the (capped) marker, compute
  // the next run number, append, re-render.
  let body = helper.renderBody({ status: 'queued', statusEmoji: '🟡', headline: 'x', filter: 'SomeLongTestClassName', history: [], requested: false });
  for (let i = 1; i <= 200; i++) {
    const hist = helper.readHistory(body);
    const nextN = hist.reduce((mx, h) => Math.max(mx, h.n || 0), 0) + 1;
    hist.push({ n: nextN, result: 'passed', resultEmoji: '🟢', sha: 'abc1234', filter: 'SomeLongTestClassName', when: '2026-06-05 10:00Z', runUrl: 'https://github.com/o/r/actions/runs/1234567890', reportUrl: 'https://reports.example.com/e2e/branch-name/1234567890_abcdef/index.html' });
    body = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: 'SomeLongTestClassName', history: hist, requested: false });
  }
  const retained = helper.readHistory(body);
  assert.ok(body.length < 65536, `body should stay under the comment limit, was ${body.length}`);
  assert.equal(retained.length, 40, 'marker retains exactly the cap');
  assert.equal(retained[retained.length - 1].n, 200, 'run numbering keeps counting past the cap');
  assert.equal(retained[0].n, 161, 'oldest retained row is run #161');
  assert.match(body, /Run history \(latest 40; 160 older hidden\)/);
});

test('exactly at the cap shows no "older hidden" note', () => {
  let body = helper.renderBody({ status: 'queued', statusEmoji: '🟡', headline: 'x', filter: '', history: [], requested: false });
  for (let i = 1; i <= 40; i++) {
    const hist = helper.readHistory(body);
    const nextN = hist.reduce((mx, h) => Math.max(mx, h.n || 0), 0) + 1;
    hist.push({ n: nextN, result: 'passed', resultEmoji: '🟢', sha: 'a', filter: '', when: 't', runUrl: 'r', reportUrl: '' });
    body = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: '', history: hist, requested: false });
  }
  assert.doesNotMatch(body, /older hidden/);
  assert.equal(helper.readHistory(body)[0].n, 1, 'at the cap the oldest retained row is still #1');
});

test('renderBody links: run + report when present, omitted when absent', () => {
  const withReport = [{ n: 1, result: 'passed', resultEmoji: '🟢', sha: 'a', filter: '', when: 't', runUrl: 'http://r/1', reportUrl: 'http://x/1' }];
  const b1 = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: '', history: withReport, requested: false });
  assert.ok(b1.includes('[run](http://r/1)') && b1.includes('[report](http://x/1)'));
  const noReport = [{ n: 1, result: 'failed', resultEmoji: '🔴', sha: 'a', filter: '', when: 't', runUrl: 'http://r/1', reportUrl: '' }];
  const b2 = helper.renderBody({ status: 'failed', statusEmoji: '🔴', headline: 'x', filter: '', history: noReport, requested: false });
  assert.ok(b2.includes('[run](http://r/1)') && !b2.includes('report]('));
});

// --- result derivation -------------------------------------------------------------

test('deriveResult: a cancelled job is aborted regardless of a partial summary', () => {
  const r = helper.deriveResult({ summary: { passed: 5, failed: 2 }, jobStatus: 'cancelled', sha: 'abc1234' });
  assert.equal(r.status, 'aborted');
  assert.equal(r.statusEmoji, '⚪');
  assert.equal(r.resultEmoji, '⚪');
  assert.equal(r.resultWord, 'aborted');
  assert.match(r.headline, /Aborted \(run cancelled\) for `abc1234`/);
});

test('deriveResult: summary.aborted is treated as aborted even on a non-cancelled job', () => {
  const r = helper.deriveResult({ summary: { aborted: true, passed: 1 }, jobStatus: 'failure', sha: 'abc1234' });
  assert.equal(r.status, 'aborted');
});

test('deriveResult: failures win over passes; the skipped clause appears only when non-zero', () => {
  const withSkips = helper.deriveResult({ summary: { passed: 8, failed: 3, skipped: 2 }, jobStatus: 'failure', sha: 'sha' });
  assert.equal(withSkips.status, 'failed');
  assert.equal(withSkips.statusEmoji, '🔴');
  assert.match(withSkips.headline, /\*\*3 failed\*\*, 8 passed, 2 skipped for `sha`/);
  const noSkips = helper.deriveResult({ summary: { passed: 8, failed: 3, skipped: 0 }, jobStatus: 'failure', sha: 'sha' });
  assert.doesNotMatch(noSkips.headline, /skipped/);
});

test('deriveResult: a clean summary is a pass', () => {
  const r = helper.deriveResult({ summary: { passed: 12, failed: 0, skipped: 1 }, jobStatus: 'success', sha: 'sha' });
  assert.equal(r.status, 'passed');
  assert.equal(r.statusEmoji, '🟢');
  assert.equal(r.resultWord, 'passed');
  assert.match(r.headline, /\*\*12 passed\*\*, 1 skipped for `sha`/);
});

test('deriveResult: a missing summary is "no results" (early failure before the runner wrote one)', () => {
  const r = helper.deriveResult({ summary: null, jobStatus: 'failure', sha: 'sha' });
  assert.equal(r.status, 'failed');
  assert.equal(r.resultWord, 'no results');
  assert.match(r.headline, /without a summary for `sha`/);
});

test('deriveResult: a reportUrl is appended to the headline; absent leaves it untouched', () => {
  const withUrl = helper.deriveResult({ summary: { passed: 1, failed: 0 }, jobStatus: 'success', sha: 'sha', reportUrl: 'http://x/1' });
  assert.match(withUrl.headline, /\[▶ Open interactive report\]\(http:\/\/x\/1\)/);
  const noUrl = helper.deriveResult({ summary: { passed: 1, failed: 0 }, jobStatus: 'success', sha: 'sha' });
  assert.doesNotMatch(noUrl.headline, /Open interactive report/);
});

// --- find-once upsert contract (no lost-update re-fetch) ----------------------------

test('upsertSticky with a provided comment updates that exact id and does NOT re-list', async () => {
  const gh = fakeGithub(sticky(42));
  const id = await helper.upsertSticky({ github: gh, owner: 'o', repo: 'r', issue_number: 1, body: 'B', existing: sticky(42) });
  assert.equal(id, 42);
  assert.deepEqual(gh.calls, ['update#42']); // no 'list' — single round-trip to the known id
});

test('upsertSticky with existing=null creates without re-listing', async () => {
  const gh = fakeGithub(null);
  const id = await helper.upsertSticky({ github: gh, owner: 'o', repo: 'r', issue_number: 1, body: 'B', existing: null });
  assert.equal(id, 999);
  assert.deepEqual(gh.calls, ['create']);
});

test('upsertSticky without existing self-fetches (back-compat), then writes', async () => {
  const ghFound = fakeGithub(sticky(7));
  assert.equal(await helper.upsertSticky({ github: ghFound, owner: 'o', repo: 'r', issue_number: 1, body: 'B' }), 7);
  assert.deepEqual(ghFound.calls, ['list', 'update#7']);

  const ghNone = fakeGithub(null);
  assert.equal(await helper.upsertSticky({ github: ghNone, owner: 'o', repo: 'r', issue_number: 1, body: 'B' }), 999);
  assert.deepEqual(ghNone.calls, ['list', 'create']);
});

test('findSticky matches only comments carrying the marker', async () => {
  assert.equal(await helper.findSticky({ github: fakeGithub({ id: 5, body: 'no marker' }), owner: 'o', repo: 'r', issue_number: 1 }), null);
  const found = await helper.findSticky({ github: fakeGithub(sticky(8)), owner: 'o', repo: 'r', issue_number: 1 });
  assert.equal(found.id, 8);
});

// --- command parsing (trigger interpretation) --------------------------------------

test('parseCommand: anchored to the first line; a mid-text mention is ignored', () => {
  assert.equal(helper.parseCommand('look at `/run-tests-e2e Foo`').isCommand, false);
  assert.equal(helper.parseCommand('hey\n/run-tests-e2e Foo').isCommand, false); // not first line
  assert.deepEqual(helper.parseCommand('/run-tests-e2e Foo'), { isCommand: true, filter: 'Foo', valid: true });
});

test('parseCommand: CRLF line endings are handled (GitHub comments often arrive as \\r\\n)', () => {
  // JS `.` does not match \r, so a naive \n-split would leave a trailing \r and the command
  // would be silently ignored. Both no-arg and filtered forms must parse under CRLF.
  assert.deepEqual(helper.parseCommand('/run-tests-e2e\r\nsecond'), { isCommand: true, filter: '', valid: true });
  assert.deepEqual(helper.parseCommand('/run-tests-e2e MyClass\r\nsecond'), { isCommand: true, filter: 'MyClass', valid: true });
  assert.deepEqual(helper.parseCommand('/run-tests-e2e MyClass\r'), { isCommand: true, filter: 'MyClass', valid: true });
});

test('parseCommand: no filter = full suite; extra whitespace collapses', () => {
  assert.deepEqual(helper.parseCommand('/run-tests-e2e'), { isCommand: true, filter: '', valid: true });
  assert.deepEqual(helper.parseCommand('/run-tests-e2e   MyClass   Method  '),
    { isCommand: true, filter: 'MyClass Method', valid: true });
});

test('parseCommand: --abort-current is no longer special — it is just a literal filter token', () => {
  // Regression guard for the removed feature: the flag must NOT be silently stripped (which
  // would run the FULL suite as if it had vanished). It is passed through verbatim as a
  // literal xUnit filter — valid characters, but it matches no test, so the run is an
  // explicit empty no-result rather than a surprise full-suite run.
  const cmd = helper.parseCommand('/run-tests-e2e --abort-current');
  assert.equal(cmd.isCommand, true);
  assert.equal(cmd.filter, '--abort-current', 'flag is preserved as the filter, not stripped');
  assert.equal(cmd.valid, true, 'all-allowed-chars; harmless literal that matches nothing');
});

test('isValidFilter: rejects marker/table/HTML breakers, accepts real xUnit filters', () => {
  // Accepts: namespaced class/method, generics, params, spaces.
  for (const ok of ['', 'PasswordProtection', 'Ns.Class.Method', 'Foo(Bar=1)', 'A B', 'Class+Nested']) {
    assert.equal(helper.isValidFilter(ok), true, `should accept ${JSON.stringify(ok)}`);
  }
  // Rejects: the marker terminator, the table-cell separator, and HTML — the three things
  // that would corrupt the sticky (see the --> history-wipe bug this guards).
  for (const bad of ['A-->B', 'A|B', '<b>x</b>', 'a`b', 'a;b', 'a\nb']) {
    assert.equal(helper.isValidFilter(bad), false, `should reject ${JSON.stringify(bad)}`);
  }
});

test('a rejected filter never reaches the markers (the --> history-wipe is unreachable post-validation)', () => {
  // Belt-and-braces: prove the validation choke point closes the corruption path end-to-end.
  // A `-->` filter is rejected by isValidFilter, so it can never be embedded; but if it ever
  // were, renderBody+readHistory would silently drop history — so we assert the guard catches it.
  assert.equal(helper.isValidFilter('A-->B'), false);
  const body = helper.renderBody({ status: 'queued', statusEmoji: '🟡', headline: 'x', filter: 'A-->B',
    history: [{ n: 1, result: 'passed', resultEmoji: '🟢', sha: 'abc', filter: 'A-->B', when: 't', runUrl: 'r', reportUrl: '' }], requested: false });
  assert.deepEqual(helper.readHistory(body), [], 'demonstrates the corruption the validation prevents');
});

test('shouldFireRerun: only a confirmed unchecked->checked transition fires', () => {
  const idle = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: '', history: [], requested: false });
  const ticked = idle.replace('- [ ] 🔁 Re-run E2E tests', '- [x] 🔁 Re-run E2E tests');
  // unchecked -> checked: fire
  assert.equal(helper.shouldFireRerun({ priorBody: idle, body: ticked }), true);
  // already checked, text edited: do NOT fire
  assert.equal(helper.shouldFireRerun({ priorBody: ticked, body: ticked }), false);
  // checked -> unchecked: do NOT fire
  assert.equal(helper.shouldFireRerun({ priorBody: ticked, body: idle }), false);
});

test('shouldFireRerun: missing prior body fails safe (skip), never fires on a ticked box', () => {
  const idle = helper.renderBody({ status: 'passed', statusEmoji: '🟢', headline: 'x', filter: '', history: [], requested: false });
  const ticked = idle.replace('- [ ] 🔁 Re-run E2E tests', '- [x] 🔁 Re-run E2E tests');
  // No changes.body.from on the event → cannot prove a transition → skip even though box is ticked.
  assert.equal(helper.shouldFireRerun({ priorBody: undefined, body: ticked }), false);
  assert.equal(helper.shouldFireRerun({ priorBody: null, body: ticked }), false);
});

// --- maintainer authorization (404 = deny, 403 = fail-closed) -----------------------

function fakeGithubPerm(behaviour) {
  return {
    rest: {
      repos: {
        getCollaboratorPermissionLevel: async () => {
          if (behaviour.status) { const e = new Error('http'); e.status = behaviour.status; throw e; }
          return { data: { permission: behaviour.permission } };
        },
      },
    },
  };
}

test('isMaintainer: admin/write allowed; read/none denied', async () => {
  const args = { owner: 'o', repo: 'r', username: 'u' };
  assert.equal(await helper.isMaintainer({ github: fakeGithubPerm({ permission: 'admin' }), ...args }), true);
  assert.equal(await helper.isMaintainer({ github: fakeGithubPerm({ permission: 'write' }), ...args }), true);
  assert.equal(await helper.isMaintainer({ github: fakeGithubPerm({ permission: 'read' }), ...args }), false);
  assert.equal(await helper.isMaintainer({ github: fakeGithubPerm({ permission: 'none' }), ...args }), false);
});

test('isMaintainer: 404 (non-collaborator) is a deny, not an error', async () => {
  assert.equal(await helper.isMaintainer({ github: fakeGithubPerm({ status: 404 }), owner: 'o', repo: 'r', username: 'u' }), false);
});

test('isMaintainer: 403/other errors fail closed (rethrow → red job, no silent allow)', async () => {
  await assert.rejects(
    helper.isMaintainer({ github: fakeGithubPerm({ status: 403 }), owner: 'o', repo: 'r', username: 'u' }),
    /http/,
  );
});
