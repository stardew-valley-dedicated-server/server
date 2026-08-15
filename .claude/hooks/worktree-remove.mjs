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
const commonDir = git("rev-parse", "--path-format=absolute", "--git-common-dir");
const mainCheckout = path.dirname(commonDir);
// Windows refuses to delete any process's current directory — make sure this
// process never holds a cwd lock inside the worktree being removed.
process.chdir(mainCheckout);
const container = path.join(path.dirname(mainCheckout), "worktrees") + path.sep;
const registered = new Set(
  git("worktree", "list", "--porcelain")
    .split(/\r?\n/)
    .filter((l) => l.startsWith("worktree "))
    .map((l) => path.resolve(l.slice(9))),
);

// A directory git has already deregistered still proves it was a worktree of
// THIS repo when its `.git` is a file whose gitdir points into our admin area
// (.git/worktrees/<id>) — the safety bar for deleting anything the
// registration check can't vouch for.
const adminRoot = path.join(commonDir, "worktrees") + path.sep;
const isWorktreeStub = (p) => {
  try {
    const dotGit = path.join(p, ".git");
    if (!fs.statSync(dotGit).isFile()) return false;
    const m = fs.readFileSync(dotGit, "utf8").match(/^gitdir:\s*(.*)$/m);
    return !!m && (path.resolve(p, m[1].trim()) + path.sep).startsWith(adminRoot);
  } catch {
    return false;
  }
};

// The input field carrying the path is undocumented; try likely names first,
// then any string value — but never ambient fields (`cwd` may be a live
// worktree that is not the removal target). Only worktrees inside the
// convention container qualify — registered ones, or deregistered leftovers
// that isWorktreeStub can vouch for; anything else exits 1, leaving the
// directory in place.
const ambient = new Set(["cwd", "session_id", "transcript_path", "hook_event_name"]);
const candidates = [
  input.worktree_path,
  input.path,
  input.worktreePath,
  ...Object.entries(input).flatMap(([k, v]) => (ambient.has(k) ? [] : [v])),
].filter((v) => typeof v === "string" && v.trim());
const target = candidates
  .map((v) => path.resolve(v.trim()))
  .find(
    (p) =>
      (p + path.sep).startsWith(container) &&
      p + path.sep !== container &&
      (registered.has(p) || isWorktreeStub(p)),
  );
if (!target) {
  console.error(
    `worktree-remove hook: no removable worktree inside ${container} in hook input: ${JSON.stringify(input)}` +
      ` — if a leftover directory remains, delete it manually and run \`git worktree prune\`.`,
  );
  process.exit(1);
}

const prune = () => execFileSync("git", ["worktree", "prune"], { cwd: mainCheckout, stdio: "ignore" });

if (!registered.has(target)) {
  // Deregistered but still on disk (a prior removal dropped the registration,
  // then failed deleting the files): filesystem delete + prune stale admin dirs.
  fs.rmSync(target, { recursive: true, force: true, maxRetries: 3 });
  prune();
} else if (!fs.existsSync(target)) {
  // Registered but already gone from disk: only the stale registration is left.
  prune();
} else {
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
    prune();
  }
}
