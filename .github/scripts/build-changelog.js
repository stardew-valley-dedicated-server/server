// Turns a build's commit range into the "Changes" changelog we post to Discord: a `## Changes`
// heading, a `### ⚠️ Breaking changes` callout when any commit is breaking, then one `###` section
// per visible commit type in release-please's order, each sorted the way release-please sorts
// (scope, then subject). The core function is exported so `npm test` can call it.
//
// How the action runs it: it pipes `git log --first-parent --format='%H%x1f%s%x1f%b%x1e' BASE..HEAD`
// in on stdin (\x1e-terminated records, so a multi-line body stays in one record) and sets the
// BASE_TAG, HEAD_OID and REPO_URL env vars. We write markdown / count / visible-count /
// hidden-count / compare-url to $GITHUB_OUTPUT (or print them to stdout locally).
// The workflow prepends a `# Build|Release [<version>](<hub>) is available!` line and posts the
// whole thing as the first embed's description; the full diff is reached from the "See all changes" button.
//
// A squash commit whose body carries further conventional-commit lines (release-please's
// "multiple fixes or features in one PR" convention) contributes one entry per such line on top
// of its subject, so the Discord post lists the same changes the release notes will.

// The heading above the list.
const HEADER = "## Changes";

// Visible commit types in release-please's changelog order, each paired with the section heading
// release-please uses (see release-please-config.json). Grouping by type makes the per-line type
// prefix redundant, so entries render with the type dropped and the scope unwrapped. Anything else
// (an unknown type, or a subject that isn't a conventional commit) is listed under "Other" — we
// never drop a commit just because we couldn't parse it. Hidden types below are omitted entirely,
// unless the commit is breaking: release-please keeps a breaking commit whatever its type, so it is
// listed in the callout and under "Other" (full subject, since no section carries its type).
const GROUPS = [
    ["feat", "### Features"],
    ["fix", "### Bug Fixes"],
    ["perf", "### Performance Improvements"],
    ["revert", "### Reverts"],
    ["docs", "### Documentation"],
];
const HIDDEN_TYPES = new Set(["style", "chore", "refactor", "test", "build", "ci"]);
// Every type release-please knows. A body line only counts as an extra entry with one of these,
// since prose like "Note: …" also fits the `word: text` shape.
const KNOWN_TYPES = new Set([...GROUPS.map(([t]) => t), ...HIDDEN_TYPES]);

// We measure length in code points (what Discord counts), not JS string length. The workflow
// prepends only a short `# Build|Release [<version>](<hub>) is available!` line before this becomes
// the first embed's description, so the budget is Discord's 4096 description limit minus a small
// reserve for that line. The full diff is always a button away, so an over-budget range trims
// trailing entries at a line boundary and notes how many it left off.
const BUDGET = 3900;

const CONVENTIONAL_RE = /^([a-z]+)(?:\(([^()]*)\))?(!)?: (.*)$/i;
const PR_SUFFIX_RE = /\s*\(#(\d+)\)$/;
// A commit is breaking with a `!` in the subject or a `BREAKING CHANGE:` / `BREAKING-CHANGE:`
// footer in the body — release-please honours both, so the callout must recognise both too. The
// token must start a line and carry the colon (a real footer), so ordinary prose that merely
// mentions "a BREAKING CHANGE" doesn't trigger a false callout.
const BREAKING_RE = /^BREAKING(?: CHANGE|-CHANGE):/m;
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
        breaking: conv ? conv[3] === "!" : false,
        pr,
    };
}

/**
 * Expand one commit into the entries it contributes: itself (subject plus body, so a `BREAKING
 * CHANGE:` footer is seen), plus every body line that is itself a conventional commit (a squash of
 * a PR shipping several changes, with a known type). Body entries carry the subject's `(#N)` so
 * they link to the same PR, and have no body of their own. Anything else in the body is ignored.
 * @param {string} subject - Raw `git log %s` subject line.
 * @param {string} body - Raw `git log %b` body (may be empty).
 * @returns {{subject: string, body: string}[]}
 */
function expandCommit(subject, body) {
    const pr = subject.match(PR_SUFFIX_RE);
    const suffix = pr ? ` (#${pr[1]})` : "";
    const extra = body
        .split("\n")
        .map((line) => line.trim())
        .filter((line) => KNOWN_TYPES.has((line.match(CONVENTIONAL_RE)?.[1] ?? "").toLowerCase()))
        .map((line) => ({ subject: `${line}${suffix}`, body: "" }));
    return [{ subject, body }, ...extra];
}

const GROUP_TYPES = new Set(GROUPS.map(([t]) => t));

/**
 * One `- …` bullet with the PR link on the end. For a commit in a type section the type is dropped
 * and the scope unwrapped (`feat(steam): x` → `steam: x`), since the section heading carries the
 * type. An "Other" entry (unknown type, hidden-but-breaking type, or non-conventional) keeps its
 * full subject verbatim —
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

/** @returns {{text: string, id: number}[]} one section's entries, sorted and rendered. */
function renderSection(entries, repoUrl) {
    return [...entries].sort(byScopeThenSubject).map((e) => ({ text: renderEntry(e, repoUrl), id: e.id }));
}

