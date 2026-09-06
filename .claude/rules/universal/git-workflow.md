# Git workflow

## Staging

- Stage by explicit path. Never `git add .` / `-A`.
- `git diff --cached --name-only` before every commit; `git restore --staged` extras.
- Ignore check: `git check-ignore -v --no-index <path>`. Tracked file in ignored dir: `git add -f`.

## Remote

- SSH only. Never HTTPS.
- SSH unavailable (no key, no agent, `Permission denied`): stop, ask the user to enable it.

## Merging

- Branch must be up to date with `master`, approved, green. Behind: use the PR's **Update branch** button.
- Approve: comment `!approve` on the PR (`gh pr review --approve` fails for the author).
- Merge: `gh pr merge <num> --squash [--auto]`.

## Chained PRs

After the parent merges (it was squashed, so plain `git rebase master` replays its commits):

```bash
gh pr edit <child-num> --base master
git checkout <child-branch> && git fetch origin master
git rebase --onto origin/master <old-parent-head> && git push --force-with-lease
gh pr merge <child-num> --squash --auto
```

## Rebasing

- Build before every `git rebase --continue` and once at the end. Auto-merged hunks can break without conflict markers.

## Commits and PRs

- Conventional commits (commitlint), body lines max 100 chars, `git commit -F <file>`.
- No `Co-Authored-By` trailer. No co-author attribution in PRs.
- PR description: bullet points of changes.

## Bot review threads

- Every CodeRabbit/Greptile thread ends resolved, never just outdated.
- Applied: push, confirm resolved; else reply in-thread naming the commit, resolve.
- Rejected: reply in-thread with reason and citation, resolve. Verify per `bot-review-blind-spots.md`.
- Resolve: `gh api graphql -f query='mutation { resolveReviewThread(input:{threadId:"<PRRT_…>"}) { thread { isResolved } } }'` (ids from `pullRequest.reviewThreads`).
- Reply: `gh api repos/<owner>/<repo>/pulls/<num>/comments/<root-comment-id>/replies -f body=…`.

## Worktrees

- Location: `../worktrees/<name>`, never inside the repo. Create via EnterWorktree (hook branches `<name>` from `master`, copies `.worktreeinclude`, runs `npm ci`). Name like a branch: `fix/...`, `feat/...`.
- No `decompiled/` in worktrees. Read it from the main checkout (`git worktree list`, first entry). Never link it in.
- Remove: `git worktree remove --force "../worktrees/<name>"`. Windows `Filename too long`: `powershell.exe -NoProfile -Command "Remove-Item -LiteralPath '<abs-path>' -Recurse -Force"`, then `git worktree prune`.
