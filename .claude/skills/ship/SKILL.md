---
name: ship
description: Approves and merges a ready pull request the way this repo does it. Brings it up to date with master, comments `!approve`, and enables auto-merge. Runs only when the user types /ship.
argument-hint: [PR number] [rebase]
disable-model-invocation: true
---

# Ship a pull request

PR number: from `$ARGUMENTS`, else `gh pr view --json number -q .number`.

## 1. Stop if it isn't ready

Stop and report if any of these is true:

- The PR isn't open, or it's a draft.
- A review thread is unresolved (count them with the query in the `pr-bots` skill). Suggest `/pr-bots`.
- A required check failed (`gh pr checks <n> --required`). Pending checks are fine; auto-merge waits for them.
- You are on the PR's branch and it has uncommitted changes or unpushed commits.

## 2. Bring it up to date

If `gh pr view <n> --json mergeStateStatus` shows `BEHIND`: `gh pr update-branch <n>`, or `gh pr update-branch <n> --rebase` with `rebase` in `$ARGUMENTS`.

## 3. Approve and merge

1. `gh pr comment <n> --body '!approve'`
2. Pick the merge command per Merging in `.claude/rules/universal/git-workflow.md`:
   - One change: `gh pr merge <n> --squash --auto`. The PR title becomes the changelog line, so check it meets the subject rule first.
   - Several changes: `gh pr merge <n> --squash --auto --body-file <file>`, one `type(scope): subject` per line. If the user hasn't approved these lines yet, show them and wait.
   - `rebase` in `$ARGUMENTS`: `gh pr merge <n> --rebase --auto`, after checking every commit subject meets the subject rule.
3. Never add `--admin` unless the user asked for it in the same message.
4. Wait with a Monitor loop until `gh pr view <n> --json reviewDecision -q .reviewDecision` prints `APPROVED`. The approval workflow withdraws its review if the branch changed while it ran, and auto-merge then waits without any sign. If no approval shows up within a few minutes, find this PR's latest run in `gh run list --workflow approve-pr.yml --limit 20` (the title is the PR title; every PR comment starts a run, so skip the `skipped` ones) and report why it didn't approve.

Don't wait for the merge. Report that auto-merge is on; the user says when it has merged. If other open PRs target this branch (`gh pr list --base '<branch>'`), name them: they need the Chained PRs steps from `.claude/rules/universal/git-workflow.md` once this one merges.
