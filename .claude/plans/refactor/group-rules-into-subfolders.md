# Group L2 rules into `vanilla` / `mod` / `tests` / `misc` subfolders

## Goal

Group the flat `.claude/rules/*.md` (L2, paths-gated) rules into four subject-matter subfolders, so the directory scans cleanly and the rules reusable outside this project are separated from the project-specific ones.

Buckets:
- **`vanilla/`** — lesson is about Stardew Valley's own engine behavior (decompiled-source mechanics the mod works around). Reusable in any Stardew modding project.
- **`mod/`** — lesson is about the JunimoServer mod's own code, services, config, or patterns.
- **`tests/`** — test infrastructure, test runner, test UI, recording/measurement harness.
- **`misc/`** — fits none of the above: docker image build, CI plumbing, dependency tooling.

A file's bucket is decided by **its lesson**, which can differ from the code area its `paths:` glob targets. The `paths:` frontmatter and rule bodies are not changed — only file location.

`universal/` (L1, always-on) is out of scope and stays as is. `README.md` stays at `rules/` root with its L2 table restructured.

## Current state

39 L2 rule files sit flat in `.claude/rules/`, alongside `README.md` (the index) and `universal/` (L1). The README's "L2 — `rules/`" section is one flat 39-row table.

The loader matches `paths:` frontmatter at any directory depth — `universal/` is already a loading subfolder. No repo code resolves rule paths: `grep -rn ".claude/rules"` hits only the README, plan docs, and the two rule-authoring skills, none of which enumerate L2 filenames. Moving files is load-safe; the only path-aware references are the README index, inter-rule citations, and the two skills (all addressed below).

## Mapping (39 files → 4 buckets)

### `vanilla/` — Stardew engine lessons (10)

| File | Why vanilla |
|---|---|
| `cabin-system.md` | Game cabin-allocation invariants (BuildStartingCabins, load sweep, Farmer split) |
| `host-automation.md` | Decompiled-first: `hasDedicatedHost`, `netReady` formula, FarmEvent completion |
| `masterplayer-is-player-on-server.md` | `multiplayerMode=2` → `MasterPlayer`/`player` engine identity |
| `chat-font-language-tag.md` | Engine chat font chosen solely by per-message `LanguageCode` tag |
| `display-scaling.md` | Zoom-out via `desired*` getters; `Game1.Update` reconcile; save-load clobber |
| `netdictionary-public-surface.md` | `NetDictionary` must be mutated via public API |
| `netfield-revert-pattern.md` | NetField interpolation makes a revert-Set a no-op |
| `save-import-layer-timing.md` | Two-phase save-load engine timing (pre-load XML vs SaveLoaded finalizer) |
| `smapi-api-surface.md` | SMAPI `SemanticVersion`/`Constants`/`ModResolver` surface |
| `abandoned-claim-is-steam-only.md` | Engine `getUserID()` LAN-vs-Steam/GOG behavior |

### `mod/` — JunimoServer mod patterns (7)

| File | Why mod |
|---|---|
| `harmony-patch-reachability.md` | Patch reachability == its registering mod-service's reachability |
| `test-state-setter-runs-engine-reconcile.md` | Mod `/test/*` API setters must run engine reconciliation |
| `startup-cold-start-measurement.md` | Mod GameManager/ApiService/RenderingController boot-cost attribution |
| `server-tps-headless.md` | Mod `Env.cs` TPS clamp + `.env` config values |
| `mod-game-thread-allocation.md` | Mod game-thread hot-path GC discipline |
| `asynclocal-pitfalls.md` | `AsyncLocal` rebind across pump boundaries |
| `debugging.md` | `LogLevel.Error` in mod code is test poison |

### `tests/` — test infra, runner, UI (18)

`docker-test-resources.md`, `test-broker-invariants.md`, `tests-assert-via-http-api.md`,
`test-timing.md`, `colocate-event-emit.md`, `drain-before-consume-disposal.md`,
`follow-true-created-state-eof.md`, `provision-up-front-when-startup-exceeds-serviceable-tail.md`,
`minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md`, `one-writer-per-artifact.md`,
`runner-side-artifact-writer.md`, `runner-ui-pipeline-plumbing.md`, `not-dispatched-derivation.md`,
`event-catalog-no-inline-enums.md`, `test-ui-build.md`, `test-overlay-pixel-contract.md`,
`ffmpeg-pixel-measurement.md`, `recorder-anchor-first-frame.md`

