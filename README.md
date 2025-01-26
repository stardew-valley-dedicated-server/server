> [!IMPORTANT]
> JunimoServer is currently **NOT MAINTAINED** and open for a new maintainer.
>
> The project is **NOT FINISHED** and you will encounter bugs, missing features, or occasional crashes. However, join our [Discord](https://discord.gg/w23GVXdSF7) to connect with the community. Check out the list of known issues [here](https://github.com/stardew-valley-dedicated-server/server/issues).

# JunimoServer

![GitHub Tag](https://img.shields.io/github/v/tag/stardew-valley-dedicated-server/server?label=Latest%20Release&style=flat&colorA=18181B)
![Static Badge](https://img.shields.io/badge/Stardew%20Valley-v1.6.15-34D058?style=flat&colorA=18181B)
[![Discord](https://img.shields.io/discord/947923329057185842?label=Discord&logo=discord&color=34D058&style=flat&colorA=18181B)](https://discord.gg/w23GVXdSF7)

**JunimoServer** makes [Stardew Valley](https://www.stardewvalley.net/) multiplayer hosting simple and flexible. Host your farm anytime, anywhere — on your local machine, a VPS, or a dedicated server.

This open-source project enables 24/7 multiplayer farms without needing to keep the game running on your machine. Players can connect at any time without requiring you to be online. With customizable settings, automated backups, and support for larger farms, JunimoServer makes multiplayer management easier than ever.

### Table of Contents

<!-- REGENRATE TOC: npx markdown-toc -i README.md -->

<!-- toc -->

- [Features](#features)
- [Documentation](#documentation)
- [Quick start](#quick-start)

<!-- tocstop -->

## Features
JunimoServer gives you everything you need to host Stardew Valley:
- **Always-On Hosting**: Keep your farm running 24/7 without needing to leave the game open.
- **Easy Management**: Control your server through a simple, web-based interface with admin capabilities.
- **Persistent Progress**: Protect your crops and ensure your farm continues to thrive, even when no one’s online.
- **Automatic Backups**: Regularly save your farm so you can easily restore it if something goes wrong.
- **Fully Customizable**: Change game modes, tweak settings, and optimize performance to fit your needs.
- **Mod-Friendly**: Supports SMAPI mods to enhance your Stardew Valley experience with customizations and extra content.

## Quick start
1. **Create Configuration**:

    Copy the `docker-compose.yml` and `.env.example` files from the repository, then rename `.env.example` to `.env` and open it to configure your server.

    Here is a minimal example configuration:

    ```sh
    # Steam Account Details (required for downloading the game server)
    STEAM_USER=""
    STEAM_PASS=""

    # VNC Server (for web-based administration access)
    VNC_PASSWORD=""
    ```

2. **Start the Server**:

    To start the server as a background process using your configuration, run `docker compose up -d`.

    To see logs, run `docker compose logs -f`.

3. **Stop the Server**:

    To save and stop the server, run `docker compose down`.

## Documentation
Explore the [full documentation](docs/1.getting-started/1.introduction.md) to get started. Here’s what you’ll find:
- **[Getting started](docs/1.getting-started/1.introduction.md):** Step-by-step instructions on setting up and managing your server.
- **[Guide](docs/2.guide/1.architecture.md):** Learn more about the key concepts behind the server.
- **[API](docs/3.api/):** Learn more about the concepts of the server.
- **[Community](docs/4.community/):** Find out how to get involved.
