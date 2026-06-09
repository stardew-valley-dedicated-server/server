# CodeQL C# built scan via a private game-DLL mirror

## Problem

CodeQL reports **Low C# analysis quality** on master:

> Percentage of calls with call target: 81 % (threshold 85 %).
> Percentage of expressions with known type: 90 % (threshold 85 %).
> C# was extracted with build-mode set to 'none'.

The type-coverage metric (90 %) already passes; only **call-target resolution (81 %)** is under threshold. The cause is `build-mode: none` in `.github/workflows/codeql.yml:103` — CodeQL parses C# source without compiling, so it can't resolve calls into the proprietary game/SMAPI assemblies (`Stardew Valley.dll`, `GalaxyCSharp.dll`, `Steamworks.NET.dll`, `Lidgren.Network.dll`, `StardewModdingAPI.dll`, Harmony). Those unresolved calls are the missing ~4 %. This is a database-quality advisory, not a vulnerability.

## Decision

Raise the metric by giving CodeQL a **traced host build** against the real game DLLs, sourced from a **private GHCR mirror** (auth-gated, never fork-readable), and run that built scan **only on master push + the weekly schedule**. PRs keep the current buildless scan.

- **PRs (any, including forks):** C# stays `build-mode: none` — unchanged, secret-free, fork-safe.
- **Push to master + weekly schedule:** built C# scan (`build-mode: manual`) using the private mirror → high-quality DB → the low-quality banner clears on master.
- **No gate added to CodeQL.** `push:[master]` and `schedule` never carry fork context, so the mirror pull + any seed secrets run in an already-trusted context. (The fork-pr gate stays where it is, on E2E.)

### Why master-only and not per-PR

Considered making the built scan run on approved PRs behind an E2E-style maintainer gate. Rejected because of the SARIF dedup model: two analyses of the **same language** coexist only with **distinct `--sarif-category` values**; same category → the later upload overwrites the earlier. Consequences for a per-PR built scan:

- Distinct categories → after approval the security tab shows **both** a low-quality buildless C# analysis and the high-quality built one; the banner never clears. Defeats the purpose.
- Same category → built overwrites buildless on approval, but a later unapproved push overwrites it **back** to low-quality. The result flip-flops with approval status.

Master-built vs PR-buildless live on **different refs** (master ref vs PR merge ref), so they never overwrite each other regardless of category — the dedup clash disappears. The cost is that the metric clears only on master, not on the open PR. That cost is acceptable: "metric passes on the open PR" was only reachable via the messy distinct-category route, which leaves the banner up anyway.

## The private mirror (the build-time win)

### Why not `actions/cache`

The game DLLs are proprietary; any store they live in must be unreachable by untrusted fork PRs. **`actions/cache` cannot hold them.** Per GitHub's dependency-caching docs, a workflow run triggered by a fork pull request *can restore caches created in the base branch (master)* — with **no approval and no credential** (cache restore is ref-scoped, not auth-gated; key secrecy is irrelevant because the attacker controls the PR's workflow file and can request any key). So DLLs in `actions/cache` leak to any fork PR.

A **private GHCR image** is the opposite: restore is gated by `packages: read`, which a fork PR run does not hold. There is no ref-scope path to it. This is the structural difference that makes the mirror legal where the cache is not. "Compile with the game, just don't distribute it" — the DLLs are used to compile, never made fetchable.

### Mirror image

`ghcr.io/<org>/codeql-game-refs:<smapi-version>-<game-build>` — **private**.

Contents (the minimal reference set the mod build needs — tens of MB, **no Content depot**):

- 4 game DLLs: `Stardew Valley.dll`, `GalaxyCSharp.dll`, `Steamworks.NET.dll`, `Lidgren.Network.dll`
- SMAPI assemblies: `StardewModdingAPI.dll` + `smapi-internal/*.dll` (resolved by `Pathoschild.Stardew.ModBuildConfig` from the SMAPI install)

### Seed job — auto-seed on version change

A small job (or reusable workflow) that runs in the trusted master/schedule context:

1. Compute the tag from `SMAPI_VERSION` (`docker/Dockerfile:2`) + the downloaded game build.
2. If the tag already exists in GHCR → **no-op** (idempotent; the built CodeQL job just pulls it).
3. If absent → acquire and push:
   - `steam-service download --skip-sdk` → `/game` (reuses the exact path in `docker/Dockerfile:34-41`).
   - SMAPI installer `--game-path /game` (reuses `docker/Dockerfile:44-49`) → lands `StardewModdingAPI.dll` + `smapi-internal/`.
   - Copy the minimal reference set into a thin image, `docker push` **private** with `GITHUB_TOKEN` + `packages: write` (the pattern already used in `build-image.yml:61`, `build-release.yml`).

