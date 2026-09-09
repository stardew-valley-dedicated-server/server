---
name: commit
description: Composes a Conventional Commit message or PR title that passes this repo's commitlint gate — provides the enforced type list, the scope enum as a files-edited→scope lookup, and the optional-scope rule. Use before running `git commit`, or when setting a PR/squash title, in this repo.
---

# Commit conventions

Commit and PR titles are `type(scope): subject`, enforced by `commitlint.config.js` at commit time (lefthook `commit-msg`) and in CI (`validate-commits`, `validate-pr-title`). Resolve type and scope by lookup, not recall. `commitlint.config.js` `scope-enum` is the authoritative scope list; the map below routes to it and must stay in sync with it.

Subject-writing rules (changelog-facing wording, 100-char body lines, no `Co-Authored-By`) live in `.claude/rules/universal/git-workflow.md` — this skill only picks `type(scope)`.

## Step 1 — type (what *kind* of work is it?)

| Type | Kind | Changelog |
|---|---|---|
| `feat` | New player/admin-facing capability | Features (minor bump) |
| `fix` | Bug fix in shipped behavior | Bug Fixes (patch bump) |
| `perf` | Faster/lighter shipped behavior | shown |
| `revert` | Reverts a commit | shown |
| `docs` | Documentation only | shown |
| `refactor` | Restructure, no behavior change | hidden |
| `test` | Test code only | hidden |
| `build` | How artifacts are compiled — Dockerfile build stages, `.csproj`, `Directory.Build.props`, SMAPI bumps | hidden |
| `ci` | Pipelines, GitHub Actions, release automation | hidden |
| `chore` | Everything else with no kind-specific type — deps, repo config, `.claude/` | hidden |
| `style` | Formatting only | hidden |

- **`ci` and `build` are types, never scopes** — `ci:` / `build:`, never `fix(ci)` or `chore(ci)`. CI is scope-free (`ci(e2e)` is rejected).
- **`chore` is the fallback**, not the parent, of the hidden types. Use `test`/`refactor`/`build`/`ci`/`style` when the kind fits; `chore` only when none does — never `chore(<kind>)`.

## Step 2 — scope (which *area*? map from the files you edited)

Scope is optional. When present, it must be one of these. Match on the paths you actually changed:

| Files edited | Scope |
|---|---|
| `Services/AlwaysOnServer/`, `Services/HostAutomation/` | `host` |
| `Services/{GameManager,GameThread,ServerOptim,PersistentOption,Settings}/`, `ModEntry.cs`, `ModService.cs`, `Env.cs`, `Util/`, `mod/JunimoServer.Shared/` | `core` |
| `Services/Api/` | `api` |
| `Services/SteamGameServer/`, `Services/AuthService/`, `tools/steam-service/` | `steam` |
| `Services/PasswordProtection/`, `Services/Roles/` | `auth` |
| `Services/Lobby/` | `lobby` |
| `Services/CabinManager/` | `cabins` |
| `Services/ChatCommands/`, `Services/Commands/` (framework + relay + generic commands) | `chat` |
| `Services/{SaveImport,GameCreator,GameLoader}/` | `saves` |
| `Services/CropSaver/` | `crop-saver` |
| `Services/{NetworkTweaks,MessageInterceptors,Security}/` | `networking` |
| `Services/GameTweaks/`, `Services/NpcIntegrity/` | `gameplay` |
| interop with another/3rd-party mod | `compat` (reserved — no backing code yet) |
| `Services/Backup/` | `backup` |
| `Services/Diagnostics/`, `tools/diagnostics/` | `diagnostics` |
| `tools/discord-bot/` | `discord` |
| `docker/` runtime image (rootfs, entrypoint, s6, proxy) | `docker` *(build stages → type `build`)* |
| `tests/JunimoServer.Tests/` | `tests` |
| `tests/JunimoServer.TestRunner/` | `test-runner` |
| `tests/test-client/` | `test-client` |
| `tests/test-ui/` | `test-ui` |
| `tools/` dev CLIs (netdebug, openapi-generator, xnb-unpacker, dll-patcher, discli-docker, lobby-layout-preview-poc) | `tools` |
| `.claude/` | `claude` |
| `docs/` | `docs` |
| `Makefile`, `lefthook.yml`, `renovate.json`, `.gitattributes`, `.editorconfig`, `biome.jsonc`, root `package.json` | `repo` |
| Renovate-authored dependency bumps only (don't hand-author) | `deps`, `deps-dev`, `deps/*` |

Two routing rules:
- **A feature-specific command attributes to its feature**, not `chat` — a `!cabin` behavior change is `feat(cabins)`, a `saves` console command is `feat(saves)`. Only the command framework, Discord relay, and generic commands (`!invite`, `!login`) are `chat`.
- **New *settings* attribute to the feature they configure** (a new cabin option is `feat(cabins)`), not `core`.

## Step 3 — when unsure, don't guess

- **The change spans several areas, or no row fits cleanly → omit the scope.** A bare `type: subject` is always valid; a wrong scope is a rejected commit. Omitting beats guessing.
- **Unsure a scope is valid → validate before committing:** `printf 'feat(scope): x\n' | npx commitlint`. Exit 0 = valid.