/**
 * Build the changelog markdown for a commit range.
 * @param {(string | {subject: string, body?: string})[]} commits - commits newest first (git log
 *   order). A plain string is treated as a subject with no body; the body is scanned for a
 *   `BREAKING CHANGE:` footer so a commit that declares breaking only in its body still gets the callout.
 * @param {{repoUrl: string, baseTag: string, headOid: string}} opts
 * @returns {{markdown: string, count: number, visibleCount: number, hiddenCount: number, compareUrl: string}}
 *   `markdown` is never longer than BUDGET code points. If the full list wouldn't fit, trailing
 *   entries are dropped at a line boundary (dropping any section heading left empty) and a bottom
 *   line says how many unique commits were left off.
 */
function buildChangelog(commits, { repoUrl, baseTag, headOid }) {
    const compareUrl = `${repoUrl}/compare/${baseTag}...${headOid}`;

    // Accept raw subject strings (unit tests, and any caller without bodies) or {subject, body}.
    const normalized = commits.map((c) => (typeof c === "string" ? { subject: c, body: "" } : c));
    const changes = normalized.filter(({ subject }) => !RELEASE_COMMIT_RE.test(subject));
    const buckets = new Map(GROUPS.map(([t]) => [t, []]));
    const other = [];
    let hiddenCount = 0;
    let nextId = 0;
    for (const { subject, body } of changes) {
        const entry = parseSubject(subject);
        // Breaking can be declared by `!` in the subject (parsed above) or a `BREAKING CHANGE:` body footer.
        entry.breaking = entry.breaking || BREAKING_RE.test(body ?? "");
        if (entry.type !== null && HIDDEN_TYPES.has(entry.type) && !entry.breaking) {
            hiddenCount += 1;
            continue;
        }
        // A stable id per commit, so the breaking callout and the type-section copy of the same
        // commit share it and the over-budget notice can count unique commits, not rendered lines.
        entry.id = nextId++;
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
    const heading = (text) => ({ text, id: null });
    const breaking = [...GROUPS.flatMap(([t]) => buckets.get(t)), ...other].filter((e) => e.breaking);
    const sections = [];
    if (breaking.length) {
        sections.push([heading("### ⚠️ Breaking changes"), ...renderSection(breaking, repoUrl)]);
    }
    for (const [type, sectionHeading] of GROUPS) {
        const entries = buckets.get(type);
        if (entries.length) {
            sections.push([heading(sectionHeading), ...renderSection(entries, repoUrl)]);
        }
    }
    if (other.length) {
        sections.push([heading("### Other"), ...renderSection(other, repoUrl)]);
    }

    // No blank separators — the `###` headings carry their own spacing. Each line is {text, id};
    // id is null for headings and shared between a commit's breaking callout and type-section copies.
    const lines = sections.flat();
    const full = [HEADER, ...lines.map((l) => l.text)].join("\n");
    if (codePoints(full) <= BUDGET) {
        return { ...result, markdown: full };
    }

    // Over budget: keep lines from the top until the next won't fit (reserving room for the notice,
    // measured at its longest in case everything remaining is dropped), then drop any trailing
    // heading left with no entries and note how many unique commits were left off. Counting commits
    // (not lines) means a breaking commit kept in the callout isn't reported "more" just because its
    // duplicate type-section line fell off the end.
    const totalCommits = new Set(lines.filter((l) => l.id !== null).map((l) => l.id)).size;
    const notice = (n) => `- …and ${n} more`;
    const reserve = 1 + codePoints(notice(totalCommits));
    const kept = [HEADER];
    const keptCommits = new Set();
    let used = codePoints(HEADER);
    for (const line of lines) {
        const cost = 1 + codePoints(line.text);
        if (used + cost + reserve > BUDGET) {
            break;
        }
        kept.push(line.text);
        used += cost;
        if (line.id !== null) {
            keptCommits.add(line.id);
        }
    }
    while (kept.length > 1 && kept[kept.length - 1].startsWith("### ")) {
        kept.pop();
    }
    const dropped = totalCommits - keptCommits.size;
    const markdown = dropped > 0 ? [...kept, notice(dropped)].join("\n") : kept.join("\n");
    return { ...result, markdown };
}

module.exports = { buildChangelog, expandCommit, parseSubject, escapeMarkdown, BUDGET };

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

    // Each record is `<sha>\x1f<subject>\x1f<body>\x1e`. Bodies span lines, so records are split
    // on the \x1e terminator and fields on \x1f; a record with an empty subject or body still counts.
    const commits = readFileSync(0, "utf8")
        .split("\x1e")
        .filter((record) => record.includes("\x1f"))
        .flatMap((record) => {
            const fields = record.split("\x1f");
            return expandCommit((fields[1] ?? "").trim(), fields.slice(2).join("\x1f"));
        });

    const result = buildChangelog(commits, { repoUrl, baseTag, headOid });
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
    console.log(`${result.count} entries (${result.visibleCount} visible, ${result.hiddenCount} internal)`);
    console.log(result.markdown);
}

if (require.main === module) {
    main();
}
