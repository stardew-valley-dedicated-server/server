# CI/CD Pipelines

We use GitHub Actions for automated building, testing, and deployment.

## Overview

| Pipeline | Trigger | Purpose |
|----------|---------|---------|
| [Build Release](#build-release-pipeline) | Merge release candidate PR to `master` | Creates releases and publishes stable Docker images |
| [Build Preview](#build-preview-pipeline) | Push to `master` | Builds and publishes preview Docker images |
| [Validate PR](#validate-pr-pipeline) | Pull requests to `master` | Validates commits and builds |
| [Validate Merge Group](#merge-queue) | Merge queue (`merge_group`) | Re-validates each PR against the latest `master` before it merges |
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

The preview build pipeline runs on every push to `master` (except docs-only or test-only changes) and creates pre-release Docker images for testing new features before they're officially released.

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

You can merge multiple features before releasing:

```
Day 1: Merge feat A → 1.1.0-preview.1 published
       Release PR created (1.0.2 → 1.1.0)

Day 2: Merge feat B → 1.2.0-preview.2 published
       Release PR updated (1.0.2 → 1.2.0)

Day 3: Test preview.2 thoroughly

Day 4: Merge Release PR → v1.2.0 released
```

The Release PR automatically updates as you merge more commits.

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

- **Commit messages** - Must follow [Conventional Commits](https://www.conventionalcommits.org/) format
- **Docker build** - Ensures the image builds successfully (without pushing)

These surface as two required status checks — `Validate Build` and `Validate Commits` — that must pass before a PR can merge.

### Trigger & Security Model

The pipeline triggers on `pull_request_target`. Unlike `pull_request`, this event runs the workflow file and grants secrets from the **base** repository, not the PR head — which is what lets fork PRs be built with the Steam credentials the Docker image needs. It also means fork code is running in a privileged context, so access is gated:

1. **`authorize`** — runs first. Its `environment:` is chosen by an expression: fork PRs resolve to **`fork-pr`** (a required reviewer must approve before the job — and therefore the rest of the pipeline — proceeds); same-repo and Renovate PRs resolve to an empty string, i.e. **no environment**, so the job passes instantly with no approval.
2. **`validate-commits`** and **`validate-build`** declare `needs: authorize`, so neither starts until the gate passes. For a fork PR this means a maintainer reviews the diff before fork code is checked out or secrets are exposed.

`validate-commits` only reads commit metadata and base-repo files (it never checks out the fork head), so it is safe under the privileged trigger. `validate-build` checks out the fork head and uses the Steam secrets — which is exactly why it sits behind the `authorize` gate.

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

The merge queue fires the `merge_group` event, which [Validate PR](#validate-pr-pipeline) does not respond to (it triggers on `pull_request_target`). The merge queue requires the same `Validate Build` and `Validate Commits` checks to report **on the merge-group ref**, so `validate-merge-group.yml` reproduces both under the same names. It runs the same commitlint and Docker build, but without the `authorize` gate — merge-group code is already approved and runs from the base repository, so there is no fork-secret exposure to gate.

::: warning
Both required checks (`Validate Build`, `Validate Commits`) must have a `merge_group` producer. A required check with no merge-group workflow leaves every queued PR stuck in `AWAITING_CHECKS` until the queue's timeout. If you add a required check, make sure it reports on `merge_group` too.
:::

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