### `misc/` — docker / CI / tooling (4)

`modern-docker.md`, `docker-save-format-source-daemon.md`,
`renovate-nuget-allowedversions-needs-semver.md`, `openapi-generator-reflection-invoke.md`

**Totals:** vanilla 10 · mod 7 · tests 18 · misc 4 = 39. `README.md` stays at root.

## Borderline placements

Subject-matter calls where the bucket differs from the `paths:` glob, recorded so the grouping is re-litigable:

- **`save-import-layer-timing.md` → vanilla.** `paths:` are all `mod/`, but the lesson is the game's two-phase save-load engine timing — reusable for any save-manipulating Stardew mod.
- **`abandoned-claim-is-steam-only.md` → vanilla.** Test-oriented framing, but the load-bearing fact is engine `getUserID()` behavior per transport.
- **`asynclocal-pitfalls.md` / `debugging.md` → mod.** Both also trigger on `tests/`, but the lesson is about mod-code async/logging behavior.
- **`test-overlay-pixel-contract.md` / `ffmpeg-pixel-measurement.md` / `recorder-anchor-first-frame.md` → tests.** Overlay geometry touches `mod/JunimoServer.Shared`, but the lesson is test-harness recording/measurement.

## Mechanism

1. Create `vanilla/`, `mod/`, `tests/`, `misc/` under `.claude/rules/`.
2. `git mv` each file into its bucket (preserves history). 39 moves.
3. Rewrite the README L2 section into four sub-tables (one per bucket), keeping the `| File | Triggers on | One-liner |` columns. Preserve every one-liner verbatim. Adjust the intro line to note the buckets are organizational and load behavior is unchanged (any-depth glob match).
4. Update both rule-authoring skills so new path-scoped rules land in a bucket, not flat at root (details below).
5. Repair any inter-rule citation a move invalidated.

## Skill updates

Both skills must track the new layout or the grouping re-fragments:

- **`extract-session-rules/SKILL.md`** — the "Scope decision" section routes path-scoped rules to `.claude/rules/<kebab-name>.md`. Change it to route to the matching subject-matter subfolder (`vanilla`/`mod`/`tests`/`misc`) chosen by the rule's lesson, with the four bucket definitions. New path-scoped rules then land grouped.
- **`review-rules/SKILL.md`** — `Glob`-based discovery (`.claude/rules/**/*.md`) already recurses, so the audit still finds bucketed files. Two spots assume the flat+universal two-level layout and need adjusting: the "Build a flat list … every file listed in `README.md`" discovery step and the README cross-check that enumerates `.claude/rules/` and `.claude/rules/universal/`. Update both to expect the bucket subfolders and the four README sub-tables.

## Compatibility verification

- **Loader.** `paths:` matched at any depth (`universal/` proves it); no `paths:` value changes.
- **No code consumer.** Re-run `grep -rn ".claude/rules"` at execution; confirm no new hit enumerates L2 filenames.
- **Inter-rule links.** No file is renamed, only relocated. Citations using a bare filename stay valid; a relative `](../x.md)` or `[[x]]` whose depth changed needs repointing. Grep every moved filename across `.claude/` and fix any reference a move invalidated. The README (main referencer) is rewritten anyway. Per `scope-means-no-reads-or-writes.md`, preserve cited filenames — none rename.

## Out of scope

- `universal/` (L1).
- Rule content — no edits to any rule body or `paths:` frontmatter.
- New buckets beyond the four.

## Post-conditions (gates)

- `ls .claude/rules/` shows only `README.md`, `universal/`, `vanilla/`, `mod/`, `tests/`, `misc/` — no loose `*.md` besides README.
- Bucket file counts match the mapping (10 / 7 / 18 / 4).
- `git status` shows 39 renames (R), not delete+add pairs.
- README L2 section has four sub-tables; the one-liner column is set-equal to the pre-move table (39 rows).
- `grep -rn` for each moved filename across `.claude/` resolves or was repointed.
- Both skills name the bucket subfolders in their rule-placement / discovery text.
