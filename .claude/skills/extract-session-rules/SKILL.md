---
name: extract-session-rules
description: Review the current session for durable, non-obvious learnings worth persisting to .claude/ (rules, skills, or CLAUDE.md edits). Use when the user asks to extract/save/capture session lessons, or at the end of a session that produced corrections or surprising findings.
argument-hint: [optional instructions for the review]
tools: Read, Grep, Glob, Write, Edit, Bash, AskUserQuestion
---

# Extract Session Rules

Review the current conversation for durable learnings that would otherwise be lost, and persist the ones that meet the bar into the project's `.claude/` folder as rules, skills, or CLAUDE.md edits.

**Empty output is the correct result for most sessions.** If nothing qualifies, say so and exit. Do not invent filler.

## Extra instructions

If the skill was invoked with arguments (e.g. `/extract-session-rules focus on test infra only`), treat them as constraints or focus hints that narrow or supplement the default procedure. Apply them throughout — they may restrict which candidates qualify, change scope decisions, or otherwise shape any step of the review. Extra instructions take precedence over the defaults in this file where they conflict.

## When to extract

A candidate qualifies only if ALL of these hold:

- **Non-obvious** — not derivable from reading the code or running `git log`.
- **Future-applicable** — will matter in future work, not just the current task.
- **Earned** — came from a user correction, a time-costing gotcha, or a validated non-default choice the user explicitly accepted.
- **Not already covered** — no existing rule, skill, or CLAUDE.md entry says this.

## What NOT to extract

Reject candidates that match any of these:

- Code patterns, file paths, module structure, architecture — discoverable by reading the repo.
- Ephemeral task state, in-progress work, or fix recipes for a one-off bug.
- Generic git / language / framework knowledge.
- User-personal preferences (e.g. "user likes terse responses") — those belong in the per-user auto-memory system, not project rules.
- Anything already documented in `CLAUDE.md` or an existing `.claude/rules/` file.

## Target decision

For each qualifying candidate, pick ONE target:

| Target | Use when | Location |
|---|---|---|
| **Rule** | Behavioral guidance with a clear *when to apply*. The default choice. | `.claude/rules/...` |
| **Skill** | Recurring multi-step procedure, not a single guideline. | `.claude/skills/<kebab-name>/SKILL.md` |
| **CLAUDE.md edit** | Stable project fact, convention, or prohibition that belongs with the top-level project doc. | Edit `CLAUDE.md` |

## Scope decision (rules only)

- **Cross-cutting** (applies regardless of which file is being edited) → `.claude/rules/universal/<kebab-name>.md`, no `paths:` frontmatter.
- **File-scoped** (applies only to certain files/areas) → `.claude/rules/<kebab-name>.md` with `paths:` frontmatter naming the glob.

If a rule only matters for `tests/**/*.cs`, it MUST be path-scoped. Loading it for unrelated edits is noise.

## Canonical rule format

Reproduce this skeleton exactly — match the style of existing rules (`move-not-delete.md`, `verify-claims.md`):

```markdown
---
paths:              # optional — only for path-scoped rules
  - "tests/**/*.cs"
---

# <Short rule title>

<One-line statement of the rule.>

**Why:** <The incident or reason that motivates the rule. Keep the narrative concrete — this is what lets a future reader judge edge cases.>

**How to apply:** <When / where this kicks in, with concrete triggers.>
```

Do NOT add session IDs, machine-local paths, timestamps, or other non-portable metadata. The rule must stand on its own for any reader on any machine.

## Canonical skill format

Only use when the candidate is a multi-step procedure. Structure:

```markdown
---
name: <kebab-name>
description: <One sentence. What the skill does and when to invoke it.>
argument-hint: <[optional hint] — omit if the skill takes no arguments>
tools: <comma-separated list of tools the skill actually needs>
---

# <Title>

<One-paragraph summary.>

## <Sections as needed: When to use, Procedure, Guardrails, etc.>
```

## Procedure

1. **Scan the session** for candidate moments:
   - User corrections ("no, not that", "stop doing X", "don't assume", "that was wrong").
   - Surprising gotchas that cost real time (wrong assumption about a framework, unexpected interaction between components).
   - Validated non-default choices (user accepted an unusual approach without pushback, especially when it went against your first instinct).
   - Repeated explanations the user had to give.

2. **Overlap check.** For each candidate, grep for overlap before proposing:
   - `Grep` `.claude/rules/` and `.claude/rules/universal/` for related keywords.
   - `Read` `CLAUDE.md` and check its Prohibitions / Conventions / Core Principles sections.
   - If a match exists → propose as *update-existing*, not *new*. If fully covered → drop the candidate.

3. **Pick target + scope** per candidate (see tables above).

4. **Present candidates to the user** in plain text with short IDs, one line of rationale each. Example:

   ```
   Found 3 candidates:

   [R1] NEW rule, universal — "Verify path ignore status with `git check-ignore -v` before assuming."
        Why: wasted 20min assuming a file was ignored when only its parent dir was.

   [R2] UPDATE .claude/rules/test-broker-invariants.md — add a session-revalidation budget pitfall under "Polling Budgets".
        Why: already partially covered; session surfaced a specific outer-budget vs inner-timeout collision worth recording.

   [R3] NEW rule, paths: ["docker/**"] — "Never create Docker networks via CLI then wrap with NetworkBuilder."
        Why: user hit Testcontainers conflict mid-session.

   Reply with IDs to save (e.g. "R1 and R3"), or "none" to skip all.
   ```

   Use plain text, not `AskUserQuestion` — the option cap of 4 is too restrictive when there are many candidates, and free-text responses scale better.

5. **Write / update** each accepted candidate using the canonical format. Use `Edit` when updating, `Write` when creating. Use `Bash mkdir -p` first if creating a skill directory.

6. **Report** the final list: files written, files updated, candidates skipped. Brief. One line per file.

## Guardrails

- **Never write silently.** Always propose in step 4 and wait for explicit approval before step 5.
- **Prefer update over create.** A near-duplicate rule is worse than a slightly-long existing one.
- **Empty result is fine.** Most sessions produce nothing. Saying "no candidates met the bar" is a correct, honest outcome.
- **Don't duplicate auto-memory.** If a candidate is about the user personally or about cross-project preferences, it belongs in auto-memory, not here. Suggest to the user that they save it there instead.
- **Keep rules short.** Match the length of existing rules — title, one-line rule, 2-4 sentence `**Why:**`, 1-3 sentence `**How to apply:**`. If a rule grows past that, it's probably really a skill.
