# CI/CD Pipelines

JunimoServer uses GitHub Actions for automated building, testing, and deployment. This guide covers the pipelines relevant to server operators and contributors.

## Overview

| Pipeline | Trigger | Purpose |
|----------|---------|---------|
| [Release](#release-pipeline) | Merge to `master` | Creates releases and publishes stable Docker images |
| [Preview Build](#preview-build-pipeline) | Push to `master` | Builds and publishes preview Docker images |
| [Deploy Server](#deploy-server-pipeline) | After preview build / manual | Deploys server instances to VPS |

## Release Pipeline

The release pipeline handles versioning, changelog generation, and publishing stable Docker images to DockerHub.

### How It Works

1. **Automatic Version Management** - Uses [release-please](https://github.com/googleapis/release-please) to analyze commits and determine version bumps
2. **Changelog Generation** - Automatically generates changelogs from conventional commit messages
3. **Docker Image Publishing** - Builds and pushes `sdvd/server` and `sdvd/steam-service` images

### Versioning

Version bumps are determined by commit message prefixes:

| Prefix | Version Bump | Example |
|--------|--------------|---------|
| `feat:` | Minor (1.0.0 → 1.1.0) | New feature added |
| `fix:` | Patch (1.0.0 → 1.0.1) | Bug fix |
| `feat!:` or `BREAKING CHANGE:` | Major (1.0.0 → 2.0.0) | Breaking change |

### Docker Images

On release, images are tagged with:
- `sdvd/server:latest` - Latest stable version
- `sdvd/server:X.Y.Z` - Specific version (e.g., `1.4.0`)

```sh
# Pull latest stable release
docker pull sdvd/server:latest

# Pull specific version
docker pull sdvd/server:1.4.0
```

## Preview Build Pipeline

The preview build pipeline creates pre-release Docker images for testing new features before they're officially released.

### When It Runs

- Automatically on every push to `master`
- After pull requests are merged

### Preview Versioning

Preview versions follow the format: `X.Y.Z-preview.N`

- `X.Y.Z` - The next expected release version
- `N` - Preview counter (increments with each build)

Example: `1.5.0-preview.3` is the third preview build for the upcoming 1.5.0 release.

### Docker Images

Preview images are tagged with:
- `sdvd/server:preview` - Latest preview build
- `sdvd/server:X.Y.Z-preview.N` - Specific preview version

```sh
# Pull latest preview
docker pull sdvd/server:preview

# Use preview in docker-compose.yml
services:
  server:
    image: sdvd/server:preview
```

::: warning
Preview builds may contain experimental features or bugs. Use stable releases for production servers.
:::

## Deploy Server Pipeline

The deploy server pipeline deploys server instances to a VPS. It supports multiple instances (preview, latest) that can be individually enabled or disabled.

### When It Runs

- **Automatically** after a successful preview build (deploys preview instance only)
- **Manually** via GitHub Actions "Run workflow" button (choose which instance to deploy)

### Instances

| Instance | Image Tag | Default Path | Default Enabled |
|----------|-----------|--------------|-----------------|
| `preview` | `preview` | `/srv/sdvd/preview` | Yes |
| `latest` | `latest` | `/srv/sdvd/latest` | No |

### Enabling/Disabling Instances

Control which instances are deployed via **Repository Variables** (Settings → Variables → Actions):

| Variable | Default | Description |
|----------|---------|-------------|
| `DEPLOY_PREVIEW_ENABLED` | `true` | Enable preview instance deployment |
| `DEPLOY_LATEST_ENABLED` | `false` | Enable latest (stable) instance deployment |

### Setup Requirements

To use this pipeline, configure the following GitHub Secrets in your repository:

**SSH Connection:**

| Secret | Required | Description |
|--------|----------|-------------|
| `DEPLOY_SSH_HOST` | Yes | Server IP address or hostname |
| `DEPLOY_SSH_USER` | Yes | SSH username |
| `DEPLOY_SSH_KEY` | Yes | SSH private key (Ed25519 recommended) |
| `DEPLOY_SSH_PORT` | No | SSH port (defaults to 22) |

**Deploy Paths (optional, have sensible defaults):**

| Secret | Default | Description |
|--------|---------|-------------|
| `DEPLOY_PATH_PREVIEW` | `/srv/sdvd/preview` | Preview instance deploy directory |
| `DEPLOY_PATH_LATEST` | `/srv/sdvd/latest` | Latest instance deploy directory |

**Application Secrets:**

| Secret | Required | Description |
|--------|----------|-------------|
| `DEPLOY_STEAM_USERNAME` | Yes | Steam account username |
| `DEPLOY_STEAM_PASSWORD` | No* | Steam account password |
| `DEPLOY_STEAM_REFRESH_TOKEN` | No* | Steam OAuth refresh token |
| `DEPLOY_VNC_PASSWORD` | Yes | VNC access password |

*\*Steam authentication: Provide `DEPLOY_STEAM_PASSWORD` OR `DEPLOY_STEAM_REFRESH_TOKEN` (or both—if both are set, refresh token is used).*

### VPS Preparation

Before the pipeline can deploy, prepare your VPS:

**1. Install Docker**

```sh
curl -fsSL https://get.docker.com | sh
apt-get install docker-compose-plugin
```

**2. Create Deploy Directories**

```sh
mkdir -p /srv/sdvd/preview
mkdir -p /srv/sdvd/latest  # If using latest instance
```

**3. Configure Firewall**

```sh
# Preview instance
ufw allow 24642/udp  # Game port
ufw allow 5800/tcp   # VNC web interface

# Latest instance (use different ports if running both)
# ufw allow 24643/udp
# ufw allow 5801/tcp
```

**4. Create SSH Key**

Generate a dedicated deployment key:

```sh
ssh-keygen -t ed25519 -C "github-deploy" -f ~/.ssh/github-deploy
```

Add the public key to `~/.ssh/authorized_keys` on your server, and add the private key as the `DEPLOY_SSH_KEY` secret in GitHub.

### Manual Deployment

To manually trigger a deployment:

1. Go to **Actions** → **Deploy Server**
2. Click **Run workflow**
3. Select which instance to deploy: `preview`, `latest`, or `all`
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

## Discord Notifications

All pipelines can send notifications to Discord when builds complete or deployments finish.

To enable notifications, add the `DISCORD_WEBHOOK_URL` secret to your repository with a Discord webhook URL.

## Troubleshooting

### Preview Build Failed

1. Check the Actions tab for error details
2. Verify Docker Hub credentials (`DOCKERHUB_USERNAME`, `DOCKERHUB_TOKEN`)
3. Ensure Steam credentials are valid for game download

### Deployment Failed

1. Check SSH connectivity: `ssh -p PORT USER@HOST`
2. Verify the deploy directory exists and is writable
3. Check Docker is running on VPS: `docker ps`
4. Review container logs: `docker compose logs -f`

### Release Not Created

1. Ensure commits follow [conventional commit](https://www.conventionalcommits.org/) format
2. Check for an open "release please" PR in the repository
3. Merge the release PR to trigger the actual release

## Next Steps

- [Upgrading](/guide/upgrading) - Learn how to upgrade your server
- [Contributing](/community/contributing) - Contribute to JunimoServer development
