# Console & Chat Commands

JunimoServer provides multiple command interfaces for server management.

## Command Interfaces

| Interface | Access | Best For |
|-----------|--------|----------|
| CLI Console | `docker compose exec server attach-cli` | Server commands, viewing logs |
| SMAPI Console | Via VNC or CLI | SMAPI and game commands |
| Chat Commands | In-game chat (`!command`) | Player/admin management |

## CLI Console

### Connecting to CLI

Attach to the interactive server console:

```sh
# Using make
make cli

# Or using docker compose
docker compose exec server attach-cli
```

The CLI provides a split-pane tmux interface:

- **Top pane** — Server logs (read-only, scrollable with mouse)
- **Bottom pane** — Interactive command input

### CLI Commands

| Command | Description |
|---------|-------------|
| `cli exit` | Detach from the CLI session |
| `cli quit` | Detach from the CLI session |
| `cli detach` | Detach from the CLI session |
| `cli clear` | Clear the input pane |
| Any other input | Sent directly to SMAPI console |

## Server Console Commands

These commands run in the SMAPI console (via CLI or VNC).

### info

Display server status information:

```
info
```

Shows: farm name, version, uptime, in-game date, player count, invite code, rendering state.

### settings

Manage server configuration:

| Command | Description |
|---------|-------------|
| `settings show` | Show current `server-settings.json` configuration |
| `settings validate` | Run configuration and state validation checks |
| `settings newgame` | Preview what a new game would create |
| `settings newgame --confirm` | Clear active save; new game created on restart |

### cabins

Manage player cabins:

| Command | Description |
|---------|-------------|
| `cabins` | List all cabins with position, owner, strategy info |
| `cabins add` | Create a new cabin (hidden or visible per strategy) |

### saves

Manage save files:

| Command | Description |
|---------|-------------|
| `saves` | List available saves (marks currently active) |
| `saves info <name>` | Show save details (farm type, cabins, players) |
| `saves select <name>` | Preview importing a save |
| `saves select <name> --confirm` | Set save as active (loaded on restart) |

### rendering

Control visual rendering for performance:

| Command | Description |
|---------|-------------|
| `rendering on` | Enable rendering |
| `rendering off` | Disable rendering |
| `rendering toggle` | Toggle rendering state |
| `rendering status` | Show current state |

### invitecode

Display the current server invite code:

```
invitecode
```

### host-auto

Toggle automatic host behavior (auto-sleep, event skipping, etc.):

```
host-auto
```

### host-visibility

Toggle whether the host player is visible to other players:

```
host-visibility
```

## Chat Commands

Players and admins use chat commands in-game. Type in the chat box (press `T` or `/`).

### General Commands (All Players)

| Command | Description |
|---------|-------------|
| `!help` | Show available commands |
| `!info` | Show server info (farm, version, uptime, players, ping) |
| `!invitecode` | Show current invite code |
| `!cabin` | Move your cabin to your current position |

### Password Protection Commands

| Command | Description |
|---------|-------------|
| `!login <password>` | Authenticate to leave lobby and play |

### Admin Commands

These require admin role:

| Command | Description |
|---------|-------------|
| `!admin <player>` | Grant admin role to a player |
| `!unadmin <player>` | Revoke admin role |
| `!kick <player>` | Kick a player |
| `!ban <player>` | Ban a player |
| `!unban <player>` | Remove a ban |
| `!listadmins` | List all admins |
| `!listbans` | List all bans |
| `!changewallet` | Toggle shared/separate wallets |
| `!event` | Start current festival event (if stuck) |

### Lobby Management (Admin)

For password-protected servers:

| Command | Description |
|---------|-------------|
| `!authstatus` | Show authentication status of all players |
| `!lobby help` | Show all lobby commands |
| `!lobby list` | List saved lobby layouts |
| `!lobby set <name>` | Set active lobby layout |

See [Password Protection](/features/password-protection/) for full lobby customization.

### Owner Commands

These require server owner (first player/host):

| Command | Description |
|---------|-------------|
| `!joja IRREVERSIBLY_ENABLE_JOJA_RUN` | Permanently enable Joja route |

::: warning Irreversible
The Joja command permanently disables the Community Center. Use with caution.
:::

## Quick Reference

### Starting and Stopping

```sh
docker compose up -d      # Start
docker compose down       # Stop
docker compose restart    # Restart
docker compose ps         # Status
```

### Viewing Logs

```sh
docker compose logs -f           # All logs
docker compose logs -f server    # Server only
docker compose logs -f steam-auth # Steam auth only
```

### Getting Server Info

```sh
# Via CLI
docker compose exec server attach-cli
# Then type: info

# Or via one-liner (requires jq for parsing)
curl -s http://localhost:8080/api/server | jq
```

## Next Steps

- [VNC Interface](/admins/operations/vnc) — Graphical management
- [Networking](/admins/operations/networking) — Connection methods
- [Troubleshooting](/admins/troubleshooting) — Common issues
