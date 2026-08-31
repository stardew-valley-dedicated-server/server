// Tests for build-changelog.js — fixed subject lists in, exact markdown out. Run with
// `npm test` (wired into the Validate JS/TS job). No dependencies: Node's built-in runner.
//
// These pin the Discord-facing contract: section grouping mirrors release-please's
// changelog-sections, no change is ever dropped (unparseable ⇒ "Other"), markdown
// characters in subjects are escaped, and the output stays under the 3500-code-point
// budget with a line-boundary cut and an "…and N more" notice.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { buildChangelog, BUDGET } = require("./build-changelog.js");

const OPTS = {
    repoUrl: "https://github.com/o/r",
    baseTag: "preview-1.5.0.1",
    headOid: "deadbee",
};
const COMPARE = "https://github.com/o/r/compare/preview-1.5.0.1...deadbee";

test("groups each visible type into its release-please section, in order", () => {
    const result = buildChangelog(
        [
            "docs: explain cabins (#5)",
            "revert: undo the thing (#4)",
            "perf: fewer allocations (#3)",
            "fix: stop the crash (#2)",
            "feat: add the thing (#1)",
        ],
        OPTS,
    );
    assert.equal(
        result.markdown,
        [
            "**Features**",
            "- feat: add the thing · [#1](https://github.com/o/r/pull/1)",
            "",
            "**Bug Fixes**",
            "- fix: stop the crash · [#2](https://github.com/o/r/pull/2)",
            "",
            "**Performance Improvements**",
            "- perf: fewer allocations · [#3](https://github.com/o/r/pull/3)",
            "",
            "**Reverts**",
            "- revert: undo the thing · [#4](https://github.com/o/r/pull/4)",
            "",
            "**Documentation**",
            "- docs: explain cabins · [#5](https://github.com/o/r/pull/5)",
        ].join("\n"),
    );
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [5, 5, 0]);
});

test("hidden-only range folds into an internal-changes note with the compare link", () => {
    const result = buildChangelog(["ci: bump action (#9)", "chore: tidy", "refactor: rename (#8)"], OPTS);
    assert.equal(result.markdown, `No user-facing changes (+3 internal changes) — [full diff](${COMPARE})`);
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [3, 0, 3]);
});

test("mixed range lists visible sections and appends the internal-changes line", () => {
    const result = buildChangelog(["test: cover cabins (#3)", "fix(ci): quote globs (#2)", "chore: bump deps"], OPTS);
    assert.equal(
        result.markdown,
        [
            "**Bug Fixes**",
            "- fix(ci): quote globs · [#2](https://github.com/o/r/pull/2)",
            "",
            "+2 internal changes",
        ].join("\n"),
    );
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [3, 1, 2]);
});

test("a single internal change uses the singular note", () => {
    const result = buildChangelog(["chore: bump deps"], OPTS);
    assert.equal(result.markdown, `No user-facing changes (+1 internal change) — [full diff](${COMPARE})`);
});

test("a subject without (#N) is listed without a PR link", () => {
    const result = buildChangelog(["feat(tools): add request-correlation context"], OPTS);
    assert.equal(result.markdown, ["**Features**", "- feat(tools): add request-correlation context"].join("\n"));
});

test("a non-conventional subject lands under Other verbatim, never dropped", () => {
    const result = buildChangelog(["Update README badges (#7)", "feat: real feature (#6)"], OPTS);
    assert.equal(
        result.markdown,
        [
            "**Features**",
            "- feat: real feature · [#6](https://github.com/o/r/pull/6)",
            "",
            "**Other**",
            "- Update README badges · [#7](https://github.com/o/r/pull/7)",
        ].join("\n"),
    );
    assert.equal(result.visibleCount, 2);
});

test("an unknown conventional type lands under Other", () => {
    const result = buildChangelog(["wip: half-done thing (#7)"], OPTS);
    assert.equal(
        result.markdown,
        ["**Other**", "- wip: half-done thing · [#7](https://github.com/o/r/pull/7)"].join("\n"),
    );
});

test("feat!: gets the breaking-change warning marker", () => {
    const result = buildChangelog(["feat!: drop LAN transport (#10)"], OPTS);
    assert.equal(
        result.markdown,
        ["**Features**", "- ⚠ feat!: drop LAN transport · [#10](https://github.com/o/r/pull/10)"].join("\n"),
    );
});

test("markdown special characters in subjects are escaped", () => {
    const result = buildChangelog(
        ["fix: escape `code` and *stars* and _under_ and ~tilde~ and |pipe| and \\slash"],
        OPTS,
    );
    assert.equal(
        result.markdown,
        [
            "**Bug Fixes**",
            "- fix: escape \\`code\\` and \\*stars\\* and \\_under\\_ and \\~tilde\\~ and \\|pipe\\| and \\\\slash",
        ].join("\n"),
    );
});

test("a non-ASCII subject passes through and the budget counts code points", () => {
    const result = buildChangelog(["feat: 🎉 支持中文标题 (#12)"], OPTS);
    assert.equal(
        result.markdown,
        ["**Features**", "- feat: 🎉 支持中文标题 · [#12](https://github.com/o/r/pull/12)"].join("\n"),
    );
});

test("an over-budget list is cut at a line boundary with an …and N more notice", () => {
    const subjects = [];
    for (let i = 1; i <= 100; i++) {
        subjects.push(`feat: a rather long feature subject line to inflate the budget quickly number ${i} (#${i})`);
    }
    subjects.push("chore: internal");
    const result = buildChangelog(subjects, OPTS);
    const markdown = result.markdown;
    assert.ok([...markdown].length <= BUDGET, `markdown is ${[...markdown].length} code points, budget is ${BUDGET}`);
    const lines = markdown.split("\n");
    // Every kept entry is whole (cut at a line boundary), the notice names the dropped
    // count, and the internal-changes line survives truncation.
    const keptEntries = lines.filter((l) => l.startsWith("- feat:")).length;
    assert.ok(keptEntries > 0 && keptEntries < 100);
    const noticeLine = lines.find((l) => l.startsWith("…and "));
    assert.equal(noticeLine, `…and ${100 - keptEntries} more — [full diff](${COMPARE})`);
    assert.equal(lines[lines.length - 1], "+1 internal change");
    for (const line of lines.filter((l) => l.startsWith("- "))) {
        assert.match(line, /· \[#\d+\]\(https:\/\/github\.com\/o\/r\/pull\/\d+\)$/);
    }
});

test("release-please's release commit is excluded from every count", () => {
    const result = buildChangelog(
        ["chore(master): release sdvd-server 1.4.1 (#97)", "fix: stop the crash (#2)", "chore: bump deps"],
        OPTS,
    );
    assert.equal(
        result.markdown,
        ["**Bug Fixes**", "- fix: stop the crash · [#2](https://github.com/o/r/pull/2)", "", "+1 internal change"].join(
            "\n",
        ),
    );
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [2, 1, 1]);
    // A plain chore mentioning "release" without a version is NOT the release commit.
    assert.equal(buildChangelog(["chore: release notes cleanup"], OPTS).hiddenCount, 1);
});

test("zero commits reports no changes since the base tag", () => {
    const result = buildChangelog([], OPTS);
    assert.equal(result.markdown, `No changes since \`preview-1.5.0.1\` — [full diff](${COMPARE})`);
    assert.deepEqual([result.count, result.visibleCount, result.hiddenCount], [0, 0, 0]);
});
