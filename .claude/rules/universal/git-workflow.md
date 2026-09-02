# Stage git files explicitly by path — never `git add .`

Project-specific git rules; generic git knowledge is assumed.

## Staging

- Never use `git add .` or `git add -A`. Stage files explicitly by path.
- Verify ignore status with `git check-ignore -v --no-index <path>` (without `--no-index` it reports nothing for tracked paths); parent patterns (e.g. `**/bin`) affect nested files. Tracked files inside an ignored directory: stage with `-f` or add a negation pattern.
- `git commit` commits the **entire index**, not just what you staged. Run `git diff --cached --name-only` immediately before committing; `git restore --staged` any extras. Recovery for a bad commit: `git reset --soft HEAD~1` + re-stage — safe only while unpushed.

## Merging

- `master` enforces strict "up to date before merge". A PR merges once its branch is up to date with `master`, it is approved, and its required checks pass. Update a behind branch (the PR's **Update branch** button) so checks re-run against the tip, then merge with `gh pr merge <num> --squash` — or `--squash --auto` to merge automatically once checks pass.
- The PR author self-approves by commenting `!approve` on the PR (repo automation posts the approving review); `gh pr review --approve` fails for the author's own token.

## Chained PRs

When a child PR depends on a parent PR, after the parent merges:

```bash
gh pr edit <child-num> --base master
git checkout <child-branch> && git fetch origin master
git rebase --onto origin/master <old-parent-head> && git push --force-with-lease
gh pr merge <child-num> --squash --auto
```

Rebase with `--onto origin/master <old-parent-head>` (the parent branch's final commit), not plain `git rebase master`: the parent was squash-merged, so a plain rebase replays its now-squashed commits and conflicts or duplicates them.

## Rebasing

- After resolving a commit's conflicts, **build before `git rebase --continue`**. Clearing the visible `<<<<<<<` markers is not "done" — a clean auto-merge can leave non-compiling code no marker flags, when the base branch refactored a type your commit uses in an *un-conflicted* region (e.g. a master commit turned a string setting into a typed enum; three conflicts resolved cleanly, then four `CS0029`/`CS1503` errors surfaced only on build, all in auto-merged hunks). An uncaught break gets baked into the rebased commit and propagates to every commit replayed on top.
- Build once more at the end: later commits replay on the changed base, so one that applied cleanly against the old base can still be broken by the new one.

## Commit messages

Conventional commits, enforced by commitlint (`config-conventional`): body capped at 100 chars/line. Wrap body lines (use `git commit -F <file>`).

No `Co-Authored-By` trailer on commits — same as the PR rule below. This deliberately overrides the Claude Code default of appending one.

## PR Descriptions

Bullet points of changes. No co-author attributions.

## Worktrees

A worktree has **no `decompiled/`** — it's gitignored (~1 GB) and `.worktreeinclude` copies files, not directories. Read decompiled sources from the main checkout (`git worktree list` lists it first); every `decompiled/...` citation in rules and plans resolves against that checkout, not your worktree.

Don't "solve" this by linking it in: `git worktree remove --force` deletes *through* a junction, destroying the main checkout's copy (gitignored, so unrecoverable), and ripgrep doesn't follow one — `Grep` would return silent zero-match results while `Read` on the same path works.

Worktrees live at `../worktrees/<name>` — never inside the repo. Create them via EnterWorktree: the `WorktreeCreate` hook (`.claude/hooks/worktree-create.mjs`) places them there, branches `<name>` from `master` (so name it like a branch: `fix/...`, `feat/...`), copies the `.worktreeinclude` files, and runs `npm ci`. Don't hand-roll `git worktree add` — if the hook path is ever unavailable, replicate those steps yourself. `ExitWorktree` can leave but not remove a worktree mid-session; clean up with `git worktree remove --force "../worktrees/<name>"` (deletes uncommitted changes; keep the branch if a PR depends on it). On Windows `Filename too long`: `powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '<abs-path>' -Recurse -Force"` then `git worktree prune`.
