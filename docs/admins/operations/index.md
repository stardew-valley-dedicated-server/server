# Server Operations

## Management Interfaces

| Interface | Access | Best For |
|-----------|--------|----------|
| **Your Game Client** | Co-op → Enter Invite Code | Playing and testing. Just connect like any multiplayer game. |
| [CLI Console](/admins/operations/commands) | `docker compose exec server attach-cli` | Server commands, logs, invite codes |
| [Chat Commands](/admins/operations/commands#chat-commands) | In-game chat | Player management, admin tasks |
| [REST API](/developers/api/introduction) | HTTP requests | Automation, external tools |
| [VNC Web Interface](/admins/operations/vnc) | Browser at `http://server:5800` | Advanced debugging only (disabled by default) |

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

Save files are stored in the `saves` Docker volume.

- [Importing Saves](/admins/operations/importing-saves): bring an existing save into the server
- [Backup & Recovery](/features/backup): back up and restore saves you already host

## Guides

- [Console & Chat Commands](/admins/operations/commands)
- [Importing Saves](/admins/operations/importing-saves)
- [Networking](/admins/operations/networking)
- [Upgrading](/admins/operations/upgrading)
- [Web Interface (VNC)](/admins/operations/vnc) (advanced debugging only)
