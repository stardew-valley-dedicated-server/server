# JunimoServer

![GitHub Tag](https://img.shields.io/github/v/tag/stardew-valley-dedicated-server/server?label=Latest%20Release&style=flat&colorA=18181B)
![Static Badge](https://img.shields.io/badge/Stardew%20Valley-v1.6.15-34D058?style=flat&colorA=18181B)
[![Discord](https://img.shields.io/discord/947923329057185842?label=Discord&logo=discord&color=34D058&style=flat&colorA=18181B)](https://discord.gg/w23GVXdSF7)

**JunimoServer** makes [Stardew Valley](https://www.stardewvalley.net/) multiplayer hosting simple and flexible. Host your farm anytime, anywhere — on your local machine, a VPS, or a dedicated server.

This open-source project enables 24/7 multiplayer farms without needing to keep the game running on your machine. Players can connect at any time without requiring you to be online. With customizable settings, automated backups, and support for larger farms, JunimoServer makes multiplayer management easier than ever.

### Table of Contents

<!-- REGENERATE TOC: npx markdown-toc -i README.md -->

<!-- toc -->

-   [Features](#features)
-   [Quick start](#quick-start)
    -   [Prerequisites](#prerequisites)
    -   [Getting started](#getting-started)
    -   [Updating to a new version](#updating-to-a-new-version)
    -   [Using preview releases](#using-preview-releases)
-   [Documentation](#documentation)

<!-- tocstop -->

## Features

JunimoServer gives you everything you need to host Stardew Valley:

-   **Always-On Hosting**: Keep your farm running 24/7 without needing to leave the game open.
-   **Easy Management**: Control your server through a simple, web-based interface with admin capabilities.
-   **Persistent Progress**: Protect your crops and ensure your farm continues to thrive, even when no one’s online.
-   **Automatic Backups**: Regularly save your farm so you can easily restore it if something goes wrong.
-   **Fully Customizable**: Change game modes, tweak settings, and optimize performance to fit your needs.
-   **Mod-Friendly**: Supports SMAPI mods to enhance your Stardew Valley experience with customizations and extra content.

## Quick start

### Prerequisites

-   **Docker**: Install [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/Mac) or [Docker Engine](https://docs.docker.com/engine/install/) (Linux)
-   **Steam account**: A Steam account that owns Stardew Valley (required to download game files)

### Getting started

1. **Create Configuration**:

    Download the configuration files from GitHub:
    - [`docker-compose.yml`](https://github.com/stardew-valley-dedicated-server/server/blob/master/docker-compose.yml)
    - [`.env.example`](https://github.com/stardew-valley-dedicated-server/server/blob/master/.env.example)

    Rename `.env.example` to `.env` and configure your server. Here is a minimal example:

    ```sh
    # Steam Account Details (required for downloading the game server)
    STEAM_USERNAME=""
    STEAM_PASSWORD=""

    # VNC Server (for web-based administration access)
    VNC_PASSWORD=""
    ```

2. **First-Time Setup**:

    Run the interactive setup to authenticate with Steam and download the game files:

    ```sh
    docker compose run --rm -it steam-auth setup
    ```

    This will prompt you for Steam Guard authentication if enabled on your account.

3. **Start the Server**:

    To start the server as a background process, run `docker compose up -d`.

    To see logs, run `docker compose logs -f`.

4. **Stop the Server**:

    To save and stop the server, run `docker compose down`.

    Your save files and Steam session are stored in Docker volumes (`saves` and `steam-session`) and persist across restarts.

### Updating to a new version

When a new version is released, update your server with:

```sh
docker compose pull
docker compose down
docker compose up -d
```

### Using preview releases

> **Note:** JunimoServer is under heavy development. If the latest stable release isn't working for you, try the preview release — it often contains fixes that haven't been officially released yet. As the project matures, stable releases will become more reliable.

Preview builds are published automatically with every code change. To use a preview release, add this to your `.env` file:

```sh
# Use the latest preview build
IMAGE_VERSION=preview
```

To switch back to stable releases, remove the line or set it to `latest`:

```sh
IMAGE_VERSION=latest
```

After changing the version, run:

```sh
docker compose pull
docker compose down
docker compose up -d
```

You can also pin to a specific version (e.g., `IMAGE_VERSION=1.0.0` or `IMAGE_VERSION=1.1.0-preview.3`). Check [Docker Hub](https://hub.docker.com/r/sdvd/server/tags) for available tags.

## Documentation

Explore the [full documentation](https://stardew-valley-dedicated-server.github.io/server/) to get started. Here's what you'll find:

-   **[Getting Started](https://stardew-valley-dedicated-server.github.io/server/getting-started/introduction):** Step-by-step instructions on setting up and managing your server.
-   **[Server Guide](https://stardew-valley-dedicated-server.github.io/server/guide/using-the-server):** Learn how to use and manage your server.
-   **[Community](https://stardew-valley-dedicated-server.github.io/server/community/getting-help):** Find out how to get involved and get help.
