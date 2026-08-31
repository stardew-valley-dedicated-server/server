// Builds the Discord changelog for a build: every first-parent commit in the build's range,
// grouped into the same sections release-please uses, budgeted for a Discord embed description.
// Used by .github/actions/build-changelog; the pure logic is exported for `npm test`.
//
// CLI contract (the composite action wires this up): `git log --first-parent
// --format='%H%x1f%s' BASE..HEAD` lines on stdin; BASE_TAG, HEAD_OID, REPO_URL env in;
// markdown / count / visible-count / hidden-count / compare-url appended to $GITHUB_OUTPUT
// (printed to stdout when GITHUB_OUTPUT is unset, for local runs).

// Section order and titles mirror release-please-config.json's changelog-sections, so a
// Discord post reads like the GitHub release page. Types "hidden" there fold into one
// "+N internal changes" line here. Anything else — unknown type or non-conventional
// subject — lands under "Other" verbatim; a change is never dropped for a parse failure.
const VISIBLE_SECTIONS = [
    { type: "feat", title: "Features" },
    { type: "fix", title: "Bug Fixes" },
    { type: "perf", title: "Performance Improvements" },
    { type: "revert", title: "Reverts" },
    { type: "docs", title: "Documentation" },
];
const HIDDEN_TYPES = new Set(["style", "chore", "refactor", "test", "build", "ci"]);

// Code points, not UTF-16 units — leaves headroom under Discord's 4096-char description
// limit for the caller's trailing "Use it" block, and under the 6000-char embed total.
const BUDGET = 3500;

