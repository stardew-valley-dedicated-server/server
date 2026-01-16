# Frequently Asked Questions

Common questions and answers about JunimoServer.

## General Questions

### What is the project status?

JunimoServer is currently in an **unstable alpha release**, meaning it's actively under development, and not all features may work as expected. You may encounter [open bugs](https://github.com/stardew-valley-dedicated-server/server/issues) or incomplete functionalities.

For updates and support, join our [Discord community](https://discord.gg/w23GVXdSF7).

::: warning Alpha Software
Expect bugs, missing features, and potential breaking changes. Always backup your save files regularly!
:::

### Is JunimoServer free?

Yes! JunimoServer is completely free and open source under the MIT License. You can use it, modify it, and even contribute to its development.

## System Requirements

### What are the minimum hardware requirements?

JunimoServer is designed to run on modest hardware, but performance depends on player count and activity.

**Minimum Requirements:**
- **CPU**: Dual-core processor
- **RAM**: 2 GB (4 GB+ recommended for better performance)
- **Disk Space**: 1 GB free (varies with save file sizes and logs)
- **Network**: Stable internet connection

**Recommended for best experience:**
- A VPS or dedicated server with at least 2 GB of RAM
- Stable internet connection with sufficient upload bandwidth
- Linux environment (though Windows/macOS work too)

### Can I run this on Windows or macOS?

Yes! While JunimoServer is optimized for Linux and runs best on Docker, it can also run on **Windows** or **macOS** systems that support Docker.

::: tip Cross-Platform Support
Thanks to Docker, JunimoServer runs on:
- Linux (recommended for production)
- Windows 10/11 with Docker Desktop
- macOS with Docker Desktop
:::

For optimal performance and compatibility, a Linux environment is recommended for production deployments.

## Server Operation

### How many players can join?

Stardew Valley officially supports up to 4 players by default. However, with JunimoServer and proper cabin management, you can support more players by adding cabins to your farm.

The actual player limit depends on:
- Your server hardware
- Network bandwidth
- Mods installed (some mods increase limits)

### Do players need mods installed?

It depends:
- **Server-only mods** (like JunimoServer core) don't require client installation
- **Content mods** (that add items, NPCs, etc.) must be installed on both server and all clients
- **Client-side mods** (like UI improvements) are optional per player

::: warning Mod Synchronization
All players should have the same content mods installed to avoid synchronization issues and potential crashes.
:::

### Can I use my existing save file?

Yes! You can import your existing Stardew Valley save files:

1. Stop the server
2. Copy your save folder into the `saves` volume (mounted at `/config/xdg/config/StardewValley` in the container)
3. Restart the server
4. Connect as usual

You can copy files into the volume using:
```sh
docker run --rm -v server_saves:/saves -v $(pwd):/backup ubuntu cp -r /backup/YourSaveFolder /saves/
```

Make sure to backup your original save first!

## Technical Questions

### Why do I need Steam credentials?

Steam credentials (`STEAM_USERNAME` and `STEAM_PASSWORD`) are required only for:
- Initial game download
- Game updates

::: info Security
Your credentials are used **locally only** to download game files. They are never shared or transmitted outside your environment. Consider using a dedicated Steam account for additional security.
:::

After the initial setup, a refresh token is saved so you don't need to re-enter credentials on every restart.

### How do I update the game version?

See the [Upgrading](/guide/upgrading) guide for detailed instructions. The basic process:

1. Stop the server
2. Remove the game volume
3. Restart - latest version downloads automatically

### Can I migrate from another server solution?

Yes! If you're coming from another Stardew Valley server solution, you can:

1. Export your save files from the old server
2. Import them into JunimoServer's `data/Saves` directory
3. Install any mods you were using
4. Start the server

Make sure to test thoroughly before switching production servers.

## Troubleshooting

### The server won't start

Common fixes:
1. Check Docker is running: `docker ps`
2. Verify your `.env` configuration is correct
3. Check Steam credentials are valid
4. Review logs: `docker compose logs -f`

See [Getting Help](/community/getting-help) if you're still stuck.

### Players can't connect

Check:
- Is the server running? `docker compose ps`
- Are ports properly forwarded? (default: 24642 UDP)
- Firewall rules allowing connections?
- Players using correct IP address?

### VNC won't load

Verify:
- VNC port is accessible (default: 5800)
- `VNC_PASSWORD` is set in `.env`
- No firewall blocking the port
- Using correct URL: `http://YOUR_IP:5800`

## Getting More Help

Still have questions?

- Check our [documentation](/getting-started/introduction)
- Join our [Discord](https://discord.gg/w23GVXdSF7)
- Search [GitHub Issues](https://github.com/stardew-valley-dedicated-server/server/issues)
- Read the [Contributing](/community/contributing) guide

If you found a bug, see [Reporting Bugs](/community/reporting-bugs).
