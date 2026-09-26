# Rule standard: gates, budget, format

The shared bar for admitting a rule (`extract-session-rules`) and keeping one (`review-rules`). The burden of proof is on the rule: one failed gate means it is not a rule.

## Gates

Walk in order; the first failure decides the outcome.

1. **Recurring or costly.** The failure happened in 2+ separate sessions, shipped a defect, or cost 30+ minutes. One correction is not enough.
2. **Beyond model default.** A capable model without the rule would plausibly get it wrong in this repo. Generic virtues — verify, keep it simple, be thorough, answer directly, don't hedge — fail.
3. **Not mechanizable.** If a hook, lint, analyzer, test, type, or CI check can enforce it, build that instead. A rule violated again after it was written has shown prose doesn't work for it: mechanize or delete.
4. **Project-specific.** Names at least one concrete identifier, path, tool, or config in this repo. Process advice with no such anchor belongs in the user's personal `~/.claude/CLAUDE.md` or nowhere.
5. **Not derivable.** Not readable from the code or `docs/`. A fact about one site is a code comment there.
6. **Not duplicated.** No other rule, skill, or `CLAUDE.md` line already says it.
7. **Placed by fire surface.** Path-scoped by default. `universal/` only when it fires in 3+ unrelated areas of this repo — name them.

## Budget

| Scope | Cap |
|---|---|
| Always loaded: `CLAUDE.md` + `.claude/rules/README.md` + `.claude/rules/universal/*.md` | 2,500 words total |
| One universal rule | 150 words |
| One path-scoped rule | 300 words |

Measure from the repo root with `node .claude/skills/review-rules/check.mjs` — it reports budget, frontmatter, broken relative links, and index sync, and exits 1 on any problem.

Over a cap, no change may grow the total: an addition ships with cuts of at least equal size, named in the same proposal. Hit a number by cutting the weakest rule, never by dropping a project-specific fact (identifier, path, value, gotcha) from a strong one.

## Rule format

```markdown
---
paths:              # path-scoped rules only; gitignore globs
  - "tests/**/*.cs"
---

# <Clause stating the rule, not a noun phrase>

<One-line statement of the rule.>

**Why:** <One incident, 1–2 sentences — the detail that lets a reader judge edge cases.>

**How to apply:** <Concrete triggers.>
```

- Action first, plain English, no quotes of anyone, no in-house shorthand.
- Backticks around identifiers, paths, env vars; forward slashes; other rules linked as relative markdown links.
- No history ("previously", "no longer"), dates, session ids, machine-local paths, or `.claude/plans/` references.
- Every path-scoped rule has one `README.md` index row: trigger globs abbreviated, one-liner a single trigger clause. Universal rules have none.

## Skill format

- Frontmatter: `name`, `description`, optional `argument-hint`, optional `allowed-tools` scoped where possible (a pattern must match each `&&`/`;`/`|` subcommand on its own).
- `description` is the discovery surface: third person, capability first, then concrete when-to-use triggers. Don't set `disable-model-invocation` on a skill meant to self-invoke — it hides the description.
- Place `$ARGUMENTS` where the procedure reads it; write repo-absolute paths with the project-dir variable (`CLAUDE_PROJECT_DIR` in `${...}` form).
