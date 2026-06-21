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

- **Top pane**: Server logs (read-only, scrollable with mouse)
- **Bottom pane**: Interactive command input

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
| `saves import <name>` | Import a save as-is (its owner becomes the headless host); loaded on restart |
| `saves import <name> --swap-host-to <id>` | Import + demote the save's owner into a cabin farmhand bound to the given platform id (Steam64 or GOG Galaxy id), installing a fresh "Server" host |

> **`--swap-host-to` rewrites the target save in place — back up first.** The swap transform mutates
> the save folder (fault-tolerantly: a failed transform leaves the loadable save byte-intact). Plain
> as-is import makes **no** file changes — it only points the next boot at the save.
>
> Importing **as-is** makes the save's original player the automated headless host (correct for a
> single-player save; for a co-op save whose owner is a real player, use `--swap-host-to <id>` so they
> stay a player). The bind stamps the demoted owner's farmhand with that platform id so it is **scoped
> to — and selectable by — that account**; the player still picks it from the farmhand menu (vanilla
> allows multiple farmhands per account, so it is not auto-selected). The `--swap-host-to` bind is only
> enforced on Steam/GOG (LAN clients have no platform id), and the import is one-shot: run it, restart
> once, done.

### rendering

Control visual rendering for performance:

| Command | Description |
|---------|-------------|
| `rendering <fps>` | Set render rate (0 disables) |
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

Pressing **F9** on the VNC display is the in-view equivalent of this command. It works over VNC whenever rendering is on (`SERVER_FPS` > 0), even while automation is suppressing all other input — F9 is deliberately kept live so you can drop automation and take manual control without a restart.

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
| `!changewallet <shared\|separate>` | Switch wallet mode at the end of the day |
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
curl -s http://localhost:8080/status | jq
```

