# CI/CD Pipelines

JunimoServer uses GitHub Actions for automated building, testing, and deployment. This guide covers the pipelines relevant to server operators and contributors.

## Overview

| Pipeline | Trigger | Purpose |
|----------|---------|---------|
| [Release](#release-pipeline) | Merge to `master` | Creates releases and publishes stable Docker images |
| [Preview Build](#preview-build-pipeline) | Push to `master` | Builds and publishes preview Docker images |
| [Deploy Preview](#deploy-preview-pipeline) | After preview build | Deploys preview images to VPS |

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

## Deploy Preview Pipeline

The deploy preview pipeline automatically deploys preview builds to a VPS for testing.

### When It Runs

- Automatically after a successful preview build
- Manually via GitHub Actions "Run workflow" button

### Setup Requirements

To use this pipeline, configure the following GitHub Secrets in your repository:

**SSH Connection:**

| Secret | Required | Description |
|--------|----------|-------------|
| `VPS_SSH_HOST` | Yes | VPS IP address or hostname |
| `VPS_SSH_USER` | Yes | SSH username |
| `VPS_SSH_PRIVATE_KEY` | Yes | SSH private key (Ed25519 recommended) |
| `VPS_SSH_PORT` | No | SSH port (defaults to 22) |

**Application Secrets:**

| Secret | Required | Description |
|--------|----------|-------------|
| `VPS_STEAM_USERNAME` | Yes | Steam account username |
| `VPS_STEAM_PASSWORD` | No* | Steam account password |
| `VPS_STEAM_REFRESH_TOKEN` | No* | Steam OAuth refresh token |
| `VPS_VNC_PASSWORD` | Yes | VNC access password |

*\*Steam authentication: Provide `VPS_STEAM_PASSWORD` OR `VPS_STEAM_REFRESH_TOKEN` (or both—if both are set, refresh token is used).*

### VPS Preparation

Before the pipeline can deploy, prepare your VPS:

**1. Install Docker**

```sh
curl -fsSL https://get.docker.com | sh
apt-get install docker-compose-plugin
```

**2. Create Deploy Directory**

```sh
mkdir -p /srv/sdvd/preview
```

**3. Configure Firewall**

```sh
ufw allow 24642/udp  # Game port
ufw allow 5800/tcp   # VNC web interface
```

**4. Create SSH Key**

Generate a dedicated deployment key:

```sh
ssh-keygen -t ed25519 -C "github-deploy" -f ~/.ssh/github-deploy
```

Add the public key to `~/.ssh/authorized_keys` on your VPS, and add the private key as the `VPS_SSH_PRIVATE_KEY` secret in GitHub.

### Manual Deployment

To manually trigger a deployment:

1. Go to **Actions** → **Deploy Preview to VPS**
2. Click **Run workflow**
3. Optionally check "Skip graceful shutdown" for emergency deploys
4. Click **Run workflow**

### What Gets Deployed

The pipeline:
1. Creates/updates `.env` file with secrets
2. Copies `docker-compose.yml` to VPS
3. Pulls latest preview images
4. Restarts containers
5. Verifies deployment health

::: tip
The pipeline uses the same `docker-compose.yml` from the repository, ensuring consistency between local development and deployed environments.
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
