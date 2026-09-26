// Searches the Claude Code transcripts of this repository: the main checkout and
// every worktree under ../worktrees. Usage is documented in ../SKILL.md.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const USAGE = "usage: recall.mjs <words...> [--session <id prefix>] [--all] [--limit N] [--wide]";
const args = process.argv.slice(2);
const opts = { all: false, wide: false, limit: undefined, session: "" };
const terms = [];
for (let i = 0; i < args.length; i++) {
  const arg = args[i];
  if (arg === "--all") opts.all = true;
  else if (arg === "--wide") opts.wide = true;
  else if (arg === "--limit") opts.limit = Number(args[++i]);
  else if (arg === "--session") opts.session = args[++i] ?? "";
  else terms.push(arg);
}
if ((terms.length === 0 && !opts.session) || (opts.limit !== undefined && !(opts.limit > 0))) {
  console.error(USAGE);
  process.exit(2);
}
// --session lists the whole session unless --limit is given.
const limit = opts.limit ?? (opts.session ? Number.POSITIVE_INFINITY : 30);

// Claude Code names each transcript folder after the working directory, with
// every non-alphanumeric character replaced by "-".
const encode = (p) => p.replace(/[^A-Za-z0-9]/g, "-");
const projectsDir = path.join(process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), ".claude"), "projects");
let mainKey = "";
let worktreePrefix = "";
if (!opts.all) {
  const commonDir = execFileSync("git", ["rev-parse", "--path-format=absolute", "--git-common-dir"], {
    encoding: "utf8",
  }).trim();
  const main = path.dirname(commonDir);
  mainKey = encode(main);
  worktreePrefix = `${encode(path.join(path.dirname(main), "worktrees"))}-`;
}
const inScope = (dir) => opts.all || dir === mainKey || dir.startsWith(worktreePrefix);
const where = (dir) => {
  if (dir === mainKey) return "main";
  return worktreePrefix && dir.startsWith(worktreePrefix) ? dir.slice(worktreePrefix.length) : dir;
};

const literal = (s) => new RegExp(s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "i");
const patterns = terms.map(literal);
const matchesAll = (text) => patterns.every((re) => re.test(text));
// The raw JSONL escapes `\` and `"`, so the cheap pre-parse filter looks for the escaped form.
const rawPatterns = terms.map((t) => literal(JSON.stringify(t).slice(1, -1)));
const rawMatch = (line) =>
  terms.length ? rawPatterns.every((re) => re.test(line)) : /"type":"user"|"queued_command"/.test(line);
const noise = /^\s*(<task-notification|<local-command|Caveat: The messages below|\/recall\b)/;
const width = opts.wide ? 700 : 220;

const pad = (n) => String(n).padStart(2, "0");
const stamp = (iso) => {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "????-??-?? ??:??";
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

// A slash command is stored as tagged parts; show it the way it was typed.
const typed = (text) => {
  const name = /<command-name>(.*?)<\/command-name>/s.exec(text)?.[1];
  if (!name) return text;
  const args = /<command-args>(.*?)<\/command-args>/s.exec(text)?.[1] ?? "";
  return `${name} ${args}`.trim();
};

// The user's prompts, Claude's text, and tool calls. Skips tool output, and earlier
// recall searches, which always contain their own search words.
const messages = (entry) => {
  const content = entry.message?.content;
  // A prompt sent while Claude is mid-turn is stored only as this attachment.
  if (entry.type === "attachment" && entry.attachment?.type === "queued_command") {
    return [["you", typed(entry.attachment.prompt)]];
  }
  if (entry.type === "user" && !entry.isMeta) {
    if (typeof content === "string") return [["you", typed(content)]];
    if (Array.isArray(content)) return content.filter((b) => b.type === "text").map((b) => ["you", typed(b.text)]);
  }
  if (entry.type === "assistant" && Array.isArray(content)) {
    return content.flatMap((b) => {
      if (b.type === "text") return [["claude", b.text]];
      if (b.type === "tool_use") {
        const input = b.input ?? {};
        if (input.skill === "recall" || (typeof input.command === "string" && input.command.includes("recall.mjs"))) {
          return [];
        }
        return [[b.name, input.command ?? Object.values(input).filter((v) => typeof v === "string").join(" ")]];
      }
      return [];
    });
  }
  return [];
};

const excerpt = (text) => {
  const flat = text.replace(/\s+/g, " ").trim();
  const hit = patterns.length ? flat.search(patterns[0]) : 0;
  const start = Math.max(0, hit - Math.floor(width / 3));
  const end = start + width;
  return `${start > 0 ? "..." : ""}${flat.slice(start, end)}${end < flat.length ? "..." : ""}`;
};

const current = process.env.CLAUDE_CODE_SESSION_ID;
const sessions = [];
for (const dir of fs.readdirSync(projectsDir).filter(inScope)) {
  const dirPath = path.join(projectsDir, dir);
  for (const file of fs.readdirSync(dirPath)) {
    const id = file.slice(0, -".jsonl".length);
    if (!file.endsWith(".jsonl") || id === current || (opts.session && !id.startsWith(opts.session))) continue;
    const session = { id, where: where(dir), title: "", matches: [] };
    for (const line of fs.readFileSync(path.join(dirPath, file), "utf8").split("\n")) {
      // Cheap filter on the raw line before parsing.
      const titleLine = line.includes('"ai-title"');
      if (!titleLine && !rawMatch(line)) continue;
      let entry;
      try {
        entry = JSON.parse(line);
      } catch {
        continue;
      }
      if (entry.type === "ai-title") {
        session.title = entry.aiTitle ?? session.title;
        continue;
      }
      for (const [who, text] of messages(entry)) {
        if (!text || noise.test(text)) continue;
        if (terms.length ? matchesAll(text) : who === "you") {
          session.matches.push({ at: entry.timestamp ?? "", who, text: excerpt(text) });
        }
      }
    }
    if (session.matches.length) sessions.push(session);
  }
}

if (sessions.length === 0) {
  console.log("No matches.");
  process.exit(0);
}

const latest = (s) => s.matches.at(-1).at;
sessions.sort((a, b) => latest(b).localeCompare(latest(a)));
const perSession = opts.session ? Number.POSITIVE_INFINITY : 5;
const total = sessions.reduce((n, s) => n + s.matches.length, 0);
let shown = 0;
for (const s of sessions) {
  if (shown >= limit) break;
  console.log(`\n${stamp(latest(s))}  ${s.id}  ${s.title || "(untitled)"}  [${s.where}]`);
  const rows = s.matches.slice(0, Math.min(perSession, limit - shown));
  for (const m of rows) console.log(`  ${stamp(m.at).slice(5)}  ${m.who.padEnd(6)}  ${m.text}`);
  shown += rows.length;
  if (s.matches.length > rows.length && !opts.session) {
    console.log(`  ${s.matches.length - rows.length} more: --session ${s.id.slice(0, 8)}`);
  }
}
const count = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const summary = `${count(total, "match", "matches")} in ${count(sessions.length, "session", "sessions")}`;
console.log(`\n${summary}${shown < total ? `, showing ${shown}` : ""}.`);
