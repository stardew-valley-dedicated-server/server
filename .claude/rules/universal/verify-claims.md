# Verify named claims before publishing them

Before you write a concrete claim that a reader will act on — an identifier name, a framework behavior, a cited number, a "nothing consumes this" cut, "the reference implementation handles X" — verify it. One grep, one source read, one benchmark check. Don't pattern-match plausible-sounding names from context, don't propagate cited numbers without checking the primary source, and don't act on shallow no-consumer reads.

**Why:** Repeated incidents across this codebase:
- Fabricated `SDVD_STEAM_ACCOUNTS_N` (real name: `STEAM_ACCOUNTS`) almost shipped in a user-facing log message — pattern-matched from surrounding `SDVD_MAX_*` convention without grepping.
- A "30-50% slower under emulation" claim in the distributed-tests spec turned out to conflate Rosetta (~20%) and QEMU (~85%) — would have produced wrong worker-capacity decisions.
- A proposed "drop `correlation_id`" cut would have broken `AsyncLocal`/`TestContext` capture across cross-thread enqueue (load-bearing per `asynclocal-pitfalls.md`); 6 of 11 cuts in that pass were wrong on second read.
- Speculative risks raised against `JsonlReporter` (disk-full Flush throws, `[Retry]` truncation) all applied equally to the in-tree `InfrastructureEventLog` reference, which was already in production — the hedging was cold-feet, not diligence.
- The v2 spec's "shared Steam-account pool serves any N workers" motivation contradicted its disjoint-slicing implementation; an operator sizing `STEAM_ACCOUNTS` from the motivation would have undersized the array.

**How to apply:**
- **Identifier names** in plans or proposed user-facing strings (env vars, flags, file paths, Makefile targets, config keys, event names) → one Grep before publishing. Especially important for strings that survive the plan and outlive it (log messages, error text, doc snippets).
- **Cited numbers** from secondary docs ("~30-50% slower", "transfers in seconds", polling cadences) → check the primary source (vendor docs, measured benchmark, the actual code path) before depending on the figure.
- **"Nothing consumes this" cuts** → grep the identifier project-wide and read every hit, not just the count. If user approval is qualified ("when you're sure", "if it makes sense") rather than binary, treat that as an explicit instruction to re-verify per cut, not per batch.
- **"In-tree pattern is risky" framing** → ask whether the reference implementation faces the same risk. If yes and the reference is fine, the risk is either not real or already accepted — don't use it to block the change. Save genuine concerns for things that differ from the reference (lifecycle scope, call-site patterns, shared state).
- **Framework internals** (xUnit dispatch, SMAPI events, Stardew save flow) → read `decompiled/sdv-1.6.15-24356/` or vendor source. Don't reach for "I think X works like…".
- **Design docs with separate "why" and "how" sections** → one explicit cross-check pass. Take each concrete claim from the motivation (numbers, scaling, "serves any N", "bounded by X") and confirm the implementation delivers exactly that property. Drift between the two sections is a load-bearing bug because readers act on the motivation.
- **Identifiers being introduced by this change** are exempt — they're new by definition and there's nothing to verify against.
