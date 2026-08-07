# `!approve` — record a maintainer approval on your own PR

## Goal

Let the sole maintainer satisfy the `master` ruleset's 1-required-approval rule by
commenting a command on the PR, instead of merging with an admin bypass.

GitHub forbids approving your own pull request. With one maintainer that makes the
rule permanently unsatisfiable, so every merge goes through `gh pr merge --admin` —
and the bypass is total, not targeted. It also skips the five required status checks,
the strict up-to-date policy, thread resolution, and the merge queue. One
unsatisfiable rule is currently costing every other rule on the branch.

The command does not change *who* reviews. The maintainer still reads the diff; the
command records that decision under an identity GitHub will accept.

## Decisions to resolve before implementing

**1. Approving identity.** Does a `github-actions[bot]` review satisfy
`required_approving_review_count`? Unresolved, and it decides the shape.

The repo already permits it — `GET /repos/:owner/:repo/actions/permissions/workflow`
returns `can_approve_pull_request_reviews: true`, so the bot *can* submit the review.
Whether it *counts* is contested: GitHub's 2022 changelog says the setting exists to
stop "a user using Actions to satisfy the 'Required approvals' branch protection
requirement" (implying it counts when enabled), while community discussion #181487
reports approvals that submit but don't satisfy. Documentation does not settle it,
and the bot identity only exists inside Actions, so it cannot be tested from a
workstation.

- (a) Ship with `GITHUB_TOKEN`, test on a live PR, fall back if it doesn't count.
  Zero setup; the fallback is a two-line swap.
- (b) Go straight to a GitHub App + `actions/create-github-app-token`. Uncontested,
  ~10 min of browser setup (create app → private key → install → two secrets).

Note a GitHub App is **not** a second GitHub account — no login, no seat, no
separate inbox — so it satisfies the "one account" constraint that rules out a
machine user.

**2. Helper placement.** `isMaintainer` lives in `.github/scripts/e2e-pr-sticky.js`,
an E2E-sticky-comment module. Import it as-is (zero churn, slightly odd import), or
extract it plus its three tests into a shared `.github/scripts/maintainer.js` that
both workflows import? Importing as-is already keeps a single definition; extraction
is a naming improvement that touches a tested file and `e2e-tests.yml`.

**3. Command prefix.** The house convention is `/run-tests-e2e`
(`e2e-tests.yml:118`). Use `/approve` for consistency, or `!approve` — deliberately
unlike a test-run command, so a misfire is visually obvious?

**4. Deny UX.** Mirror the existing 👎-reaction-plus-maintainer-only-reply
(`e2e-tests.yml:296-306`), or stay silent on an unauthorized command?

## What the ruleset actually requires

`master` ruleset (id `1151039`), the relevant rules:

```
pull_request:
  required_approving_review_count: 1      ← the unsatisfiable rule
  dismiss_stale_reviews_on_push: true
  required_review_thread_resolution: true
  require_last_push_approval: false       ← a bot approval survives your own last push
  allowed_merge_methods: [squash, rebase]
required_status_checks (strict): Validate Build | Commits | Line Endings | PR Title | Formatting
merge_queue: SQUASH, ALLGREEN
bypass_actors: OrganizationAdmin (always) ← what `--admin` uses today
```

`require_last_push_approval: false` is load-bearing: with it true, the approval would
be void whenever the maintainer was the last pusher, which is always.

## Rejected alternatives

- **`required_approving_review_count: 0`.** Rejected by the maintainer: a human must
  approve the change. (Worth stating plainly that this is a process choice, not a
  security one — a bot that approves on command is functionally equivalent, and the
  command's value is the explicit sign-off moment and its audit trail.)
- **Widening `bypass_actors`.** That is the status quo being replaced.
- **A second GitHub account.** Explicitly ruled out. Superseded by the GitHub App
  option, which needs no account.
- **CodeRabbit auto-approve** (`request_changes_workflow: true` in
  `.coderabbit.yaml`, currently `false`). Approves once its own comments are resolved
  and pre-merge checks pass; as a GitHub App its review counts. Rejected because it
  puts a third party on the merge path and the approval is CodeRabbit's judgment,
  not the maintainer's.

## Reuse — the authorization chain already exists

`e2e-tests.yml` implements this exact command shape for `/run-tests-e2e`. The
approve workflow should reuse it rather than invent a second auth path:

```
issue_comment → if: issue.pull_request != null
              && sender.type != 'Bot'
              && startsWith(comment.body, '<command>')
   → gate job → helper.isMaintainer(sender.login)
              → repos.getCollaboratorPermissionLevel → admin|write
```

- `isMaintainer` — `.github/scripts/e2e-pr-sticky.js:429`. Tested at
  `e2e-pr-sticky.test.js:675` (admin/write allow, read/none deny), `:683` (404 =
  deny), `:690` (403 = rethrow, fail closed).
- **`author_association` must not be used.** Deliberately rejected in-tree, twice, as
  permission-blind — `e2e-pr-sticky.js:416` and `e2e-tests.yml:22`. `MEMBER` means
  org membership, not write access.
- **Use `github.event.sender`, not `github.event.comment.user`**
  (`e2e-tests.yml:106-109`). Identical for a `created` comment, but following the
  established idiom costs nothing.
- **Fail closed.** 404 denies; 403/5xx rethrows to a red job. Correct posture for an
  auth gate, and doubly so for one that grants merge rights.

## Implementation sketch

One new workflow, `.github/workflows/approve-pr.yml`, matching `label-pr.yml`'s style
(top-level `permissions: {}`, per-job grants, no third-party action to SHA-pin):

1. `on: issue_comment: types: [created]`.
2. Job `if:` — PR comment, non-bot sender, command prefix match.
3. Permissions: `pull-requests: write`, `contents: read` (the helper checkout).
4. Sparse-checkout `.github/scripts/` from the default branch, as `e2e-tests.yml:134`
   does, so `github-script` can `require()` the helper.
5. `isMaintainer(sender.login)` → deny path per decision 4.
6. `pulls.createReview({ event: 'APPROVE', body: 'Approved on behalf of @<actor>' })`.
7. 👍-react the triggering comment on success, so the command visibly took.

## Constraints to design around

- **`issue_comment` always runs the default branch's copy of the workflow**
  (`GITHUB_REF` = default branch, `GITHUB_SHA` = its last commit; already documented
  at `e2e-tests.yml:19-20`). Two consequences: a PR cannot alter the approver that
  gates it — a real security property — and **the workflow cannot approve its own
  introducing PR**, so landing it needs one final `--admin` merge.
- **`dismiss_stale_reviews_on_push: true`** drops the approval on every subsequent
  push. Re-issue the command as the last action before merging.
- **`required_review_thread_resolution: true`** is untouched; CodeRabbit threads
  still need resolving by hand.
- **Public repo.** An ungated command would let any passer-by approve. This is why
  reuse of `isMaintainer` is mandatory, not stylistic.

## Post-conditions (runtime gates, not static checks)

1. On a live PR, the command from `@JulianVallee` produces an approving review — and
   the PR's mergeability flips such that a **non-admin** `gh pr merge` succeeds. This
   is the gate that answers decision 1; a submitted review that leaves the PR still
   blocked means fall back to the GitHub App.
2. The same command from a non-maintainer account produces no approval.
3. A push after approval dismisses it (confirms `dismiss_stale_reviews_on_push`
   behaves as read), and re-issuing restores it.
4. A full merge completes through the merge queue with all five required checks
   green — i.e. `--admin` is no longer needed for a normal merge.
