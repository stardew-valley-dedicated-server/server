---
name: review-rules
description: Audit and refine the project's `.claude/` rules and CLAUDE.md for conciseness, format consistency, correctness, and pull-its-weight tokens. Use when the user asks to review, refine, audit, lint, or clean up rules — or when a rule directory has grown enough that adherence is degrading.
argument-hint: [optional path/glob or focus hint]
tools: Read, Grep, Glob, Edit, Write
---

# Review Rules

Audit `.claude/rules/**`, `CLAUDE.md`, and `.claude/skills/**/SKILL.md` and propose edits before writing any. Curates what exists; does not grow the set — for new rules, route the user to `/extract-session-rules`.

If the skill was invoked with arguments (e.g. `/review-rules universal only`, `/review-rules tests/**`, `/review-rules conciseness pass`), apply them as scope or focus filters in pass 1 and propagate to pass 2. They take precedence over the defaults below.

## Scope

In scope: `.claude/rules/**/*.md`, `.claude/rules/README.md`, `CLAUDE.md` (root + nested), `.claude/skills/**/SKILL.md`.

Out of scope unless the user opts in: `.claude/archive/**`, `.claude/tasks/**`, `.claude/commands/**`, `docs/**`, anything outside `.claude/` and root `CLAUDE.md`.

## Procedure

### Pass 1 — Inventory

Glob the in-scope files (filtered by argument if any). Read each. Build a flat list: path, line count, frontmatter, one-line summary. Cross-check `.claude/rules/README.md`: every file listed, every listed file present, one-liners match.

**Early exit:** if pass 1 surfaces no candidate issues at all, report that and stop. Don't manufacture findings.

### Pass 2 — Findings

Tag each finding:

- **[CUT]** delete content that fails the keep-test below
- **[FIX]** edit for format, clarity, correctness
- **[MOVE]** content belongs elsewhere (code comment, commit, docs, auto-memory, different rule)
- **[SPLIT]** file does two unrelated things
- **[MERGE]** duplicates an existing rule
- **[RESCOPE]** `paths:` glob is wrong, or universal/path-scoped placement is wrong
- **[INDEX]** `README.md` out of sync with the file
- **[QUESTION]** needs the user before acting

#### Form

The keep-test for every line: **"Would removing this cause Claude to make mistakes?"** If no, cut it.

