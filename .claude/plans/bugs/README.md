# Bug plans

One file per bug. Every plan opens with an H1 title followed by this header block, in this order:

```md
**Status:** validation | ready-to-implement | in-review | done
**Priority:** 1 (low) | 2 (medium) | 3 (high) | 4 (critical)
**GitHub Issue(s):** [#123](https://github.com/stardew-valley-dedicated-server/server/issues/123) | none
**Area:** server | tests | docker | steam-service | docs | ci
**Related:** [`sibling-plan.md`](./sibling-plan.md) (example); [PR #n](https://github.com/stardew-valley-dedicated-server/server/pull/n) | none
**Observed:** where and how often (run id, date, rate; "production report"; "not observed, found by reading")
**Next step:** the one action that moves the plan to its next status
**Notes:** optional; only for state the body does not already say
```

- **Status** — `validation`: needs a human to confirm the plan is correct and ready; `ready-to-implement`: root-caused, approach agreed; `in-review`: PR open; `done`: merged (delete the plan in the same change per `plan-discipline.md`).
- **Priority** — `4` data loss, security, the server wedges in production, or docs whose instructions cause one of those; `3` production-visible bug with a workaround; `2` test-harness reliability or contained server bug; `1` cosmetic, one-off, or triage-only.
- **GitHub Issue(s)** — absolute links, comma-separated.
- **Area** — comma-separated when a fix spans more than one.
- **Related** — sibling plans as relative markdown links; PRs as absolute GitHub links; do not repeat the GitHub issue here.
- **Observed** — evidence provenance, so flakes are comparable across plans. Run ids as `YYYY-MM-DDTHH-MM-SSZ_<sha>`.

## Body skeleton

Single-bug plans use these H2 sections, in this order; leave a section out rather than writing a placeholder:

1. `## Symptom` — what was observed, with the exact error or test name.
2. `## Root cause` — the mechanism, or what is known and what is not.
3. `## Fix` — the change, as steps when there is more than one.
4. `## Verification` — how the fix is proven (test, log marker, runtime check).

Further H2s (`## Out of scope`, `## Non-causes`, `## Open decisions`, `## Dead ends`) follow those four. Domain detail goes in H3s under the nearest of the four.

`docs-cleanup/` is an audit, not a single bug: its workstreams keep their Objective / Scope / finding-list layout and rank each finding Critical / High / Medium / Low. That per-finding severity maps to the plan priority as Critical → 4, High → 3, Medium → 2, Low → 1; a workstream's `Priority` is its worst finding.

Plans cite files and symbols, never line numbers (`.claude/rules/plans-cite-files-not-lines.md`).
