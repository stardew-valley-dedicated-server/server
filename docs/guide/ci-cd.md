# CI/CD Pipelines

We use GitHub Actions for automated building, testing, and deployment. This guide covers the pipelines relevant for maintainers of this project.

## Overview

| Pipeline                                      | Trigger                                | Purpose                                             |
| --------------------------------------------- | -------------------------------------- | --------------------------------------------------- |
| [Build Release](#build-release-pipeline)      | Merge release candidate PR to `master` | Creates releases and publishes stable Docker images |
| [Build Preview](#build-preview-pipeline)      | Push to `master`                       | Builds and publishes preview Docker images          |
| [Validate PR](#validate-pr-pipeline)          | Pull requests to `master`              | Validates commits and builds                        |
| [Deploy Server](#deploy-server-pipeline)      | After preview build / manual           | Deploys server instances to VPS                     |
| [Deploy Docs](#deploy-docs-pipeline)          | After build / manual                   | Deploys documentation to GitHub Pages               |
| [Cleanup Preview Tags](#cleanup-preview-tags) | Weekly schedule / manual               | Deletes old preview tags from DockerHub             |
| [Cleanup Caches](#cleanup-caches)             | Weekly schedule / manual               | Removes stale GitHub Actions caches                 |

## Build Release Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/build-release.yml)

The release pipeline handles version bumping, changelog generation, and publishing stable Docker images to DockerHub once a [release-please](https://github.com/googleapis/release-please) release candidate PR has been merged to master.

### Versioning

Version bumps are determined by commit message prefixes:

| Prefix                         | Version Bump          | Example           |
| ------------------------------ | --------------------- | ----------------- |
| `fix:`                         | Patch (1.0.0 → 1.0.1) | Bug fix           |
| `feat:`                        | Minor (1.0.0 → 1.1.0) | New feature added |
| `feat!:` or `BREAKING CHANGE:` | Major (1.0.0 → 2.0.0) | Breaking change   |

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

### Versioning

Preview versions follow the format: `X.Y.Z-preview.N`

- `X.Y.Z` - The next expected release version
- `N` - Preview counter (increments with each build)

Example: `1.5.0-preview.3` is the third preview build for the upcoming 1.5.0 release.

### Docker Images

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

## Cleanup Preview Tags

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/cleanup-preview-tags.yml)

Over time, versioned preview tags (`X.Y.Z-preview.N`) accumulate on DockerHub. This pipeline removes old ones, keeping the 10 most recent per repository (`server`, `steam-service`, `discord-bot`).

The floating `preview`, `latest`, and release `X.Y.Z` tags are never touched.

### When It Runs

- **Weekly** on Monday at 06:00 UTC
- **Manually** via GitHub Actions "Run workflow" button

### Manual Options

| Input        | Default | Description                                      |
| ------------ | ------- | ------------------------------------------------ |
| `keep_count` | `10`    | Number of most recent preview tags to keep        |
| `dry_run`    | `false` | List tags that would be deleted without deleting  |

## Validate PR Pipeline

[Open in Github](https://github.com/stardew-valley-dedicated-server/server/tree/master/.github/workflows/validate-pr.yml)

The validation pipeline runs on every pull request targeting `master`. It ensures code quality before merging.

### What It Validates

- **Commit messages** - Must follow [Conventional Commits](https://www.conventionalcommits.org/) format
- **Docker build** - Ensures the image builds successfully (without pushing)

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

| Secret                       | Required                               | Description                             |
| ---------------------------- | -------------------------------------- | --------------------------------------- |
| `DEPLOY_DISCORD_BOT_TOKEN`   | No                                     | Discord bot token for status display    |
| `DEPLOY_GAME_PORT`           | Yes                                    | UDP port for game connections           |
| `DEPLOY_SSH_HOST`            | Yes                                    | Server IP address or hostname           |
| `DEPLOY_SSH_KEY`             | Yes                                    | SSH private key (Ed25519 recommended)   |
| `DEPLOY_SSH_PORT`            | No                                     | SSH port (defaults to 22)               |
| `DEPLOY_SSH_USER`            | Yes                                    | SSH username                            |
| `DEPLOY_STEAM_AUTH_PORT`     | Yes                                    | TCP port for Steam auth service         |
| `DEPLOY_STEAM_PASSWORD`      | No <a id="tip-0-0" href="#tip-0">1</a> | Steam account password                  |
| `DEPLOY_STEAM_REFRESH_TOKEN` | No <a id="tip-0-1" href="#tip-0">1</a> | Steam OAuth refresh token               |
| `DEPLOY_STEAM_USERNAME`      | Yes                                    | Steam account username                  |
| `DEPLOY_VNC_PASSWORD`        | Yes                                    | VNC access password                     |
| `DEPLOY_VNC_PORT`            | Yes                                    | TCP port for VNC web interface          |

_<a id="tip-0" href="#tip-0-0">[1]</a> Steam authentication: Provide `DEPLOY_STEAM_PASSWORD` OR `DEPLOY_STEAM_REFRESH_TOKEN` (or both—if both are set, refresh token is used)._

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
