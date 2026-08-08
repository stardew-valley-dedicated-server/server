// WorktreeCreate hook: places Claude-created worktrees at ../worktrees/<name>
// (project convention, see .claude/rules/universal/git-workflow.md) instead of
// the default .claude/worktrees/ inside the repo. Claude Code does not process
// .worktreeinclude when this hook is active, so the copy happens here.
// Contract (code.claude.com/docs/en/hooks#worktreecreate): stdin JSON {name},
// stdout = worktree path, non-zero exit = creation failure.
import { execFileSync, execSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

const git = (...args) =>
  execFileSync("git", args, { encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] }).trim();

const input = JSON.parse(fs.readFileSync(0, "utf8"));
const rawName = String(input.name ?? "").trim();
if (!rawName) {
  console.error("worktree-create hook: no worktree name in hook input");
  process.exit(1);
}

// Resolve the main checkout even when invoked from inside another worktree.
const mainCheckout = path.dirname(git("rev-parse", "--path-format=absolute", "--git-common-dir"));
const dirName = rawName.replace(/[^A-Za-z0-9._-]/g, "-");
const dest = path.join(path.dirname(mainCheckout), "worktrees", dirName);

const worktrees = git("worktree", "list", "--porcelain")
  .split(/\r?\n\r?\n/)
  .map((block) => ({
    path: block.match(/^worktree (.+)$/m)?.[1],
    branch: block.match(/^branch refs\/heads\/(.+)$/m)?.[1],
  }))
  .filter((w) => w.path);

const existing = worktrees.find((w) => path.resolve(w.path) === path.resolve(dest));
if (existing) {
  // Distinct names can flatten to the same directory (fix/a-b vs fix/a/b) —
  // only reuse a worktree that is on the requested branch.
  if (existing.branch !== rawName) {
    console.error(
      `worktree-create hook: ${dest} is on branch '${existing.branch}', not '${rawName}' — pick a name that flattens to a different directory`,
    );
    process.exit(1);
  }
  console.log(dest); // reuse, mirroring `--worktree` semantics for an existing name
  process.exit(0);
}
if (fs.existsSync(dest)) {
  console.error(`worktree-create hook: ${dest} exists but is not a registered worktree`);
  process.exit(1);
}

// Check out the branch if it already exists (e.g. worktree removed, branch
// kept for a PR); -b would reject it.
let branchExists = true;
try {
  execFileSync("git", ["rev-parse", "--verify", "--quiet", `refs/heads/${rawName}`], { stdio: "ignore" });
} catch {
  branchExists = false;
}
execFileSync(
  "git",
  branchExists ? ["worktree", "add", dest, rawName] : ["worktree", "add", "-b", rawName, dest, "master"],
  { cwd: mainCheckout, stdio: ["ignore", "ignore", "inherit"] },
);

// .worktreeinclude entries are plain repo-relative paths, one per line.
const includeFile = path.join(mainCheckout, ".worktreeinclude");
if (fs.existsSync(includeFile)) {
  for (const line of fs.readFileSync(includeFile, "utf8").split(/\r?\n/)) {
    const rel = line.trim();
    if (!rel || rel.startsWith("#")) continue;
    const src = path.join(mainCheckout, rel);
    if (fs.existsSync(src)) fs.copyFileSync(src, path.join(dest, rel));
  }
}

try {
  execSync("npm ci", { cwd: dest, stdio: ["ignore", "ignore", "pipe"] });
} catch (e) {
  const stderr = e.stderr?.toString() ?? "";
  process.stderr.write(stderr);
  // On Windows `npm ci` exits non-zero at the `lefthook install` postinstall
  // (core.hooksPath already points at the main repo's hooks) with node_modules
  // complete and the hooks still firing — tolerate only that; any other
  // failure means an unprovisioned worktree, so fail creation (the worktree
  // stays on disk and a retry with the same name reuses it).
  if (!/lefthook install/.test(stderr)) {
    console.error("worktree-create hook: npm ci failed");
    process.exit(1);
  }
}

console.log(dest);
