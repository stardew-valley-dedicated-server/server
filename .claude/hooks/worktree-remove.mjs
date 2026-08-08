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

// The input field carrying the path is undocumented; try likely names first,
// then any string value — but never ambient fields (`cwd` may be a live
// worktree that is not the removal target). Only paths inside the convention
// container qualify; anything else exits 1, leaving the worktree in place.
const ambient = new Set(["cwd", "session_id", "transcript_path", "hook_event_name"]);
const candidates = [
  input.worktree_path,
  input.path,
  input.worktreePath,
  ...Object.entries(input).flatMap(([k, v]) => (ambient.has(k) ? [] : [v])),
].filter((v) => typeof v === "string" && v.trim());
const target = candidates
  .map((v) => path.resolve(v.trim()))
  .find((p) => (p + path.sep).startsWith(container));
if (!target) {
  console.error(`worktree-remove hook: no path inside ${container} in hook input: ${JSON.stringify(input)}`);
  process.exit(1);
}

try {
  execFileSync("git", ["worktree", "remove", "--force", target], {
    cwd: mainCheckout,
    stdio: ["ignore", "ignore", "inherit"],
  });
} catch {
  // Windows "Filename too long" fallback (deep node_modules paths).
  fs.rmSync(target, { recursive: true, force: true, maxRetries: 3 });
  execFileSync("git", ["worktree", "prune"], { cwd: mainCheckout, stdio: "ignore" });
}
