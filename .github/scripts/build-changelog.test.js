// Tests for build-changelog.js — fixed lists of commit subjects in, exact markdown out. Run with
// `npm test` (part of the Validate JS/TS job). No dependencies: Node's built-in test runner.
//
// These lock in how the Discord "Changes" changelog looks: a `## Changes` heading, a breaking
// callout when any commit is breaking, one `###` section per visible type in release-please's
// order and sort (scope, then subject), the type dropped and scope unwrapped on each line, nothing
// user-facing ever dropped except when over the code-point budget (whole trailing sections only,
// with an "…and N more" note), and markdown special characters escaped.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { buildChangelog, expandCommit, BUDGET } = require("./build-changelog.js");

const OPTS = {
    repoUrl: "https://github.com/o/r",
    baseTag: "v1.5.0-preview.1",
    headOid: "deadbee",
};

test("lists visible types as sections in release-please order, type dropped and scope unwrapped", () => {
    const result = buildChangelog(
        [
            "docs: explain cabins (#5)",
            "revert: undo the thing (#4)",
            "perf: fewer allocations (#3)",
            "fix(net): stop the crash (#2)",
            "feat: add the thing (#1)",
        ],
        OPTS,
    );
    assert.equal(
        result.markdown,
        [
            "## Changes",
            "### Features",
            "- add the thing ([#1](https://github.com/o/r/pull/1))",
            "### Bug Fixes",
            "- net: stop the crash ([#2](https://github.com/o/r/pull/2))",
            "### Performance Improvements",
            "- fewer allocations ([#3](https://github.com/o/r/pull/3))",
            "### Reverts",
            "- undo the thing ([#4](https://github.com/o/r/pull/4))",
            "### Documentation",
            "- explain cabins ([#5](https://github.com/o/r/pull/5))",
        ].join("\n"),
    );
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [5, 5, 0]);
});

test("within a section, entries sort by scope then subject with no-scope entries first", () => {
    const result = buildChangelog(
        [
            "feat(steam): auth renewal (#4)",
            "feat: zzz last no-scope (#3)",
            "feat(steam): another steam thing (#2)",
            "feat: aaa first no-scope (#1)",
        ],
        OPTS,
    );
    assert.equal(
        result.markdown,
        [
            "## Changes",
            "### Features",
            "- aaa first no-scope ([#1](https://github.com/o/r/pull/1))",
            "- zzz last no-scope ([#3](https://github.com/o/r/pull/3))",
            "- steam: another steam thing ([#2](https://github.com/o/r/pull/2))",
            "- steam: auth renewal ([#4](https://github.com/o/r/pull/4))",
        ].join("\n"),
    );
});

test("a breaking commit gets a top callout and also appears in its type section without a marker", () => {
    const result = buildChangelog(["feat(config)!: drop LAN transport (#10)", "fix: small fix (#9)"], OPTS);
    assert.equal(
        result.markdown,
        [
            "## Changes",
            "### ⚠️ Breaking changes",
            "- config: drop LAN transport ([#10](https://github.com/o/r/pull/10))",
            "### Features",
            "- config: drop LAN transport ([#10](https://github.com/o/r/pull/10))",
            "### Bug Fixes",
            "- small fix ([#9](https://github.com/o/r/pull/9))",
        ].join("\n"),
    );
});

test("a subject without (#N) is listed without a PR link", () => {
    const result = buildChangelog(["feat(tools): add request-correlation context"], OPTS);
    assert.equal(
        result.markdown,
        ["## Changes", "### Features", "- tools: add request-correlation context"].join("\n"),
    );
});

test("a non-conventional subject is listed under Other, verbatim, never dropped", () => {
    const result = buildChangelog(["Update README badges (#7)", "feat: real feature (#6)"], OPTS);
    assert.equal(
        result.markdown,
        [
            "## Changes",
            "### Features",
            "- real feature ([#6](https://github.com/o/r/pull/6))",
            "### Other",
            "- Update README badges ([#7](https://github.com/o/r/pull/7))",
        ].join("\n"),
    );
    assert.equal(result.visibleCount, 2);
});

test("an unknown conventional type is listed under Other with its type kept", () => {
    const result = buildChangelog(["wip: half-done thing (#7)"], OPTS);
    assert.equal(
        result.markdown,
        ["## Changes", "### Other", "- wip: half-done thing ([#7](https://github.com/o/r/pull/7))"].join("\n"),
    );
});

test("hidden types are omitted; a hidden-only range says so", () => {
    const result = buildChangelog(["ci: bump action (#9)", "chore: tidy", "refactor: rename (#8)"], OPTS);
    assert.equal(result.markdown, "## Changes\nNo player- or admin-facing changes in this build.");
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [3, 0, 3]);
});

