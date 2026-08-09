# Stage git files explicitly by path — never `git add .`

Project-specific git rules; generic git knowledge is assumed.

## Staging

- Never use `git add .` or `git add -A`. Stage files explicitly by path.
- Verify ignore status with `git check-ignore -v --no-index <path>` (without `--no-index` it reports nothing for tracked paths); parent patterns (e.g. `**/bin`) affect nested files. Tracked files inside an ignored directory: stage with `-f` or add a negation pattern.
- `git commit` commits the **entire index**, not just what you staged. Run `git diff --cached --name-only` immediately before committing; `git restore --staged` any extras. Recovery for a bad commit: `git reset --soft HEAD~1` + re-stage — safe only while unpushed.

## Chained PRs

When a child PR depends on a parent PR, after the parent merges:

```bash
gh pr edit <child-num> --base master
git checkout <child-branch> && git rebase master && git push --force-with-lease
sleep 2 && gh pr merge <child-num> --squash --admin   # sleep: GitHub reports "not mergeable" right after a force-push
```

## Commit messages

Conventional commits, enforced by commitlint (`config-conventional`): body capped at 100 chars/line. Wrap body lines (use `git commit -F <file>`).

No `Co-Authored-By` trailer on commits — same as the PR rule below. This deliberately overrides the Claude Code default of appending one.

## PR Descriptions

Bullet points of changes. No co-author attributions.

## Worktrees

Worktrees live at `../worktrees/<name>` — never inside the repo. Create them via EnterWorktree: the `WorktreeCreate` hook (`.claude/hooks/worktree-create.mjs`) places them there, branches `<name>` from `master` (so name it like a branch: `fix/...`, `feat/...`), copies the `.worktreeinclude` files, and runs `npm ci`. Don't hand-roll `git worktree add` — if the hook path is ever unavailable, replicate those steps yourself. `ExitWorktree` can leave but not remove a worktree mid-session; clean up with `git worktree remove --force "../worktrees/<name>"` (deletes uncommitted changes; keep the branch if a PR depends on it). On Windows `Filename too long`: `powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '<abs-path>' -Recurse -Force"` then `git worktree prune`.
