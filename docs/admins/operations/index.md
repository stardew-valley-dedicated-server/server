# Server Operations

Day-to-day management of your JunimoServer.

## Management Interfaces

JunimoServer provides multiple ways to manage your server:

| Interface | Access | Best For |
|-----------|--------|----------|
| [VNC Web Interface](/admins/operations/vnc) | Browser at `http://server:5800` | Visual management, save files |
| [CLI Console](/admins/operations/commands) | `docker compose exec server attach-cli` | Server commands, logs |
| [Chat Commands](/admins/operations/commands#chat-commands) | In-game chat | Player management, admin tasks |
| [REST API](/developers/api/introduction) | HTTP requests | Automation, external tools |

## Common Tasks

### Starting and Stopping

```sh
# Start the server
docker compose up -d

# Stop the server
docker compose down

# View logs
docker compose logs -f

# Restart
docker compose restart
```

### Server Status

Check if the server is running:

```sh
docker compose ps
```

Get detailed server info via CLI:

```sh
docker compose exec server attach-cli
# Then type: info
```

### Save Management

Save files are stored in the `saves` Docker volume. See [VNC Interface](/admins/operations/vnc) for backup and restore procedures.

## Guides

- [Web Interface (VNC)](/admins/operations/vnc) — Graphical access and save management
- [Console & Chat Commands](/admins/operations/commands) — CLI and in-game commands
- [Networking](/admins/operations/networking) — Ports, connection methods, troubleshooting
- [Upgrading](/admins/operations/upgrading) — Update to new versions

## Need Help?

- [Troubleshooting](/admins/troubleshooting) — Common issues and solutions
- [Getting Help](/community/getting-help) — Community support channels
