# Docs Cleanup — Audit Overview & Workstream Index

Full audit of `docs/` (62 markdown pages + VitePress config + generated OpenAPI spec) against the
codebase, performed 2026-06-13. Every page was read in full; every checkable claim (env vars,
defaults, commands, paths, ports, routes, behavior) was verified against the implementing code, and
every finding was independently re-verified by an adversarial second pass before landing here.

## Workstreams (execute in order)

| Plan | Scope | Worst severity |
|---|---|---|
| [01-admin-docs-corrections.md](01-admin-docs-corrections.md) | `docs/admins/**`, `docs/features/backup.md`, `.env.example` touchpoints | **Critical** ×3 |
| [02-features-players-docs.md](02-features-players-docs.md) | `docs/features/**` (rest), `docs/players/**`, `docs/community/**` link fixes | Medium |
| [03-developer-testing-docs.md](03-developer-testing-docs.md) | `docs/developers/testing/**`, `building-from-source.md`, `docs/README.md` | High |
| [04-architecture-api-events-ci.md](04-architecture-api-events-ci.md) | `docs/developers/architecture/**`, `advanced/**`, `events-schema.md`, `api/**`, `contributing/**`, OpenAPI spec | High |
| [05-structure-and-navigation.md](05-structure-and-navigation.md) | Information architecture, dedup/canonicalization, sidebar/index sync, VitePress config | Medium |

Correctness (01–04) comes before structure/editorial (05). Within each plan, items are ordered
Critical → High → Medium → Low; Low items are "while you're in the file" riders, not separate PRs.

## Cross-cutting themes (read before starting any workstream)

1. **The multi-host / multi-account refactor is the dominant staleness vector.** Pages written
   against the old single-host model: `test-harness.md` (component catalog), `remote-host-setup.md`
   (runId claims), `events-schema.md` (container naming), `architecture/steam-auth.md`
   (single-account `/health` shape, 2-of-8 endpoints, missing `STEAM_ACCOUNTS`). When fixing any of
   them, diff against `tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs`,
   `tests/JunimoServer.Tests/Infrastructure/HostPool.cs`, and `tools/steam-service/Program.cs`
   (`DiscoverAccounts`) **as a unit** — don't fix one page in isolation.
2. **Twin pages drift.** The same facts are hand-maintained in admin- and developer-facing copies
   (chat commands ×3 pages, networking ports ×2, env vars ×3 surfaces, invite-code flow ×3,
   multi-host setup ×2). Plan 05 designates a canonical home per topic; plans 01–04 fix content
   in place. When both plans touch the same table, do the 05 consolidation first or in the same PR.
3. **"X is undocumented" requires a full-tree check.** One first-pass finding
   (STEAM_KEEP_LANGUAGES "undocumented") was wrong — it is documented in `steam-auth.md:104-133`;
   the right fix was a cross-link, not new prose. Before adding any "missing" content, grep all of
   `docs/` for it first.
