---
name: extract-session-rules
description: Reviews the current session for learnings that pass the shared gates and budget in `review-rules/standard.md`, and proposes them as rules, skill or `CLAUDE.md` edits, or mechanical checks. Use only when the user asks to extract, save, or capture session lessons.
argument-hint: [optional instructions for the review]
allowed-tools: Read, Grep, Glob, Write, Edit, Bash(node .claude/skills/review-rules/check.mjs), Bash(mkdir -p *)
---

# Extract Session Rules

Propose persistent `.claude/` changes for the rare session learning that clears every gate in [standard.md](../review-rules/standard.md). Most sessions produce nothing; say so and stop.

Arguments (focus or constraints; they override the defaults below):

> $ARGUMENTS

Steps 1–6 are read-only. Nothing is written before the user approves in step 6.

## Procedure

1. **Read `standard.md`, then collect candidates:** user corrections, gotchas that cost real time, non-default choices the user validated. Drop anything tied to the current task, branch, or an in-progress migration.
2. **Gate each one** in order, citing gates by number and name. Gate 1 (recurring or costly) needs one of its three conditions — the user says it happened before, an existing rule, plan, or commit from an earlier session shows the same failure, or the session shows a shipped defect or 30+ minutes lost; a first-time, cheap correction fails. A candidate failing gate 3 (not mechanizable) becomes a proposal for the check instead, naming the condition it detects and where it runs (hook, lint, test, or CI). Drop every other failure.
3. **Check overlap** in `.claude/rules/`, `CLAUDE.md`, and `.claude/skills/`. Fully covered → drop. Partly covered → propose an update that doesn't grow the file unless the budget has room. Contradicts an existing rule → propose replacing it, never adding both.
4. **Check the budget** with `node .claude/skills/review-rules/check.mjs`. If the addition would exceed a cap, the proposal must name cuts of at least equal size — each an existing rule plus the gate it fails — or it is dropped.
5. **Pick the target:** path-scoped rule (default), universal rule (gate 7 only), skill (multi-step procedure), `CLAUDE.md` (stable project fact), hook, lint, test, or check (gate 3), code comment (fact about one site).
6. **Propose** in plain text with IDs — not a picker, whose 4-option cap is too small: target, the one-line rule, gate evidence (especially gate 1), words added and cut. Wait for explicit approval.

   ```
   2 candidates:

   [R1] UPDATE .claude/rules/server-tps-headless.md — "Compare perf only against the parent commit at the same SERVER_TPS."
        Gate 1: a perf claim was compared against a plan's numbers at a different TPS, second time. +18 words, −18 from its Why.
   [R2] MECHANIZE .claude/settings.json — `ask` permission for `gh pr merge`; detects any merge, runs as a permission prompt.
        Gate 1: user says unapproved merges happened before. +0 rule words.

   Reply with IDs to apply (e.g. "R1"), or "none".
   ```
7. **Write** exactly the approved items, in the `standard.md` format, then rerun `check.mjs` and report files changed and the budget after.

## Guardrails

- Never self-invoke.
- A preference about how the user likes to work goes in their personal `~/.claude/CLAUDE.md` — suggest it there, not as a project rule.
