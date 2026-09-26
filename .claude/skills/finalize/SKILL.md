---
name: finalize
description: Takes a change to done in one pass before a PR or merge. Reviews every changed hunk against a fixed checklist, gets a second opinion from a fresh-context reviewer and CodeRabbit, cleans up comments and docs, runs the build, format, and unit checks for the touched areas, fixes what is clearly wrong, and reports only what needs a decision. Use when the user asks to finalize, wrap up, or clean up a change, asks for an adversarial review, or asks whether a change is done or ready for a PR.
argument-hint: [report] [focus or files]
---

# Finalize a change

One thorough pass instead of repeated review rounds. Finish every step before reporting.

## Mode

- `$ARGUMENTS` contains `report`, or the user asked a question or only asked for a review: change nothing, report, stop.
- Otherwise fix everything that is clearly wrong and inside the change, and hand back the rest.
- Never commit, push, or open a PR unless the user asked for that in the same message.

## 1. Scope

- The change is `git diff origin/master...HEAD` plus uncommitted work (`git status`, `git diff HEAD`).
- If the tree holds another task's work (common in the main checkout), review only this task's files and name what you left out.
- Read the path-scoped rules for every touched area.

## 2. Review every hunk

Open each changed file around every hunk; the diff alone hides context. Check each change for:

- **Correct**: logic, edge cases, error paths, cleanup, concurrency, and every caller of a changed signature or behavior.
- **Complete**: every other use of a renamed or changed name, string, env var, or setting is updated. Grep the whole repo (code, tests, `docs/`, `.env.example`, `docker-compose*.yml`, `.github/`, `.claude/`), not just the diff.
- **Consistent**: same patterns, names, and wording as the surrounding code and sibling features.
- **Simple**: no retry, fallback, or catch that hides a root cause; no dead code, unused parameters, leftover debug output, or options nobody asked for.
- **Documented**: user-facing behavior changes appear where users look.
- **Plan**: a tracked plan in `.claude/plans/` that this change completes is deleted with `git rm`, and links to it are fixed. A partly done plan keeps only what is left.

## 3. Comments and docs

Check every comment and doc line the change touched against the code:

- Accurate, minimal, useful. Explain why when it helps, never what the code already shows.
- Comments cover intent, constraints, and non-obvious behavior; docs cover behavior and usage.
- No history, changelog, diary, or implementation narrative. No claims the code doesn't support.
- No filler ("it's worth noting", "in order to", "as mentioned above", "for completeness"), hedging, or repetition.
- Simple, plain English, no em dashes. Delete a comment that doesn't earn its place.

## 4. Second opinion

- Spawn one `general-purpose` subagent, not a fork, so it doesn't know how the code was written. Give it the diff range, the goal of the change, and the step 2 checklist. Ask for findings with `file:line` and evidence, no fixes.
- Run `coderabbit review --agent --base master --include-untracked` if `coderabbit auth status` shows you are logged in.
- Treat every finding from either as a claim: confirm it in the code before acting on it.

## 5. Checks for the touched areas

| Touched | Run |
|---|---|
| `.cs` files | `dotnet build` on each owning `.csproj`, then `dotnet csharpier check <files>` |
| JS, TS, Vue files | `npx @biomejs/biome check --no-errors-on-unmatched <files>` |
| `mod/` or `tools/steam-service/` logic | `make test-unit` |
| `tests/test-ui/` | `make build-test-ui` |
| `docs/` | In `docs/`: `bun install` if `node_modules` is missing, then `bun run check` and `bun run build` (the build fails on dead links). Both need `docs/assets/openapi.json`; if it's missing, copy `/data/openapi.json` out of a local `sdvd/server` image, as `make docs` does. |
| `.claude/rules/` or `CLAUDE.md` | the `review-rules` skill on the changed files |

Don't start E2E runs; the runner is shared. Name the test classes that cover the change instead. After fixing, rerun the checks for what you changed.

## 6. Report

Use only these sections and leave out empty ones:

- **Fixed**: one line per fix, with `file:line`.
- **Needs your decision**: the finding, why it matters, the options, and your recommendation, one line each.
- **Checks**: what ran and passed, and which E2E classes cover the change.

If nothing needed fixing and nothing needs a decision, say so in one line.
