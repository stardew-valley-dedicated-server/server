// Builds the Discord changelog for a build: every first-parent commit in the build's range,
// as one flat "Changes" list ordered by release-please's type priority, budgeted for a Discord
// embed description. Used by .github/actions/build-changelog; the pure logic is exported for
// `npm test`.
//
// CLI contract (the composite action wires this up): `git log --first-parent
// --format='%H%x1f%s' BASE..HEAD` lines on stdin; BASE_TAG, HEAD_OID, REPO_URL env in;
// markdown / count / visible-count / hidden-count / compare-url appended to $GITHUB_OUTPUT
// (printed to stdout when GITHUB_OUTPUT is unset, for local runs).

// The single "Changes" list heading. One flat list, no per-type sub-headers.
const HEADER = "**Changes**";

// Visible commit types in the order release-please lists them, so a Discord post reads like
// the GitHub release page. Hidden types fold into one "+N internal changes" line. Anything
// else — unknown type or non-conventional subject — sorts after these visible types, listed
// verbatim; a change is never dropped for a parse failure.
const VISIBLE_TYPES = ["feat", "fix", "perf", "revert", "docs"];
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
 * plain text. Brackets are escaped too: a subject containing `[label](url)` must render
 * literally, not as a masked link — commit subjects are contributor-controlled and these
 * posts go to public channels. (Embeds never ping, so @-mentions need no handling.)
 * @param {string} text
 * @returns {string}
 */
function escapeMarkdown(text) {
    return text.replace(/[\\`*_~|[\]]/g, (c) => `\\${c}`);
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

/**
 * Build the changelog markdown for a commit range.
 * @param {string[]} subjects - `git log %s` subjects, newest first (git log order).
 * @param {{repoUrl: string, baseTag: string, headOid: string}} opts
 * @returns {{markdown: string, count: number, visibleCount: number, hiddenCount: number, compareUrl: string}}
 *   `markdown` is at most BUDGET code points; over-budget ranges drop trailing entries and
 *   name the dropped count on the bottom line, cut at a line boundary.
 */
function buildChangelog(subjects, { repoUrl, baseTag, headOid }) {
    const compareUrl = `${repoUrl}/compare/${baseTag}...${headOid}`;
    const diffLink = `[diff](${compareUrl})`;

    const changes = subjects.filter((s) => !RELEASE_COMMIT_RE.test(s));
    const visible = new Map(VISIBLE_TYPES.map((t) => [t, []]));
    const other = [];
    let hiddenCount = 0;
    for (const subject of changes) {
        const entry = parseSubject(subject);
        if (entry.type !== null && HIDDEN_TYPES.has(entry.type)) {
            hiddenCount += 1;
            continue;
        }
        (visible.get(entry.type) ?? other).push(entry);
    }

    const count = changes.length;
    const visibleCount = count - hiddenCount;
    const result = { count, visibleCount, hiddenCount, compareUrl };

    if (count === 0) {
        return { ...result, markdown: `No changes since \`${baseTag}\` · ${diffLink}` };
    }

    // Bottom line: an optional "…and N more" truncation notice, the internal-changes count,
    // and always the diff link — joined with " · " so it reads like the "· #PR" suffix on
    // every entry above.
    const bottomLine = (droppedCount) => {
        const parts = [];
        if (droppedCount > 0) {
            parts.push(`…and ${droppedCount} more`);
        }
        if (hiddenCount > 0) {
            parts.push(`+${hiddenCount} internal change${hiddenCount === 1 ? "" : "s"}`);
        }
        parts.push(diffLink);
        return parts.join(" · ");
    };

    const entryLines = [...VISIBLE_TYPES.flatMap((t) => visible.get(t)), ...other].map((e) => renderEntry(e, repoUrl));

    const full = [HEADER, ...entryLines, bottomLine(0)].join("\n");
    if (codePoints(full) <= BUDGET) {
        return { ...result, markdown: full };
    }

    // Over budget: keep whole entry lines while the worst-case bottom line (the largest
    // possible dropped count) still fits, then report how many were dropped.
    const reserve = 1 + codePoints(bottomLine(visibleCount));
    let used = codePoints(HEADER);
    const kept = [];
    for (const line of entryLines) {
        const cost = 1 + codePoints(line);
        if (used + cost + reserve > BUDGET) {
            break;
        }
        kept.push(line);
        used += cost;
    }
    const dropped = visibleCount - kept.length;
    const markdown = [HEADER, ...kept, bottomLine(dropped)].join("\n");
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
