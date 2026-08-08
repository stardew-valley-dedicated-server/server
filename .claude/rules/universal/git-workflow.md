# Stage git files explicitly by path — never `git add .`

Project-specific git rules; generic git knowledge is assumed.

## Staging

- Never use `git add .` or `git add -A`. Stage files explicitly by path.
- Verify ignore status with `git check-ignore -v <path>`; parent patterns (e.g. `**/bin`) affect nested files. Tracked files inside an ignored directory: stage with `-f` or add a negation pattern.
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

## PR Descriptions

Bullet points of changes. No co-author attributions.

## Worktrees

Run these from the main checkout — the relative paths resolve wrong from inside another worktree (use absolute paths there). A fresh worktree is a clean checkout, so the gitignored things from the main checkout need setting up:

```bash
git worktree add -b <branch> "../worktrees/<name>" master
cp .env .env.test .sdvd_runner_key "../worktrees/<name>/"   # skip if created via `claude --worktree` (.worktreeinclude)
cd "../worktrees/<name>" && npm ci   # commitlint hook; never symlink the main repo's node_modules
git worktree remove --force "../worktrees/<name>"   # cleanup; keep the branch if a PR depends on it
```

`git worktree remove` can fail on Windows with `Filename too long` (deep `node_modules`/build paths). Fall back to `powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '<abs-path>' -Recurse -Force"` then `git worktree prune`.
