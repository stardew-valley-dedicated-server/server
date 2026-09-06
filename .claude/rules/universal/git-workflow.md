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
- Merge: `gh pr merge <num> --squash [--auto]`. Squash uses the PR title as the commit subject and drops the body, so the PR title is the changelog line.
- `--rebase` only when the PR is a series of independent player/admin-facing changes that each deserve their own line; then every commit subject must meet the subject rule below.

## Chained PRs

After the parent merges (squash or rebase-merge rewrote its commits, so plain `git rebase master` replays them):

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
- Subjects (PR title, commit subject) are changelog lines: lead with the player/admin-facing outcome ("festivals no longer kick players who moved their cabin"), not the mechanism, and say what kind of thing shipped ("embeddable live server status widget for web pages", not "status widget backed by /status").
- Commit body in plain English — what changed, why, what tests cover it — no unexplained in-house terms.
- A squashed PR is one changelog line, so it holds one change. A fix that belongs to a sibling feature still in review moves into that feature's PR instead of riding along.
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
