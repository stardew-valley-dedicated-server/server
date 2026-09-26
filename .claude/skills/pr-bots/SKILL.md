---
name: pr-bots
description: Handles CodeRabbit and Greptile review feedback on a pull request end to end. Collects unresolved threads and summary findings, verifies each against the code, fixes the valid ones in one batch, pushes once, then replies to and resolves every thread. Use when the user asks to review, check, answer, or resolve bot comments on a PR.
argument-hint: [PR number, default: the current branch's PR]
---

# Handle bot review comments

## 1. Find the PR and sync

- PR number: `$ARGUMENTS`, or `gh pr view --json number -q .number` for the current branch.
- Work on the PR's branch. Run `git fetch` and `git status -sb`. If the remote branch has commits you don't have (the user may have rebased on GitHub), get them with `git pull --rebase` before editing, and never force-push over them. A plain `git pull` would merge the old and the rebased commits.
- If a bot is still reviewing (`gh pr checks <n>` shows `CodeRabbit` or `Greptile Review` pending), wait for it with a Monitor loop before collecting.
- If the `CodeRabbit` check says "Review rate limited", it didn't review this push. Tell the user.

## 2. Collect

Unresolved inline threads:

```bash
gh api graphql --paginate -F owner='{owner}' -F name='{repo}' -F pr=<n> -f query='
  query($owner: String!, $name: String!, $pr: Int!, $endCursor: String) {
    repository(owner: $owner, name: $name) { pullRequest(number: $pr) {
      reviewThreads(first: 100, after: $endCursor) { pageInfo { hasNextPage endCursor }
        nodes { id isResolved isOutdated path line originalLine
        comments(first: 10) { nodes { databaseId author { login } body } } } } } } }'
```

- Findings outside the diff and nitpicks sit in the review bodies: `gh api repos/{owner}/{repo}/pulls/<n>/reviews --jq '.[] | {user: .user.login, body}'`.
- Greptile's summary and confidence score are a PR comment: `gh pr view <n> --json comments`.
- Skip resolved threads.

## 3. Verify and sort

Open the code for every finding. Bots can't see the gitignored `decompiled/` folder, so check there (in the main checkout; worktrees don't have it) before accepting a "doesn't exist" claim. Sort each finding into one group:

- **Apply**: real and inside the PR's scope.
- **Reject**: wrong or not worth doing; note the reason with a `file:line` or doc citation.
- **Decide**: a design choice only the user can make.

Report the result, one line per finding. If anything is in Decide, or the user asked to see the findings first, stop and wait.

## 4. Fix and push once

Apply all fixes, run the checks from the `finalize` skill's table for the touched files, commit (pick type and scope with the `commit` skill), and push once. If the user said not to push, stop after the commit and list the replies you would post.

## 5. Reply to and resolve every thread

After the push, wait for the bot checks to finish as in step 1, then run the step 2 query again. Threads a bot resolved itself need nothing more. For every thread still open:

- Applied: "Fixed in <short sha>.", plus one sentence if the fix differs from the suggestion.
- Rejected: the reason and the citation.
- Outdated and already fixed: name the commit that fixed it.
- Findings outside the diff have no thread: answer them all in one PR comment.
- Reply, using the `databaseId` of the thread's first comment: `gh api repos/{owner}/{repo}/pulls/<n>/comments/<databaseId>/replies -f body='...'`
- Resolve: `gh api graphql -f query='mutation { resolveReviewThread(input: {threadId: "<id>"}) { thread { isResolved } } }'`

Replies are plain English, one or two sentences, no em dashes.

Finish with the counts (applied, rejected, waiting on the user), the commit sha, and any thread still open.