const CONVENTIONAL_RE = /^([a-z]+)(\([^()]*\))?(!)?: \S/i;
const PR_SUFFIX_RE = /\s*\(#(\d+)\)$/;
// release-please's own release commit ("chore(master): release sdvd-server 1.4.1") is the
// commit a release tag points at, so it sits inside its own range. It is release machinery,
// not a change — excluded from every count so it can't pad "+N internal changes".
const RELEASE_COMMIT_RE = /^chore(\([^()]*\))?: release\b.*\d+\.\d+\.\d+/;

/** @param {string} s @returns {number} length in code points (what Discord counts) */
function codePoints(s) {
    return [...s].length;
}

/**
 * Backslash-escape Discord inline-markdown characters so a commit subject renders as
 * plain text. `[`/`]` stay unescaped: subjects are emitted as plain text, never as link
 * text, so they cannot break the appended PR link.
 * @param {string} text
 * @returns {string}
 */
function escapeMarkdown(text) {
    return text.replace(/[\\`*_~|]/g, (c) => `\\${c}`);
}

/**
 * Classify one commit subject.
 * @param {string} subject - Raw `git log %s` subject line.
 * @returns {{text: string, type: string|null, breaking: boolean, pr: number|null}}
 *   `text` is the subject without its trailing `(#N)`; `type` is the lowercased
 *   conventional-commit type or null for a non-conventional subject.
 */
function parseSubject(subject) {
    const prMatch = subject.match(PR_SUFFIX_RE);
    const pr = prMatch ? Number(prMatch[1]) : null;
    const text = (prMatch ? subject.slice(0, prMatch.index) : subject).trim();
    const conv = text.match(CONVENTIONAL_RE);
    return {
        text,
        type: conv ? conv[1].toLowerCase() : null,
        breaking: conv ? conv[3] === "!" || /\bBREAKING[ -]CHANGE\b/.test(text) : false,
        pr,
    };
}

/** @returns {string} one `- …` bullet; ⚠ marks a breaking change, the PR link is appended. */
function renderEntry(entry, repoUrl) {
    const warn = entry.breaking ? "⚠ " : "";
    const link = entry.pr === null ? "" : ` · [#${entry.pr}](${repoUrl}/pull/${entry.pr})`;
    return `- ${warn}${escapeMarkdown(entry.text)}${link}`;
}

/** @returns {string} the "+N internal changes" summary line. */
function internalNote(hiddenCount) {
    return `+${hiddenCount} internal change${hiddenCount === 1 ? "" : "s"}`;
}

/**
 * Build the changelog markdown for a commit range.
 * @param {string[]} subjects - `git log %s` subjects, newest first (git log order).
 * @param {{repoUrl: string, baseTag: string, headOid: string}} opts
 * @returns {{markdown: string, count: number, visibleCount: number, hiddenCount: number, compareUrl: string}}
 *   `markdown` is at most BUDGET code points; over-budget ranges end with
 *   "…and N more — [full diff](…)" cut at a line boundary.
 */
function buildChangelog(subjects, { repoUrl, baseTag, headOid }) {
    const compareUrl = `${repoUrl}/compare/${baseTag}...${headOid}`;
    const fullDiff = `[full diff](${compareUrl})`;

    const changes = subjects.filter((s) => !RELEASE_COMMIT_RE.test(s));
    const sections = VISIBLE_SECTIONS.map((s) => ({ ...s, entries: [] }));
    const other = { title: "Other", entries: [] };
    let hiddenCount = 0;
    for (const subject of changes) {
        const entry = parseSubject(subject);
        if (entry.type !== null && HIDDEN_TYPES.has(entry.type)) {
            hiddenCount += 1;
            continue;
        }
        const section = sections.find((s) => s.type === entry.type) ?? other;
        section.entries.push(entry);
    }

    const count = changes.length;
    const visibleCount = count - hiddenCount;
    const result = { count, visibleCount, hiddenCount, compareUrl };

    if (count === 0) {
        return { ...result, markdown: `No changes since \`${baseTag}\` — ${fullDiff}` };
    }
    if (visibleCount === 0) {
        return { ...result, markdown: `No user-facing changes (${internalNote(hiddenCount)}) — ${fullDiff}` };
    }

    // Flat line list, tagged so truncation can count dropped changes and trim headers
    // whose entries were all cut.
    const lines = [];
    for (const section of [...sections, other]) {
        if (section.entries.length === 0) {
            continue;
        }
        if (lines.length > 0) {
            lines.push({ text: "", isEntry: false });
        }
        lines.push({ text: `**${section.title}**`, isEntry: false });
        for (const entry of section.entries) {
            lines.push({ text: renderEntry(entry, repoUrl), isEntry: true });
        }
    }
    const hiddenLine = hiddenCount > 0 ? internalNote(hiddenCount) : null;
    const tailAfter = (body) => (hiddenLine === null ? body : `${body}\n\n${hiddenLine}`);

    const full = tailAfter(lines.map((l) => l.text).join("\n"));
    if (codePoints(full) <= BUDGET) {
        return { ...result, markdown: full };
    }

    // Over budget: emit whole lines while the worst-case tail (notice with the largest
    // possible N, plus the internal-changes line) still fits, then trim trailing headers
    // and blanks so the cut lands after an entry.
    const notice = (n) => `…and ${n} more — ${fullDiff}`;
    const reserve =
        codePoints(`\n${notice(visibleCount)}`) + (hiddenLine === null ? 0 : codePoints(`\n\n${hiddenLine}`));
    const kept = [];
    let used = 0;
    for (const line of lines) {
        const cost = codePoints(line.text) + (kept.length > 0 ? 1 : 0);
        if (used + cost > BUDGET - reserve) {
            break;
        }
        kept.push(line);
        used += cost;
    }
    while (kept.length > 0 && !kept[kept.length - 1].isEntry) {
        kept.pop();
    }
    const dropped = visibleCount - kept.filter((l) => l.isEntry).length;
    const markdown = tailAfter(`${kept.map((l) => l.text).join("\n")}\n${notice(dropped)}`);
    return { ...result, markdown };
}

module.exports = { buildChangelog, parseSubject, escapeMarkdown, BUDGET };

// --- CLI entry (composite-action wiring) ---------------------------------------------

/** @param {string} name @returns {string} */
function requireEnv(name) {
    const value = process.env[name];
    if (value === undefined || value === "") {
        console.error(`::error::build-changelog.js: missing required env ${name}`);
        process.exit(1);
    }
    return value;
}

function main() {
    const { readFileSync, appendFileSync } = require("node:fs");
    const { randomUUID } = require("node:crypto");

    const baseTag = requireEnv("BASE_TAG");
    const headOid = requireEnv("HEAD_OID");
    const repoUrl = requireEnv("REPO_URL");

    // One `<sha>\x1f<subject>` line per commit; the sha guarantees a non-empty line per
    // commit so the count stays right even for an empty subject.
    const subjects = readFileSync(0, "utf8")
        .split("\n")
        .filter((line) => line.includes("\x1f"))
        .map((line) => line.slice(line.indexOf("\x1f") + 1));

    const result = buildChangelog(subjects, { repoUrl, baseTag, headOid });
    const delimiter = `EOF_${randomUUID()}`;
    const output = [
        `markdown<<${delimiter}`,
        result.markdown,
        delimiter,
        `count=${result.count}`,
        `visible-count=${result.visibleCount}`,
        `hidden-count=${result.hiddenCount}`,
        `compare-url=${result.compareUrl}`,
        "",
    ].join("\n");

    if (process.env.GITHUB_OUTPUT) {
        appendFileSync(process.env.GITHUB_OUTPUT, output);
    }
    console.log(`${result.count} commits (${result.visibleCount} visible, ${result.hiddenCount} internal)`);
    console.log(result.markdown);
}

if (require.main === module) {
    main();
}
