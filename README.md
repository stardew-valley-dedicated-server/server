# JunimoServer

![Static Badge](https://img.shields.io/badge/SVDS-0.0.2--alpha-34D058)
![Static Badge](https://img.shields.io/badge/Stardew%20Valley-1.6.15-34D058)
![Static Badge](https://img.shields.io/badge/SMAPI-4.1.10-34D058)

A customizable, headless dedicated server designed for running [Stardew Valley](https://www.stardewvalley.net/) on Linux. JunimoServer allows to host Stardew Valley games on your own server, providing greater flexibility and control over game settings, automated backups, and in-game stability. With JunimoServer, you can keep your farm up and running even while players are offline, ensuring your crops and farm activities continue without worry.

> [!IMPORTANT]
> This is an **unstable alpha release**. Expect bugs, incomplete features, and potential crashes. Join our [Discord](https://discord.gg/w23GVXdSF7) to report issues, get updates, and participate in community discussions. See current open bugs [here](https://github.com/stardew-valley-dedicated-server/server/issues).

## Table of Contents

<!-- REGENRATE TOC: npx markdown-toc -i README.md -->

<!-- toc -->

- [Features](#features)
- [Quick start](#quick-start)
- [Documentation](#documentation)
- [FAQ](#faq)
  * [What is the project status?](#what-is-the-project-status)
  * [Are there more planned features?](#are-there-more-planned-features)
  * [What are the minimum hardware requirements?](#what-are-the-minimum-hardware-requirements)
  * [Can I run this on Windows or macOS?](#can-i-run-this-on-windows-or-macos)
  * [Are there other Stardew Valley servers?](#are-there-other-stardew-valley-servers)
- [Credits & Contributors](#credits--contributors)

<!-- tocstop -->

## Features
JunimoServer provides essential features for dedicated Stardew Valley hosting:

- **Dedicated Hosting**: Runs on a Linux Docker image, enabling autonomous hosting without requiring player intervention.
- **Web-Based Administration**: Manage your server through a VNC-enabled web interface, offering convenient control over game settings and monitoring.
- **Persistent Farm State**: Implements a "CropSaver" feature to protect your farm's state even while players are offline, preventing crops from decaying and ensuring continuity.
- **Automated Backup and Restore**: Schedule regular backups of your farm's progress, so you can recover from any unexpected issues.
- **Flexible Configuration Options**: Allows customization of various settings, providing control over game modes, player access, and server performance optimizations.
- **Seamless SMAPI Integration**: Supports popular mods and customizations for enhanced gameplay experiences.


## Quick start
  1. **Copy Configuration Files**: Copy the `docker-compose.yml` and `.env.example` files. Rename `.env.example` to `.env` and open it to configure your server.

  2. **Set Environment Variables**: Update the `.env` file with your credentials and preferences. Here’s an example configuration:
      ```sh
      # Steam Account Details (required for downloading the game server)
      STEAM_USER="your_steam_username"
      STEAM_PASS="your_steam_password"

      # VNC Server (for web-based administration access)
      VNC_PASSWORD="your_secure_vnc_password"
      ```

  3. **Start the Server**: Once the configuration is complete, launch the server with Docker Compose:
      ```sh
      docker compose up -d
      ```

      You can monitor the server logs to ensure it's running correctly with:

      ```sh
      docker compose logs -f
      ```


 4. **Stop the Server**: To shut down the server, run:
      ```sh
      docker compose down
      ```

      To remove any saved state and start fresh, you may also want to remove the Docker volumes:

      ```sh
      docker compose down -v
      ```



## Documentation
Read the [user manual](docs/usage.md) for more information about setting up and managing a server.

If you want to contribute or simply want to understand how everything works under the hood, please read the [technical documentation](docs/architecture.md).

Contribution guidelines available [here](docs/contributing.md).



## FAQ
### What is the project status?
JunimoServer is currently in an unstable alpha release phase, meaning it’s still under active development and not all features may work as expected. You may encounter [open bugs](https://github.com/stardew-valley-dedicated-server/server/issues) or incomplete functionalities. For support and updates, please join our [Discord](https://discord.gg/w23GVXdSF7) community.

### Are there more planned features?
Yes! We have a list of planned features [here](docs/planned-features.md). Suggestions and contributions are welcome! If you have ideas for new features or improvements, please feel free to open an issue or submit a pull request.

### What are the minimum hardware requirements?
JunimoServer is designed to run on modest hardware, but performance may vary depending on player count and activity:

  - **CPU**: Dual-core processor (minimum)
  - **RAM**: 2 GB (recommended minimum, though 4 GB or more may improve performance)
  - **Disk Space**: 1 GB free space (depending on save file sizes and log generation)

A VPS or dedicated server with at least 2 GB of RAM and a stable internet connection is recommended for optimal performance.

### Can I run this on Windows or macOS?
While JunimoServer is optimized for Linux and runs on Docker, you can also run it on Windows or macOS systems that support Docker. However, for best performance and compatibility, a Linux environment is recommended.

### Are there other Stardew Valley servers?
If JunimoServer doesn’t fit your needs, here are a few alternatives:
  - [norimicry/stardew-multiplayer-docker](https://github.com/norimicry/stardew-multiplayer-docker): Another Docker-based Stardew Valley server with a different setup approach.



## Credits & Contributors
This project is not affiliated with or endorsed by ConcernedApe LLC.
All product names, logos, and brands are property of their respective owners.

This project would not have been possible without the amazing work of many other people :heart:
* [Junimohost](https://github.com/JunimoHost/junimohost-stardew-server) (from [mrthinger](https://github.com/mrthinger) and [Regnivon](https://github.com/regnivon)) as the base of this dedicated server
* [AlwaysOn Mod](https://github.com/funny-snek/Always-On-Server-for-Multiplayer) (from [funny-snek](https://github.com/funny-snek)) for writing the original keep-alive logic for the server player
* [NetworkOptimizer](https://github.com/Ilyaki/NetworkOptimizer) (from [Ilyaki](https://github.com/Ilyaki))

If you think your name belongs here, please feel free to create a PR adding yourself to this list!