4. **Several findings are doc-vs-code conflicts where the *code* may be the right side to change.**
   These are marked **DECISION** in the plans and listed in [Decision register](#decision-register).
   The plans default to the doc-side fix (this is a docs cleanup); the code-side option is recorded
   so it isn't silently lost.

## Decision register

| # | Conflict | Doc-side fix (default) | Code-side alternative |
|---|---|---|---|
| D1 | `API_PORT` is both host-mapped to fixed container port 8080 (`docker-compose.yml:10`) AND forwarded into the container (`:33`), so changing it breaks the API | Remove `API_PORT` from the "Changing Ports" example; document the breakage | Stop forwarding `API_PORT` into the container (container port stays 8080) — cleaner |
| D2 | `VERBOSE_LOGGING`, `HEALTH_CHECK_SECONDS`, `ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS`, `FORCE_NEW_DEBUG_GAME` documented as `.env` knobs but never forwarded by compose | Annotate as "requires adding to docker-compose.yml `environment:`" | Add the four keys to the compose `environment:` block |
| D3 | `ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS` documented default `true`; code default `false` (`Env.cs:22-25`) | Document `false` (in `environment.md` AND `.env.example:94`) | Flip the code default if `true` was intended |
| D4 | `introduction.md` claims 400 on invalid params; handlers return 200 + `{success:false}` (no 400 exists anywhere in `ApiService.cs`) | Document the 200+body contract | Make handlers set 400 |
| D5 | OpenAPI spec carries zero `parameters`/`requestBody`/`securitySchemes`; generator never emits them (`OpenApiGenerator.cs:50-115`) | Document params manually in `introduction.md` | Extend the generator (watch `.claude/rules/openapi-generator-reflection-invoke.md` — reflection-invoked, fixed positional args) |
| D6 | `!event` documented as admin command; code has no role gate (`AlwaysOnFestivals.cs:525-547`) and broadcasts "Type !event to start now" to everyone | Reclassify as player command in both docs | Add an admin gate (contradicts the public broadcast — unlikely intended) |
| D7 | `ci-cd.md` marks `DEPLOY_API_KEY` optional; workflow aborts without it (`deploy-server.yml:113-124`) | Mark Required: Yes | Relax the workflow check |
| D8 | `getting-help.md` routes feature requests to GitHub Discussions; Discussions are disabled on the repo (`has_discussions:false`) | Route to Issues (`feature_request.yml` exists) + Discord | Enable Discussions |
| D9 | `!authstatus` example shows a "(45s remaining)" countdown that doesn't exist (`AuthStatusCommand.cs:59-73`) | Show real `[OK]/[PENDING]` output | Implement the countdown |
| D10 | `UPDATE_INTERVAL_MS` consumed by discord-bot but compose hardcodes `"30000"` | Leave undocumented (internal) | Interpolate `${UPDATE_INTERVAL_MS:-30000}` and document |

## Methodology & validation statement

- **Process:** 13 parallel reviewers (8 doc areas, 4 reverse code→docs sweeps over env vars /
  commands / HTTP+WS API / dev workflow, 1 structure+dead-link audit), each finding then
  adversarially verified by an independent agent that re-derived the evidence; a completeness
  critic then spot-checked under-covered files, which triggered a second pass over the seven
  developer pages and a full env-var inventory. The three Critical findings were additionally
  re-confirmed by hand against `docker-compose.yml`, `docker/rootfs/startapp.sh`,
  `tools/steam-service/Dockerfile`, and the doc texts.
- **Validated as accurate (no action needed).** Substantial doc surface checked out exactly against
  code — preserve it during remediation. Highlights: all of `server-settings.md`'s defaults table
  and farm-type/PetBreed/NetworkBroadcastPeriod semantics (`ServerSettings.cs`,
  `FarmTypeSetting.cs`, `GameCreatorService.cs`); `environment.md` port defaults, SERVER_FPS/VNC
  behavior, ALLOW_INSECURE_SETUP semantics (`startapp.sh:14-54`); all of `discord.md`'s relay
  formats/intents/permissions (`tools/discord-bot/src/index.ts`); installation.md service names,
  image names, `attach-cli`/`info` flow; the CSharpier/format gate and merge-queue documentation in
  `ci-cd.md`/`contributing/index.md` (verified against `validate-pr.yml`, `validate-merge-group.yml`,
  `lefthook.yml`, `Makefile`); `game-engine-notes.md` (every claim verified against decompiled
  sources); ~40 spot-checked file:line citations in `client-manipulation-techniques.md` (all exact);
  `events-schema.md`'s envelope/serialization/correlation-chain documentation; steam-auth.md's
  commands table and language-filtering section; version facts repo-wide (game 1.6.15-24356,
  ~200-day token lifetime — consistent everywhere).
- **Not exercised:** the VitePress build itself (`bun install && bun run build` writes only to
  gitignored paths but was skipped under this task's no-modification constraint), and GitHub-side
  settings (merge queue/ruleset config — verified only via in-repo evidence). **Each remediation PR
  should run `make docs` (or `docs/scripts/fetch-openapi.sh` + `bun --cwd docs run build`) as its
  verification gate** — it also operationally validates the gitignored `docs/assets/openapi.json`
  prerequisite that plan 03 documents.

## Out of scope for all workstreams

- Any change outside `docs/` except where a plan explicitly lists a companion touchpoint
  (`.env.example`, `CLAUDE.md` step-count, `CONTRIBUTING.md` anchor, compose/code items from the
  decision register). Those are flagged per-item, never silent.
- `docs/node_modules`, `docs/.vitepress/cache`, `docs/.vitepress/dist` (vendored/generated).