A SMAPI/game bump changes the tag, so the next master run auto-reseeds. After the first seed, the built scan's cost is one small `docker pull` + one `dotnet build` — **no per-run Steam download.**

## Changes

### 1. `.github/workflows/codeql.yml`

- Make the C# `build-mode` conditional:
  - `pull_request` → `none` (today's behavior).
  - `push` / `schedule` → `manual`.
- On the `manual` path, between `init` and `analyze`:
  - `docker login ghcr.io` (with `GITHUB_TOKEN`) → `docker pull` the mirror (needs `packages: read` on the job).
  - Extract the DLLs to a dir (e.g. `./gamedir`).
  - `dotnet build mod/JunimoServer/JunimoServer.csproj /p:GamePath=$PWD/gamedir` — **CodeQL traces this**. Requires the .NET 6 SDK on the runner (the mod targets `net6.0`).
  - `analyze` with a stable `--sarif-category`/`category` for `csharp` (consistent across runs).
- NuGet cached via the cache-dance pattern from `e2e-tests.yml:388-404` (public — safe to cache).
- The `changes`/path-scoping logic and the `javascript-typescript` / `actions` matrix entries are untouched.

### 2. Seed job / reusable workflow

New file (e.g. `.github/workflows/seed-codeql-assets.yml` or a job folded into an existing master-context workflow). Builds + pushes the private mirror as described. `packages: write`.

### 3. Docs + load-bearing comments

- `docs/developers/contributing/ci-cd.md` CodeQL section (currently `:200-210`): document PRs-buildless / master-built-via-mirror and **why the metric clears only on master**.
- Load-bearing comment in `codeql.yml` and the seed workflow — the invariant:

  > Game DLLs live in the private GHCR mirror ONLY — never in `actions/cache`. A fork PR run can restore master/base-scoped caches with no approval and no credential (cache is ref-scoped); the mirror is auth-gated (no fork credential). Moving the DLLs into `actions/cache` to "save the pull" silently reopens the leak.

  This stops a future contributor from optimizing the DLLs into `actions/cache`.

## Open items (needed at implementation time)

- **Org/namespace** for the private image: `ghcr.io/???/codeql-game-refs`.
- **Steam secret availability in the CodeQL workflow context** for the seed job. `STEAM_ACCOUNTS` (or single-account `STEAM_USERNAME`/`PASSWORD`) is currently scoped to the `test-vps` environment for E2E (`e2e-tests.yml:458,482`); the seed job needs it too, in a trusted non-fork context (master/schedule). Decide whether to put the seed job in an environment that carries the secret, or add a repo/environment secret for it.
- Confirm the runner's **.NET 6 SDK** provisioning for the traced `dotnet build` (the mod is `net6.0`; `setup-dotnet` with `6.0.x`, alongside whatever else the job needs).

## Residual risk (consciously accepted)

Per-PR built scanning was dropped, so there is no new post-approval exposure on CodeQL. The mirror's only consumers are master/schedule runs (no fork code). The pre-existing E2E post-approval trust model (a maintainer reviews the changeset before approving a fork PR that gets secrets) is unchanged by this plan.

## Verified facts this rests on

- Mod build needs exactly those 4 DLLs + SMAPI, resolved via `$(GamePath)` (`mod/JunimoServer/JunimoServer.csproj:52-69`; `mod/JunimoServer.Shared/JunimoServer.Shared.csproj:14-15`).
- The existing mod build happens only inside `docker build` (`docker/Dockerfile:54-99`); CodeQL cannot trace an in-Docker compile, so a new host `dotnet build` is required (not reuse).
- `actions/cache` is fork-PR-restorable from master → illegal for proprietary DLLs (GitHub dependency-caching docs).
- Private GHCR is auth-gated, not ref-scoped → fork-safe for DLLs.
- Same-language two-scan coexistence needs distinct `--sarif-category`; different refs avoid the clash entirely → master-built vs PR-buildless is clean (GitHub SARIF / CodeQL CLI docs).
- `push:[master]` / `schedule` never carry fork context → the built scan needs no gate (`codeql.yml:8-16`).
- GHCR private-push pattern already in the repo: `docker/login-action` + `GITHUB_TOKEN` + `packages: write` (`build-image.yml:51-64`).

## Alternative (if the mirror is ever unwanted)

Accept the warning and document it: the 81 % call-target gap is entirely proprietary-DLL calls, which is not where vulnerabilities in this repo's own code hide. Zero cost, banner stays but is explained. This is the fallback if the private-mirror maintenance or licensing posture is later judged not worth it.