test("a mixed range lists only the visible entries and omits the hidden ones", () => {
    const result = buildChangelog(["test: cover cabins (#3)", "fix(ci): quote globs (#2)", "chore: bump deps"], OPTS);
    assert.equal(
        result.markdown,
        ["## Changes", "### Bug Fixes", "- ci: quote globs ([#2](https://github.com/o/r/pull/2))"].join("\n"),
    );
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [3, 1, 2]);
});

test("markdown special characters in subjects are escaped", () => {
    const result = buildChangelog(
        ["fix: escape `code` and *stars* and _under_ and ~tilde~ and |pipe| and \\slash"],
        OPTS,
    );
    assert.equal(
        result.markdown,
        [
            "## Changes",
            "### Bug Fixes",
            "- escape \\`code\\` and \\*stars\\* and \\_under\\_ and \\~tilde\\~ and \\|pipe\\| and \\\\slash",
        ].join("\n"),
    );
});

test("a subject with markdown link syntax cannot inject a masked link", () => {
    const result = buildChangelog(["feat: [deployment guide](https://attacker.example) (#13)"], OPTS);
    assert.equal(
        result.markdown,
        [
            "## Changes",
            "### Features",
            "- \\[deployment guide\\](https://attacker.example) ([#13](https://github.com/o/r/pull/13))",
        ].join("\n"),
    );
});

test("a non-ASCII subject passes through and the budget counts code points", () => {
    const result = buildChangelog(["feat: 🎉 支持中文标题 (#12)"], OPTS);
    assert.equal(
        result.markdown,
        ["## Changes", "### Features", "- 🎉 支持中文标题 ([#12](https://github.com/o/r/pull/12))"].join("\n"),
    );
});

test("an over-budget range trims trailing entries at a line boundary with an …and N more notice", () => {
    const subjects = [];
    for (let i = 1; i <= 80; i++) {
        subjects.push(`feat: a rather long feature subject line to inflate the budget quickly number ${i} (#${i})`);
    }
    // A trailing docs entry so there is a second section to drop.
    subjects.push("docs: a documentation entry that should be dropped when over budget (#999)");
    const result = buildChangelog(subjects, OPTS);
    const markdown = result.markdown;
    assert.ok([...markdown].length <= BUDGET, `markdown is ${[...markdown].length} code points, budget is ${BUDGET}`);
    const lines = markdown.split("\n");
    assert.equal(lines[0], "## Changes");
    assert.equal(lines[1], "### Features");
    // The Documentation section is dropped whole; the notice names how many entries were left off.
    assert.ok(!markdown.includes("### Documentation"));
    assert.match(lines[lines.length - 1], /^- …and \d+ more$/);
});

test("release-please's release commit is excluded from every count", () => {
    const result = buildChangelog(
        ["chore(master): release sdvd-server 1.4.1 (#97)", "fix: stop the crash (#2)", "chore: bump deps"],
        OPTS,
    );
    assert.equal(
        result.markdown,
        ["## Changes", "### Bug Fixes", "- stop the crash ([#2](https://github.com/o/r/pull/2))"].join("\n"),
    );
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [2, 1, 1]);
    // A plain chore mentioning "release" without a version is NOT the release commit.
    assert.equal(buildChangelog(["chore: release notes cleanup"], OPTS).hiddenCount, 1);
});

test("a squash commit's conventional body lines become their own entries, linked to the same PR", () => {
    const body = [
        "Prose describing the change, which is not an entry.",
        "",
        "fix(docker): the console exits when the server isn't running",
        "  feat(docker): startup noise is hidden  ",
        "not a conventional line",
        "chore: internal tidy-up",
    ].join("\n");
    assert.deepEqual(expandCommit("feat(docker): quieter console (#661)", body), [
        "feat(docker): quieter console (#661)",
        "fix(docker): the console exits when the server isn't running (#661)",
        "feat(docker): startup noise is hidden (#661)",
        "chore: internal tidy-up (#661)",
    ]);
    // Without a PR suffix on the subject, body entries get none either.
    assert.deepEqual(expandCommit("fix: direct push", "feat: extra"), ["fix: direct push", "feat: extra"]);
    // An empty body contributes nothing.
    assert.deepEqual(expandCommit("fix: alone (#1)", ""), ["fix: alone (#1)"]);
});

test("zero commits reports no changes since the base tag", () => {
    const result = buildChangelog([], OPTS);
    assert.equal(result.markdown, "## Changes\nNo changes since `v1.5.0-preview.1`.");
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [0, 0, 0]);
});
