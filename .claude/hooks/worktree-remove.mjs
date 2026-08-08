// WorktreeRemove hook: cleans up worktrees that worktree-create.mjs placed at
// ../worktrees/<name>. Removes the worktree only, never its branch (a PR may
// depend on it — see .claude/rules/universal/git-workflow.md).
// Failures are logged by Claude Code in debug mode only and cannot block removal.
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

const git = (...args) =>
  execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] }).trim();

const input = JSON.parse(fs.readFileSync(0, "utf8"));
const mainCheckout = path.dirname(git("rev-parse", "--path-format=absolute", "--git-common-dir"));
const container = path.join(path.dirname(mainCheckout), "worktrees") + path.sep;
const registered = new Set(
  git("worktree", "list", "--porcelain")
    .split(/\r?\n/)
    .filter((l) => l.startsWith("worktree "))
    .map((l) => path.resolve(l.slice(9))),
);

// The input field carrying the path is undocumented; try likely names first,
// then any string value — but never ambient fields (`cwd` may be a live
// worktree that is not the removal target). Only registered worktrees inside
// the convention container qualify; anything else exits 1, leaving the
// worktree in place.
const ambient = new Set(["cwd", "session_id", "transcript_path", "hook_event_name"]);
const candidates = [
  input.worktree_path,
  input.path,
  input.worktreePath,
  ...Object.entries(input).flatMap(([k, v]) => (ambient.has(k) ? [] : [v])),
].filter((v) => typeof v === "string" && v.trim());
const target = candidates
  .map((v) => path.resolve(v.trim()))
  .find((p) => (p + path.sep).startsWith(container) && p + path.sep !== container && registered.has(p));
if (!target) {
  console.error(`worktree-remove hook: no registered worktree inside ${container} in hook input: ${JSON.stringify(input)}`);
  process.exit(1);
}

try {
  execFileSync("git", ["worktree", "remove", "--force", target], {
    cwd: mainCheckout,
    stdio: ["ignore", "ignore", "pipe"],
  });
} catch (e) {
  const stderr = e.stderr?.toString() ?? "";
  process.stderr.write(stderr);
  // Deep node_modules paths can exceed Windows path limits; anything else is a
  // real git failure and must not fall through to a blind recursive delete.
  if (!/too long/i.test(stderr)) process.exit(1);
  fs.rmSync(target, { recursive: true, force: true, maxRetries: 3 });
  execFileSync("git", ["worktree", "prune"], { cwd: mainCheckout, stdio: "ignore" });
}
