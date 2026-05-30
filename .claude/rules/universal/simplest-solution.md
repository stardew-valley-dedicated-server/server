# Prefer the simplest, most direct solution

If the fix is to remove a line, remove it. Don't add a redirect, wrapper, or abstraction layer. If the codebase already has a pattern for something, follow it exactly.

**Why:** Three failure modes around scaffolding and abstraction:
- **Over-engineering when a one-liner works.** Drift toward "flexible" wrappers, redirects, or new abstraction layers when the codebase already has a working pattern (or when "delete this line" is the actual fix).
- **Building infrastructure with no consumer.** Caught 2026-04-24 on a planned `DiscoverTestIdentities` API + theory display-name porter to seed per-test entries in `ctrf-report.json` — a subagent check confirmed Makefile, LLMRenderer, UI, GitHub Actions, and FlakinessTracker all read only `results.summary` (never `results.tests[]`). Cutting the unbacked scaffolding saved ~320 LOC.
- **Dissolving an established home for a convention.** Caught 2026-05-05 on `Helpers/AssertHelpers.cs`. I recommended inlining the last remaining method into its single caller. That would have dissolved the established home for custom asserts in a codebase that demonstrably accumulates them. The "I'll re-extract when the next consumer arrives" framing is a lie — future-you misses the prior convention, the next assertion lands at a new call-site, and the pattern fragments until a multi-day consolidation pass is needed.

**How to apply:**
- **Default to the simplest fix.** Before proposing a wrapper, redirect, or new abstraction, ask: does an existing function or pattern in this repo already do this? If yes, use it. If the simplest fix feels too small, that's usually correct.
- **Verify a downstream consumer before scaffolding.** When a refactor proposes new infrastructure to *feed* something (per-test entries in a report, extra fields on an event, a new index), grep for an actual reader before building it.
- **Pattern continuity beats consumer count.** The "verify a consumer" check applies to *new* infrastructure. It does NOT apply to an existing "home for X" file (`*Helpers`, `Asserts`, `Validators`, etc.) whose member count has shrunk to one. Signals it's a home: plural name, single-concern namespace, sibling files suggesting more will accrete. If yes, the single consumer is enough — keep the home, add to it.
