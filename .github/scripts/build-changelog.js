// Turns a build's commit range into the "Changes" changelog we post to Discord: a `## Changes`
// heading, a `### ⚠️ Breaking changes` callout when any commit is breaking, then one `###` section
// per visible commit type in release-please's order, each sorted the way release-please sorts
// (scope, then subject). The core function is exported so `npm test` can call it.
//
// How the action runs it: it pipes `git log --first-parent --format='%H%x1f%s' BASE..HEAD` in on
// stdin and sets the BASE_TAG, HEAD_OID and REPO_URL env vars. We write markdown / count /
// visible-count / hidden-count / compare-url to $GITHUB_OUTPUT (or print them to stdout locally).
// The workflow prepends a `# Build|Release [<version>](<hub>) is available!` line and posts the
// whole thing as the first embed's description; the full diff is reached from the "See all changes" button.

// The heading above the list.
const HEADER = "## Changes";

// Visible commit types in release-please's changelog order, each paired with the section heading
// release-please uses (see release-please-config.json). Grouping by type makes the per-line type
// prefix redundant, so entries render with the type dropped and the scope unwrapped. Anything else
// (an unknown type, or a subject that isn't a conventional commit) is listed under "Other" — we
// never drop a commit just because we couldn't parse it. Hidden types below are omitted entirely.
const GROUPS = [
    ["feat", "### Features"],
    ["fix", "### Bug Fixes"],
    ["perf", "### Performance Improvements"],
    ["revert", "### Reverts"],
    ["docs", "### Documentation"],
];
const HIDDEN_TYPES = new Set(["style", "chore", "refactor", "test", "build", "ci"]);

// We measure length in code points (what Discord counts), not JS string length. The workflow
// prepends only a short `# Build|Release [<version>](<hub>) is available!` line before this becomes
// the first embed's description, so the budget is Discord's 4096 description limit minus a small
// reserve for that line. The full diff is always a button away, so an over-budget range trims
// trailing entries at a line boundary and notes how many it left off.
const BUDGET = 3900;

