# CI/CD Pipelines

We use GitHub Actions for automated building, testing, and deployment.

## Overview

| Pipeline | Trigger | Purpose |
|----------|---------|---------|
| [Build Release](#build-release-pipeline) | Merge release candidate PR to `master` | Creates releases and publishes stable Docker images |
| [Build Preview](#build-preview-pipeline) | Manual `workflow_dispatch`, or push to `master` when `AUTO_BUILD_PREVIEW=true` | Builds and publishes preview Docker images |
| [Validate PR](#validate-pr-pipeline) | Pull requests to `master` | Validates commits, builds, formatting, and line endings |
| [Validate Merge Group](#merge-queue) | Merge queue (`merge_group`) | Re-validates each PR against the latest `master` before it merges |
| [CodeQL](#codeql-pipeline) | Pull requests / push to `master` / weekly | Static security analysis (advisory) |
| [E2E Tests](#e2e-tests-pipeline) | Manual: `workflow_dispatch`, or a maintainer's `/run-tests-e2e` PR comment / re-run checkbox | Runs the Docker E2E suite on a remote VPS (never a required check) |
| [Deploy Server](#deploy-server-pipeline) | After preview build / manual | Deploys server instances to VPS |
| [Deploy Docs](#deploy-docs-pipeline) | After build / manual | Deploys documentation to GitHub Pages |
| [Cleanup Preview Tags](#cleanup-preview-tags) | Weekly schedule / manual | Deletes old preview tags from DockerHub |
| [Cleanup Caches](#cleanup-caches) | Weekly schedule / manual | Removes stale GitHub Actions caches |

## Build Release Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/build-release.yml)

The release pipeline handles version bumping, changelog generation, and publishing stable Docker images to DockerHub once a [release-please](https://github.com/googleapis/release-please) release candidate PR has been merged to master.

### Versioning

Version bumps are determined by commit message prefixes:

| Prefix | Version Bump | Example |
|--------|--------------|---------|
| `fix:` | Patch (1.0.0 → 1.0.1) | Bug fix |
| `feat:` | Minor (1.0.0 → 1.1.0) | New feature added |
| `feat!:` or `BREAKING CHANGE:` | Major (1.0.0 → 2.0.0) | Breaking change |

### Docker Images

On release, images are tagged with:

- `sdvd/server:latest` - Latest stable version
- `sdvd/server:X.Y.Z` - Specific version (e.g., `1.5.0`)

```sh
# Pull latest stable release
docker pull sdvd/server:latest

# Pull specific version
docker pull sdvd/server:1.5.0
```

## Build Preview Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/build-preview.yml)

::: warning
Preview builds may contain experimental features or bugs. Use stable releases for production servers.
:::

The preview build pipeline creates pre-release Docker images for testing new features before they're officially released. It runs two ways:

- **Manually** — a maintainer triggers it from **Actions → Build Preview → Run workflow** (`workflow_dispatch`). This always runs.
- **Automatically on push to `master`** (except docs-only or test-only changes) — but **only when the `AUTO_BUILD_PREVIEW` repository variable is set to `true`**. It is unset by default, so merges do **not** auto-publish a preview unless a maintainer opts in. This avoids a throwaway preview image per merge.

A `gate` job enforces this: a manual run skips the variable check, while a push run proceeds only if `AUTO_BUILD_PREVIEW == 'true'`. When the gate is skipped, the whole workflow is skipped (every job roots at it). Set the variable under **Settings → Secrets and variables → Actions → Variables**.

### Preview Versioning

Preview versions follow the format: `X.Y.Z-preview.N`

- `X.Y.Z` - The next expected release version
- `N` - Preview counter (increments with each build)

Example: `1.5.0-preview.3` is the third preview build for the upcoming 1.5.0 release.

### Preview Docker Images

Preview images are tagged with:

- `sdvd/server:preview` - Latest preview build
- `sdvd/server:X.Y.Z-preview.N` - Specific preview version (e.g., `1.5.0-preview.3`)

```sh
# Pull latest preview
docker pull sdvd/server:preview

# Use preview in docker-compose.yml
services:
  server:
    image: sdvd/server:preview
```

### Batching Features

You can merge multiple features before releasing. The release-please Release PR accumulates on every merge regardless of how previews are built:

```
Day 1: Merge feat A → Release PR created (1.0.2 → 1.1.0)
Day 2: Merge feat B → Release PR updated (1.0.2 → 1.2.0)

Day 3: Get a preview to test — either dispatch Build Preview,
       or (with AUTO_BUILD_PREVIEW=true) it auto-published on each merge.
       Test 1.2.0-preview.N thoroughly.

Day 4: Merge Release PR → v1.2.0 released
```

The Release PR automatically updates as you merge more commits; the preview counter `N` increments on each preview build for the same target version, whether that build was dispatched manually or auto-triggered on push.

## Cleanup Preview Tags

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/cleanup-preview-tags.yml)

Over time, versioned preview tags (`X.Y.Z-preview.N`) accumulate on DockerHub. This pipeline removes old ones, keeping the 10 most recent per repository (`server`, `steam-service`, `discord-bot`).

The floating `preview`, `latest`, and release `X.Y.Z` tags are never touched.

### When It Runs

- **Weekly** on Monday at 06:00 UTC
- **Manually** via GitHub Actions "Run workflow" button

### Manual Options

| Input | Default | Description |
|-------|---------|-------------|
| `keep_count` | `10` | Number of most recent preview tags to keep |
| `dry_run` | `false` | List tags that would be deleted without deleting |

## Validate PR Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/validate-pr.yml)

The validation pipeline runs on every pull request targeting `master`. It ensures code quality before merging.

### What It Validates

- **PR title** - Must follow [Conventional Commits](https://www.conventionalcommits.org/) format. The repo squash-merges, so the title becomes the commit subject the merge queue lints — checking it here fails a bad title on the PR rather than cryptically in the queue.
- **Commit messages** - Must follow [Conventional Commits](https://www.conventionalcommits.org/) format
- **Docker build** - Ensures the image builds successfully (without pushing)
- **Formatting** - Runs `dotnet csharpier check .` over the whole tree; fails on any C# formatting drift (fix locally with `make lint-fix`)
- **JS/TS** - Runs `biome ci` over the projects scoped in the root `biome.jsonc`; fails on formatting drift or lint errors (fix locally with `make lint-fix`)
- **Line endings** - Fails if a file with CRLF line endings reached the index, bypassing the LF normalization `.gitattributes` enforces

These surface as required status checks — `Validate PR Title`, `Validate Build`, `Validate Commits`, `Validate Formatting`, `Validate JS/TS`, and `Validate Line Endings` — that must pass before a PR can merge.

### Trigger & Security Model

The pipeline triggers on `pull_request_target`. Unlike `pull_request`, this event runs the workflow file and grants secrets from the **base** repository, not the PR head — which is what lets fork PRs be built with the Steam credentials the Docker image needs. It also means fork code is running in a privileged context, so access is gated:

1. **`authorize`** — runs first. Its `environment:` is chosen by an expression: fork PRs resolve to **`fork-pr`** (a required reviewer must approve before the job — and therefore the rest of the pipeline — proceeds); same-repo and Renovate PRs resolve to an empty string, i.e. **no environment**, so the job passes instantly with no approval.
2. **`validate-commits`**, **`validate-line-endings`**, **`validate-format`**, **`validate-js`**, and **`validate-build`** declare `needs: authorize`, so none starts until the gate passes. For a fork PR this means a maintainer reviews the diff before fork code is checked out or secrets are exposed.

`validate-commits` only reads commit metadata and base-repo files (it never checks out the fork head), so it is safe under the privileged trigger. The other four check out the fork head — and `validate-build` additionally uses the Steam secrets — which is exactly why they sit behind the `authorize` gate.

::: warning
Keep this a single `pull_request_target` trigger. Adding `pull_request` back produces duplicate check entries (one per event), and the build job must keep `needs: authorize` rather than its own `environment:` — otherwise fork PRs are gated twice.
:::

### GitHub Environment

The pipeline uses a single GitHub Environment, `fork-pr`, purely as an authorization gate (it holds no deploy secrets):

| Environment | Used for | Protection rules |
|-------------|----------|------------------|
| `fork-pr` | Fork PRs — pauses the pipeline for maintainer approval before fork code or secrets run | Required reviewer |

Same-repo and Renovate PRs resolve the `authorize` job's `environment:` expression to an empty string, which GitHub treats as **no environment** — so no gate, no approval, and nothing extra in the repo's environment list.

## Merge Queue

Merges to `master` go through a [GitHub merge queue](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/configuring-pull-request-merges/managing-a-merge-queue). You do not merge a PR directly — once it is approved and its checks pass, enabling auto-merge adds it to the queue, and GitHub merges it for you.

### How a PR merges

1. The PR passes its [Validate PR](#validate-pr-pipeline) checks and receives the required approval.
2. Enabling auto-merge (or, for Renovate PRs, Renovate arming it automatically) hands the PR to the queue.
3. The queue builds a temporary `gh-readonly-queue/master/...` branch containing the latest `master` plus the PR's changes, and runs the required checks against it. This is what the **Validate Merge Group** workflow ([`validate-merge-group.yml`](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/validate-merge-group.yml)) validates — the PR is re-tested against the current tip of `master`, not the stale base it was branched from.
4. If those checks pass, the queue fast-forwards `master`. PRs are merged one at a time, each squashed into a single commit.

A PR sitting in the queue shows **`AWAITING_CHECKS`** while its merge-group build runs, and **`UNMERGEABLE`** if its changes no longer apply cleanly on top of the current `master` (typically because an overlapping PR merged ahead of it). An unmergeable PR is dropped from the queue; rebasing it onto `master` and re-queuing resolves it.

### Why Validate Merge Group is a separate workflow

The merge queue fires the `merge_group` event, which [Validate PR](#validate-pr-pipeline) does not respond to (it triggers on `pull_request_target`). The merge queue requires the same `Validate Build`, `Validate Commits`, `Validate Formatting`, `Validate JS/TS`, `Validate Line Endings`, and `Validate PR Title` checks to report **on the merge-group ref**, so `validate-merge-group.yml` reproduces all six under the same names. All but the title check run the same commitlint, Docker build, CSharpier, Biome, and line-ending scans, but without the `authorize` gate — merge-group code is already approved and runs from the base repository, so there is no fork-secret exposure to gate.

`Validate PR Title` is the exception: there is nothing to re-lint in the queue. The `merge_group` payload carries no `pull_request.title`, the title is immutable once a PR is queued (it was already linted at PR time), and the queue branch holds the PR's original commits — not the squash subject — so `Validate Commits` doesn't cover it either. Its merge-group job is therefore a no-op that exists only to report the required status; without it the queue would wait on the title check forever.

::: warning
All six required checks (`Validate Build`, `Validate Commits`, `Validate Formatting`, `Validate JS/TS`, `Validate Line Endings`, `Validate PR Title`) must have a `merge_group` producer. A required check with no merge-group workflow leaves every queued PR stuck in `AWAITING_CHECKS` until the queue's timeout. If you add a required check, make sure it reports on `merge_group` too — even if, like the title check, the merge-group job is only a stub that satisfies the contract.
:::

## CodeQL Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/codeql.yml)

[CodeQL](https://codeql.github.com/) runs GitHub's static security analysis over the codebase. It is configured as **advanced setup** — a committed workflow that gives full control over languages, triggers, and path filters. The workflow runs on pull requests, on push to `master`, and on a weekly schedule (Wednesday 07:17 UTC).

### Advisory, Not Required

CodeQL is **not** a required status check — a PR merges on `Validate Build` + `Validate Commits` + `Validate Line Endings` alone, and CodeQL findings surface under **Security → Code scanning** without blocking the merge.

This is deliberate. The pipeline uses per-language path scoping (below), so a PR that touches no analyzable source runs **zero** analyze jobs. A *required* check that never reports leaves a PR stuck on "Expected — Waiting for status", so the path-scoping optimization is only safe while CodeQL stays advisory.

::: warning
If CodeQL is ever promoted to a required check, this pipeline must be revisited: a required check needs a `merge_group` producer (like `validate-merge-group.yml`) and a way to report even when path-scoped out. The current advisory design has neither, on purpose.
:::

### Languages Analyzed

Three languages are analyzed. C# uses `build-mode: none`, so CodeQL builds its database from source directly with no Docker or `dotnet build`; the other two need no build step at all:

| Language | Covers |
|----------|--------|
| `csharp` | The SMAPI mod, shared library, E2E tests, runner, and tools |
| `javascript-typescript` | The Vue/TypeScript test UI and docs site (JS, TS, and Vue in one unified language) |
| `actions` | The GitHub Actions workflows themselves |

C/C++ is intentionally excluded: the only `.c`/`.h` files in the repo are deployment shims under `docker/modern/` (`pthread_shim.c`, `steamclient_stub.c`), not application source worth scanning.

### Per-Language Path Scoping

A fast `changes` job runs first and emits a JSON array of just the languages whose files changed in the PR. The `analyze` job consumes that array as its build matrix, so **only the relevant analyze jobs are ever created** — there are no skipped-job rows to read past.

- A PR that touches only a Dockerfile (e.g. a base-image bump) creates **zero** analyze jobs.
- A `.cs` or `.csproj` change creates only `Analyze (csharp)`.
- A `package.json`, `.ts`, or `.vue` change creates only `Analyze (javascript-typescript)`.
- A `.github/workflows/**` change creates only `Analyze (actions)`.

On push to `master` and on the weekly schedule there is no PR diff base, so the `changes` job emits all three languages and the full scan runs — the safety net for anything the per-PR scoping skipped.

### Trigger & Fork Safety

CodeQL triggers on `pull_request`, **not** `pull_request_target` (the opposite choice from [Validate PR](#validate-pr-pipeline)). It analyzes the PR head read-only with the default `GITHUB_TOKEN` and needs no secrets, so it is safe to run on fork PRs — fork code must be read to be scanned, but no secret is ever exposed to it. It is not run on `merge_group`: that event is only for required checks, and running an advisory scan there would be pure waste.

## E2E Tests Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/e2e-tests.yml)

Runs the heavy Docker E2E suite. The coordinator (`JunimoServer.TestRunner`) runs on the GitHub runner; the actual Stardew game containers run on a **remote VPS over SSH**. It is **manual and maintainer-gated** — never an automatic merge gate, and **never a required check** (an external VPS being down must not block the queue). For how to *use* it (triggers, results, the re-run checkbox), see [E2E Testing → CI Usage](../testing/e2e-testing.md#ci-usage); this section covers the pipeline's safety model and one-time setup.

### Three entry points

| Trigger | How |
|---------|-----|
| `workflow_dispatch` | Actions tab → **Run workflow** (full suite from a trusted branch; optional `filter`). |
| `/run-tests-e2e [filter]` | A PR comment (the `issue_comment: created` event). Runs against the PR's HEAD. |
| **Re-run checkbox** | Ticking "🔁 Re-run E2E tests" in the bot's results comment (`issue_comment: edited`). |

### Trigger & Fork Safety

The PR-comment path is privileged (it reaches the VPS SSH key = root on the test VPS via the docker group), so it is gated in layers:

- **`issue_comment` always runs the workflow file from the default branch** (`master`), never the PR/fork copy — a fork cannot inject workflow code via a comment.
- The **`gate` job** (no secrets) authorizes the **event actor** (`github.event.sender`, not the comment author — they differ on a checkbox edit) via the repo-permission API (`getCollaboratorPermissionLevel`; **write/admin required**, `author_association` is deliberately not used; a non-collaborator's `404` is a deny; a `403` fails closed). Non-maintainers get a 👎 + a "not authorized" reply and **no secret-bearing job runs**.
- **Fork PRs** additionally pass through the **`fork-pr` GitHub Environment** approval (a required reviewer) on the `authorize` job before the secret-bearing `e2e` job runs. Same-repo PRs resolve no environment (no prompt). This mirrors [Validate PR](#validate-pr-pipeline)'s `authorize` gate.
- The `e2e` job checks out the **PR HEAD at a pinned SHA** (resolved by the gate) to build and test the proposed code — this is the intended behaviour, gated by the fork-pr approval. The PR-sticky helper is loaded from a **separate trusted (default-branch) checkout**, never from the PR checkout, so fork code can't run with secrets through our own tooling.
- **Single VPS runner:** a global `concurrency` singleton means runs **queue** (active + at most one pending); a newer trigger replaces the waiting one and never preempts the active run. To preempt, a maintainer cancels the in-flight run from the Actions tab — kept off the comment surface deliberately, since `cancel-in-progress` is evaluated *before* the gate authorizes, so a comment-driven cancel would let a non-maintainer grief the queue. The cancelled run reports "⚪ aborted".

### One-time setup (required for the PR path to be safe)

1. **`fork-pr` Environment** (Settings → Environments) — must exist with **at least one required reviewer**. This is the load-bearing control for fork PRs; without a reviewer the approval auto-passes and fork code would reach the secrets. (Shared with Validate PR.)
2. **`test-vps` Environment** — holds the run secrets: `SDVD_DOCKER_HOSTS` (the host-fleet JSON with the inline SSH key) and `STEAM_ACCOUNTS`. The Cloudflare-R2 report publish uses `R2_ACCESS_KEY_ID` / `R2_SECRET_ACCESS_KEY` / `R2_ACCOUNT_ID` (secrets) plus `R2_BUCKET` / `R2_PUBLIC_BASE_URL` (**variables**, not secrets — they contain a hyphen that the secret masker would over-mask). See [E2E Testing → Hosted report](../testing/e2e-testing.md#hosted-report-cloudflare-r2).

### Helper script & tests

The PR sticky-comment logic lives in [`.github/scripts/e2e-pr-sticky.js`](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/scripts/e2e-pr-sticky.js) (pure functions + thin GitHub-API wrappers). Its unit tests (`e2e-pr-sticky.test.js`) use Node's built-in runner — run them with `npm test`. These cover the command parsing, the re-run checkbox state machine, the marker round-tripping, the run-history cap, the filter validation, and the maintainer-auth fail-closed behaviour.

To lint the workflow YAML itself, run [actionlint](https://github.com/rhysd/actionlint) via its Docker image (no local install needed):

```bash
docker run --rm -v "$PWD:/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/e2e-tests.yml
```

## Deploy Docs Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/deploy-docs.yml)

Deploys the documentation site to GitHub Pages. Runs automatically after builds or can be triggered manually to rebuild from existing Docker images.

## Deploy Server Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/deploy-server.yml)

The deploy server pipeline deploys server instances to a VPS. It supports multiple environments that can be individually configured.

### When It Runs

- **Automatically** after a successful preview build
- **Automatically** when a release is published
- **Manually** via GitHub Actions "Run workflow" button

### Adding a New Server

1. **Create a GitHub Environment** matching your server name
2. **Add the environment to the workflow matrix** in `.github/workflows/deploy-server.yml`
3. **Update the workflow dispatch options** to include the new environment

Example matrix entry:

```yaml
matrix:
    include:
        - environment: public-test
          image_tag: preview
          on_preview: true
          on_release: false

        - environment: production
          image_tag: latest
          on_preview: false
          on_release: true
```

### Setup Requirements

Each deployment target needs a **GitHub Environment** with its configuration.

#### Creating Environments

1. Go to **Settings** → **Environments** in your repository
2. Click **New environment**
3. Name it to match the workflow matrix (e.g., `public-test`, `production`)
4. Add the secrets listed below

#### Environment Secrets

All secrets use the `DEPLOY_` prefix.

| Secret | Required | Description |
|--------|----------|-------------|
| `DEPLOY_API_KEY` | No | API key for authenticating API/WebSocket requests |
| `DEPLOY_DISCORD_BOT_TOKEN` | No | Discord bot token for status display |
| `DEPLOY_DISCORD_CHAT_CHANNEL_ID` | No | Discord channel ID for chat relay |
| `DEPLOY_GAME_PORT` | Yes | UDP port for game connections |
| `DEPLOY_SSH_HOST` | Yes | Server IP address or hostname |
| `DEPLOY_SSH_KEY` | Yes | SSH private key (Ed25519 recommended) |
| `DEPLOY_SSH_PORT` | No | SSH port (defaults to 22) |
| `DEPLOY_SSH_USER` | Yes | SSH username |
| `DEPLOY_STEAM_AUTH_PORT` | Yes | TCP port for Steam auth service |
| `DEPLOY_STEAM_PASSWORD` | No¹ | Steam account password |
| `DEPLOY_STEAM_REFRESH_TOKEN` | No¹ | Steam OAuth refresh token |
| `DEPLOY_STEAM_USERNAME` | Yes | Steam account username |
| `DEPLOY_VNC_PASSWORD` | Yes | VNC access password |
| `DEPLOY_VNC_PORT` | Yes | TCP port for VNC web interface |

_¹ Steam authentication: Provide `DEPLOY_STEAM_PASSWORD` OR `DEPLOY_STEAM_REFRESH_TOKEN` (or both; if both are set, refresh token is used)._

::: tip API Key
Generate a secure API key with: `openssl rand -base64 32`
:::

::: tip
If multiple servers share the same VPS and credentials, **repository-level** secrets can be used as fallbacks. Environment-level secrets override repository-level secrets with the same name.
:::

### VPS Preparation

Before the pipeline can deploy, prepare your VPS.

**1. Install Docker**

```sh
curl -fsSL https://get.docker.com | sh
apt-get install docker-compose-plugin
```

**2. Create Deploy User**

Run the setup script from the repository (as root):

```sh
curl -fsSL https://raw.githubusercontent.com/stardew-valley-dedicated-server/server/master/tools/create-ssh-user.sh | bash
```

This creates a `github_deploy` user with:
- Docker group membership
- SSH key for authentication
- Deploy directory at `~/srv/` (environments deploy to `~/srv/<environment-name>`)

The script outputs the private key to add as `DEPLOY_SSH_KEY` in GitHub.

**3. Configure Firewall**

```sh
# Example for public-test environment
ufw allow 24642/udp  # Game port
ufw allow 5800/tcp   # VNC web interface
```

### Manual Deployment

To manually trigger a deployment:

1. Go to **Actions** → **Deploy Server**
2. Click **Run workflow**
3. Select which environment to deploy (e.g., `public-test`)
4. Optionally check "Skip graceful shutdown" for emergency deploys
5. Click **Run workflow**

### What Gets Deployed

The pipeline:

1. Creates/updates `.env` file with secrets and correct `IMAGE_VERSION`
2. Copies `docker-compose.yml` to VPS
3. Pulls the appropriate Docker images
4. Restarts containers
5. Verifies deployment health

::: tip
The pipeline uses the same `docker-compose.yml` from the repository, ensuring consistency between local development and deployed environments. The `IMAGE_VERSION` environment variable controls which image tag is used.
:::

## Cleanup Caches

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/cleanup-caches.yml)

GitHub Actions caches can accumulate over time. This pipeline removes caches that haven't been accessed in 14 days.

### When It Runs

- **Weekly** on Sunday at 06:00 UTC
- **Manually** via GitHub Actions "Run workflow" button

## Discord Notifications

Most pipelines try to send notifications to Discord when builds complete or deployments finish.

To enable notifications, the `DISCORD_WEBHOOK_URL` repository secret needs to be set with a [Discord webhook URL](https://support.discord.com/hc/en-us/articles/228383668-Intro-to-Webhooks).
