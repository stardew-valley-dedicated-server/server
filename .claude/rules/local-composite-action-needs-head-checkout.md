---
paths:
  - ".github/**"
---

# A local `uses: ./…` action must exist in the checked-out tree — adding one reds in-flight `pull_request_target` PRs until they rebase

`actions/checkout` populates the workspace that a local `uses: ./…` reference resolves against. Under `pull_request_target` the workflow *definition* comes from the base branch but the checkout is the PR *head*, so a local composite action added on the base is absent from every PR head that predates it — the job fails with "Can't find 'action.yml' … Did you forget to run actions/checkout?" until that PR rebases.

**Why:** A retry composite action added under `.github/actions/` broke `Validate Formatting` on every open PR ("Can't find action.yml"), because those PR heads lacked the new file while the base workflow referenced it. This was misread as a structural flaw and triggered a full inline-rewrite detour; the actual fix was just rebasing the open PRs. New PRs branched from the updated base were never affected.

**How to apply:** When adding or renaming a local `./…` action referenced by a `pull_request_target` (or any head-checkout) workflow, expect every in-flight PR to go red at that step until it rebases — a transient, self-healing cost, not a bug to engineer around. If that cost is unacceptable, inline the logic into the `run:` block (workflow text always comes from the base ref) or reference the action at a fixed `owner/repo/path@ref` so the checked-out tree is irrelevant.