- No filler ("It is important to note", "In general", "As mentioned earlier"), no hedging ("may want to consider possibly").
- No restating what Claude already knows (general framework concepts, language semantics).
- One subject per file. "X invariants" with five related bullets is fine; two unrelated rules sharing a file is [SPLIT].
- Length: CLAUDE.md target ≤200 lines (Anthropic's stated soft cap). For rule files, only flag length if a file has grown materially since last review or contains content that fails the keep-test — don't flag a rule purely for its line count.

#### Format

- **Frontmatter.** Path-scoped rules: `paths:` only, gitignore globs. Universal rules: no frontmatter. SKILL.md: `name`, `description`, optional `argument-hint`, optional `tools`.
- **SKILL.md description shape.** The description is the only thing Claude sees when deciding whether to load the skill — it's the discovery surface, not just metadata. Required: third person, first sentence states the capability, second clause states *when to use* with concrete triggers. Reject "Helps with X"-style vagueness.
- **Title is a clause that states the rule** (e.g. `# AsyncLocal flows through awaits, not through external queue pumps`). Not a noun phrase like `# Cabin Notes`.
- **Body skeleton.** Opening one-line statement; optional sub-sections; trailing `**Why:**` and `**How to apply:**`. Match this across rules — if a rule omits one of those blocks and the content would benefit, [FIX].
- **Headings.** `#` for title, `##` for sub-sections. `####+` only when genuinely needed.
- **Backticks** around filenames, identifiers, env vars, paths.
- **Cross-rule links.** Markdown links to other rule files, used consistently. Don't mix bare filenames and links.
- **Doc links.** Relative paths into `docs/`, used the same way everywhere.
- **No backslash paths.** Forward slashes only.
- **Terminology.** One term per concept within a rule, and (where feasible) across rules — e.g. don't have `host` / `daemon` / `endpoint` referring to the same thing in three different files.

#### Correctness

- **Cited paths exist.** Spot-check with Glob.
- **Cited identifiers exist.** Grep one or two per rule.
- **No refactor history.** "previously", "no longer", "has been removed", "this used to" — see `no-refactor-history-in-code.md`.
- **No machine-local content.** Dev-machine paths, full session IDs, `.claude/tasks/...` references. (Dated incident anchors in `**Why:**` are fine.)
- **No contradictions across rules.** If `rule-A` says "always X" and `rule-B` says "never X under condition Y", at least one needs to acknowledge the other.
- **Trigger still real.** If the `**Why:**` cites a condition (a feature flag, a config default, a framework quirk), confirm the condition still exists. A rule guarding against a removed surface is dead weight.
- **Earned, not speculative.** A rule should trace to a real incident, not "we should be careful about X." Speculative rules age the worst — flag for [QUESTION] if the `**Why:**` doesn't ground it in something concrete.

#### Scope and placement

- **Right home.** For each rule, ask:
  - Fact about code Claude can read → code comment, not rule.
  - How the system evolved → commit / PR description.
  - User-facing reference → `docs/`.
  - User-personal preference → auto-memory.
  - One-off task state → ephemeral, no persistent home.
  - Behavioral guidance with non-obvious "why" → rule, keep.
- **`paths:` accuracy.** Open the cited paths and confirm the rule actually applies. A rule mentioning `tests/.../Infrastructure/**` but only ever firing for `Helpers/Docker*.cs` should be [RESCOPE]d.
- **Universal vs path-scoped.** Single-area → must be path-scoped. Genuinely cross-cutting → `universal/`. For each L1 rule, ask: "outside the subtree where the incident happened, where in *this* codebase would this rule actually fire?" If the answer is "almost nowhere," it's [RESCOPE] to L2, not [KEEP] — universal placement costs tokens on every session for a rule that only matters in one tree. Generalized vocabulary is not the same as cross-cutting applicability; check the fire surface in this repo, not the rule's wording.

#### Index sync

- Every file in `.claude/rules/` and `.claude/rules/universal/` listed in `README.md`.
- One-liner matches current rule.
- "Triggers on" column reflects current `paths:` (abbreviated is fine).

### Pass 3 — Propose, then act

1. **Present findings in plain text**, grouped by file, one short bullet each. Cap initial output at the highest-value ~20 findings; if more, say so and ask whether to continue. Plain text, not pickers — free-text replies (`fix all CUT and FIX, defer SPLIT`) scale better.

2. **Wait for explicit approval.** Never write silently.

3. **Apply** with `Edit` (preferred — diff is reviewable) or `Write` (whole-file rewrites only). When updating `README.md`, edit only the rows that changed.

4. **Re-read each edited file** to confirm the change.

5. **Report final state**: files changed, deferred (with one-line reason), open [QUESTION]s. One line per file.

## Self-check before reporting

Before pass 3, walk this checklist:

- [ ] Each finding has one of the eight tags and anchors to a check above (no taste-only findings).
- [ ] Identifier / path claims grepped at least once.
- [ ] Cross-rule contradictions actually cross-checked, not assumed absent.
- [ ] No new footer label, heading style, or terminology introduced by my own findings.
- [ ] If pass 1 surfaced nothing, I'm reporting that, not padding.

## Guardrails

- **Verify before claiming.** No shipped finding without one piece of evidence (per `verify-claims.md`).
- **Adversarial split for findings.** Per `adversarial-review-split-findings.md`: each self-review finding is inherent non-issue or actionable; "out of scope" doesn't cover small adjacent fixes.
- **Match edit count to what was approved.** Per `plan-discipline.md`: N approved findings → exactly N edits. New issue mid-pass → announce before applying.
- **Anchor every [FIX] to a check above.** "I'd phrase it differently" is not a finding.
- **Preserve cited filenames.** Per `scope-means-no-reads-or-writes.md`: before renaming a rule file, grep the project. If anything outside `.claude/rules/` cites it, keep the name and rewrite contents in place.
- **No stylistic drift mid-pass.** Don't introduce a new footer label like `**Triggers:**` while auditing for inconsistency.
- **Don't grow the rule set here.** Real new rule worth saving → suggest `/extract-session-rules`.
