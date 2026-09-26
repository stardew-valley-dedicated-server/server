---
name: review-rules
description: Audits the project's `.claude/` rules, `CLAUDE.md`, and skills against the shared gates and word budget in `standard.md`, proposing cuts, compressions, mechanizations, and fixes. Use when the user asks to review, prune, audit, or clean up rules, and after writing or materially rewriting a rule file.
argument-hint: [optional path/glob or focus hint]
allowed-tools: Read, Grep, Glob, Edit, Write, Bash(node .claude/skills/review-rules/check.mjs)
---

# Review Rules

Audit `CLAUDE.md`, `.claude/rules/**`, and `.claude/skills/*/SKILL.md` (the default scope) against [standard.md](standard.md). A rule that fails a gate is cut, mechanized, moved, merged, or rescoped — whichever its first failed gate calls for. This skill never adds rules; new ones go through `/extract-session-rules`.

Arguments (scope or focus filters; they override the default scope):

> $ARGUMENTS

Phases 1–3 are read-only. Nothing is edited before the user approves in phase 3.

## Procedure

### 1. Measure

Read `standard.md`, then run `node .claude/skills/review-rules/check.mjs`. Investigate each problem it reports and either propose a fix or dismiss it with evidence. The gates in phase 2 are the judgment it can't make.

### 2. Judge

Read every in-scope file. Walk each rule through the gates in order; the first failed gate decides its disposition:

| Failed gate | Disposition |
|---|---|
| 1 recurring or costly, 2 beyond model default | **[CUT]** |
| 3 not mechanizable | **[MECHANIZE]** — name the hook, lint, or check |
| 4 project-specific | **[MOVE]** to `~/.claude/CLAUDE.md` if it's a working preference worth keeping, else **[CUT]** |
| 5 not derivable | **[CUT]** if the code or docs already say it; **[MOVE]** to a code comment or `docs/` if they should |
| 6 not duplicated | **[MERGE]** into the surviving rule if it adds anything, else **[CUT]** |
| 7 placement | **[RESCOPE]**, or **[MOVE]** to a skill when it's a multi-step procedure |

A rule whose trigger (path, identifier, feature) no longer exists is [CUT]. A cut rule gets no other finding. Any other rule can additionally get:

- **[COMPRESS]** exceeds its cap or carries extra incidents, restated rationale, or narration — give the target word count.
- **[FIX]** format, broken link, missing path or identifier, `paths:` not matching where it fires, contradiction with another rule.
- **[INDEX]** `README.md` out of sync.

"Removing it might cause mistakes" is not a keep argument; a gate is. Back each finding with evidence that fits the claim:

- Existence (path, identifier, trigger) → grep or glob.
- Recurrence (gate 1) → one of the gate's three conditions shown by the rule's `**Why:**`, another rule, or `git log`; a single cited incident with no defect or stated cost fails.
- Mechanizable or derivable (gates 3, 5) → the config, code, or docs that would carry it.
- Duplication (gate 6) → the other rule's text.

Skills have no word cap (they load only when invoked), but cut restatements and check their format.

### 3. Propose

Open with a budget table: current, after proposal, cap. Then one line per finding, grouped by tag: `[TAG] path — evidence — words saved`. If a cap stays exceeded, say by how much and name further cuts that would reach it, weakest gate evidence first. Plain text, not pickers — their 4-option cap is too small. Wait for explicit approval.

```
|               | Now   | After | Cap   |
|---------------|-------|-------|-------|
| Always loaded | 9,734 | 2,310 | 2,500 |

[CUT] .claude/rules/universal/orthogonal-fields.md — gate 1: one incident, no stated cost — −291
[MECHANIZE] .claude/rules/universal/answer-then-stop.md — gate 3: `UserPromptSubmit` hook on a `Q:` prefix — −407
[COMPRESS] .claude/rules/host-automation.md — keep the invariants, cut the narration; 2,421 → 300 — −2,121

Reply with findings or tags to apply (e.g. "all CUT, and the host-automation COMPRESS"), or "none".
```

### 4. Apply

Make exactly the approved edits. Before renaming or deleting a rule or skill, grep the repo for its filename (for a skill, its directory name) and repair citations — or keep the name if a citing file is out of scope. Rerun `check.mjs` and report before/after, plus deferred items with a one-line reason.
