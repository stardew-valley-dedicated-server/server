# Stage git files explicitly by path — never `git add .`

Project-specific git rules; generic git knowledge is assumed.

## Staging

- Never use `git add .` or `git add -A`. Stage files explicitly by path.
- Verify a path's ignore status with `git check-ignore -v <path>` before assuming a file is ignored. Parent directory patterns (e.g., `**/bin`) affect nested files.
- For tracked files that now sit inside an ignored directory, either stage with `-f` or fix `.gitignore` with a negation pattern (e.g., `!docker/rootfs/opt/base/bin/`).
- `git add <paths>` stages *your* files, but `git commit` commits the **entire index** — which may already hold changes you didn't stage (pre-staged by the user or a prior step). Always run `git diff --cached --name-only` and confirm it shows ONLY your intended files immediately before committing. If it lists extras, `git restore --staged <those>` first. (This session a commit swept in two pre-staged unrelated files — `CIRenderer.cs`, `test-broker-invariants.md` — that were already in the index; fixing it needed a `git reset --soft HEAD~1` + re-stage. The soft-reset is safe only while the commit is unpushed.)

## Chained PRs

When a child PR depends on a parent PR, after the parent merges:

```bash
gh pr edit <child-num> --base master
git checkout <child-branch> && git rebase master && git push --force-with-lease
sleep 2 && gh pr merge <child-num> --squash --admin
```

The `sleep 2` before `gh pr merge` avoids GitHub returning "not mergeable" right after a force-push (sync delay).

## Commit messages

Conventional commits, enforced by a commitlint hook that extends `config-conventional` — so the body is capped at **100 chars/line** (an inherited default not written in `commitlint.config`). Wrap body lines (use `git commit -F <file>`) or the hook rejects the commit.

## PR Descriptions

Bullet points of changes. No co-author attributions.

## Worktrees

A fresh worktree is a clean checkout, so two gitignored things from the main checkout need setting up:

```bash
git worktree add -b <branch> "../server-worktrees/<name>" master
cp .env .env.test "../server-worktrees/<name>/"   # build + tests; skip if created via `claude --worktree` (.worktreeinclude handles it)
cd "../server-worktrees/<name>" && npm ci          # commitlint hook needs node_modules; per-worktree, never symlink the main repo's
git worktree remove --force "../server-worktrees/<name>"   # cleanup; keep the branch if a PR depends on it
```