const CONVENTIONAL_RE = /^([a-z]+)(?:\(([^()]*)\))?(!)?: (.*)$/i;
const PR_SUFFIX_RE = /\s*\(#(\d+)\)$/;
// release-please's own "release" commit (e.g. "chore(master): release 1.5.0") is what
// a release tag points at, so it falls inside its own range. It's release plumbing, not a real
// change, so we drop it from every count — otherwise it would inflate the hidden-change count.
const RELEASE_COMMIT_RE = /^chore(\([^()]*\))?: release\b.*\d+\.\d+\.\d+/;

/** @param {string} s @returns {number} length in code points (what Discord counts) */
function codePoints(s) {
    return [...s].length;
}

/**
 * Escapes the characters Discord treats as markdown so a commit subject shows as plain text.
 * We escape brackets too, so a subject like `[label](url)` shows literally instead of turning
 * into a clickable link — subjects come from contributors and these posts are public. (Embeds
 * never ping, so we don't need to handle @-mentions.)
 * @param {string} text
 * @returns {string}
 */
function escapeMarkdown(text) {
    return text.replace(/[\\`*_~|[\]]/g, (c) => `\\${c}`);
}

/**
 * Classify one commit subject.
 * @param {string} subject - Raw `git log %s` subject line.
 * @returns {{text: string, type: string|null, scope: string, subject: string, breaking: boolean, pr: number|null}}
 *   `text` is the subject with its trailing `(#N)` removed; `type` is the lowercased
 *   conventional-commit type (null if not a conventional commit); `scope` is the unwrapped scope
 *   ("" if none); `subject` is the description with the `type(scope): ` prefix removed (the whole
 *   `text` for a non-conventional subject).
 */
function parseSubject(subject) {
    const prMatch = subject.match(PR_SUFFIX_RE);
    const pr = prMatch ? Number(prMatch[1]) : null;
    const text = (prMatch ? subject.slice(0, prMatch.index) : subject).trim();
    const conv = text.match(CONVENTIONAL_RE);
    return {
        text,
        type: conv ? conv[1].toLowerCase() : null,
        scope: conv ? (conv[2] ?? "") : "",
        subject: conv ? conv[4] : text,
        breaking: conv ? conv[3] === "!" || /\bBREAKING[ -]CHANGE\b/.test(text) : false,
        pr,
    };
}

const GROUP_TYPES = new Set(GROUPS.map(([t]) => t));

/**
 * One `- …` bullet with the PR link on the end. For a commit in a type section the type is dropped
 * and the scope unwrapped (`feat(steam): x` → `steam: x`), since the section heading carries the
 * type. An "Other" entry (unknown type or non-conventional) keeps its full subject verbatim —
 * there's no heading to convey its type, so dropping it would mangle the line.
 * @returns {string}
 */
function renderEntry(entry, repoUrl) {
    const link = entry.pr === null ? "" : ` ([#${entry.pr}](${repoUrl}/pull/${entry.pr}))`;
    const grouped = entry.type !== null && GROUP_TYPES.has(entry.type);
    const label = grouped ? (entry.scope ? `${entry.scope}: ${entry.subject}` : entry.subject) : entry.text;
    return `- ${escapeMarkdown(label)}${link}`;
}

/** release-please's commitsSort: scope first (no-scope sorts first), then subject. */
function byScopeThenSubject(a, b) {
    return a.scope.localeCompare(b.scope) || a.subject.localeCompare(b.subject);
}

/** @returns {string[]} the entries of one section, sorted, rendered. */
function renderSection(entries, repoUrl) {
    return [...entries].sort(byScopeThenSubject).map((e) => renderEntry(e, repoUrl));
}

/**
 * Build the changelog markdown for a commit range.
 * @param {string[]} subjects - `git log %s` subjects, newest first (git log order).
 * @param {{repoUrl: string, baseTag: string, headOid: string}} opts
 * @returns {{markdown: string, count: number, visibleCount: number, hiddenCount: number, compareUrl: string}}
 *   `markdown` is never longer than BUDGET code points. If the full list wouldn't fit, trailing
 *   entries are dropped at a line boundary (dropping any section heading left empty) and a bottom
 *   line says how many entries were left off.
 */
function buildChangelog(subjects, { repoUrl, baseTag, headOid }) {
    const compareUrl = `${repoUrl}/compare/${baseTag}...${headOid}`;

    const changes = subjects.filter((s) => !RELEASE_COMMIT_RE.test(s));
    const buckets = new Map(GROUPS.map(([t]) => [t, []]));
    const other = [];
    let hiddenCount = 0;
    for (const subject of changes) {
        const entry = parseSubject(subject);
        if (entry.type !== null && HIDDEN_TYPES.has(entry.type)) {
            hiddenCount += 1;
            continue;
        }
        (buckets.get(entry.type) ?? other).push(entry);
    }

    const count = changes.length;
    const visibleCount = count - hiddenCount;
    const result = { count, visibleCount, hiddenCount, compareUrl };

    if (visibleCount === 0) {
        // Nothing user-facing to list: an empty range, or one that was only hidden (chore/ci/…).
        const note =
            count === 0 ? `No changes since \`${baseTag}\`.` : "No player- or admin-facing changes in this build.";
        return { ...result, markdown: `${HEADER}\n${note}` };
    }

    // Breaking changes get a callout at the top (like release-please's ⚠ BREAKING CHANGES section);
    // the icon lives only on this heading — each commit also appears in its own type section below,
    // rendered the same as any other entry.
    const breaking = [...GROUPS.flatMap(([t]) => buckets.get(t)), ...other].filter((e) => e.breaking);
    const sections = [];
    if (breaking.length) {
        sections.push(["### ⚠️ Breaking changes", ...renderSection(breaking, repoUrl)]);
    }
    for (const [type, heading] of GROUPS) {
        const entries = buckets.get(type);
        if (entries.length) {
            sections.push([heading, ...renderSection(entries, repoUrl)]);
        }
    }
    if (other.length) {
        sections.push(["### Other", ...renderSection(other, repoUrl)]);
    }

    // No blank separators — the `###` headings carry their own spacing.
    const lines = sections.flat();
    const full = [HEADER, ...lines].join("\n");
    if (codePoints(full) <= BUDGET) {
        return { ...result, markdown: full };
    }

    // Over budget: keep lines from the top until the next won't fit (reserving room for the notice,
    // measured at its longest in case everything remaining is dropped), then drop any trailing
    // heading left with no entries and note how many entries were left off.
    const isHeading = (line) => line.startsWith("### ");
    const totalEntries = lines.filter((line) => !isHeading(line)).length;
    const notice = (n) => `- …and ${n} more`;
    const reserve = 1 + codePoints(notice(totalEntries));
    const kept = [HEADER];
    let used = codePoints(HEADER);
    let keptEntries = 0;
    for (const line of lines) {
        const cost = 1 + codePoints(line);
        if (used + cost + reserve > BUDGET) {
            break;
        }
        kept.push(line);
        used += cost;
        if (!isHeading(line)) {
            keptEntries += 1;
        }
    }
    while (kept.length > 1 && isHeading(kept[kept.length - 1])) {
        kept.pop();
    }
    const dropped = totalEntries - keptEntries;
    const markdown = dropped > 0 ? [...kept, notice(dropped)].join("\n") : kept.join("\n");
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

    // Each line is `<sha>\x1f<subject>`. We split on the \x1f, which guarantees one line per commit
    // even when the subject is empty, so the commit count stays correct.
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
