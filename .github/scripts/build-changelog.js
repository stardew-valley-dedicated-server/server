// Turns a build's commit range into the changelog we post to Discord: one flat "Changes" list,
// ordered the way release-please orders its release notes, trimmed to fit a Discord embed. Used
// by .github/actions/build-changelog; the core function is exported so `npm test` can call it.
//
// How the action runs it: it pipes `git log --first-parent --format='%H%x1f%s' BASE..HEAD` in on
// stdin and sets the BASE_TAG, HEAD_OID and REPO_URL env vars. We write markdown / count /
// visible-count / hidden-count / compare-url to $GITHUB_OUTPUT (or print them to stdout locally).

// The heading above the list. One flat list, no per-type sub-headings.
const HEADER = "**Changes**";

// The commit types we list individually, in the order release-please uses, so a post reads like
// the GitHub release page. The "hidden" types below are counted but not listed. Anything else (an
// unknown type, or a subject that isn't a conventional commit) is listed after these, as-is — we
// never drop a commit just because we couldn't parse it.
const VISIBLE_TYPES = ["feat", "fix", "perf", "revert", "docs"];
const HIDDEN_TYPES = new Set(["style", "chore", "refactor", "test", "build", "ci"]);

// We measure length in code points (what Discord counts), not JS string length. 3500 leaves room
// under Discord's 4096-char description limit for the "Use it" block the caller appends, and under
// the 6000-char limit for the whole embed.
const BUDGET = 3500;

const CONVENTIONAL_RE = /^([a-z]+)(\([^()]*\))?(!)?: \S/i;
const PR_SUFFIX_RE = /\s*\(#(\d+)\)$/;
// release-please's own "release" commit (e.g. "chore(master): release sdvd-server 1.4.1") is what
// a release tag points at, so it falls inside its own range. It's release plumbing, not a real
// change, so we drop it from every count — otherwise it would inflate the "+N internal changes" line.
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
 * @returns {{text: string, type: string|null, breaking: boolean, pr: number|null}}
 *   `text` is the subject with its trailing `(#N)` removed; `type` is the lowercased
 *   conventional-commit type, or null if the subject isn't a conventional commit.
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

/** @returns {string} one `- …` bullet. A breaking change gets a ⚠ prefix; the PR link goes on the end. */
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
 *   `markdown` is never longer than BUDGET code points. If the full list wouldn't fit, we drop
 *   entries from the end (always between whole lines) and say how many we dropped on the bottom line.
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

    // The last line of the changelog. It holds up to three pieces, joined with " · ": an "…and N
    // more" note when we had to drop entries, the "+N internal changes" count, and always the diff
    // link. The " · " separator matches the "· #PR" that ends each entry above, so it all reads alike.
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

    // Too long to fit: add whole entry lines until we'd run out of room for the bottom line —
    // measured at its longest, in case every remaining entry ends up dropped — then note how many
    // we left off.
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
