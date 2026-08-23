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

## Collecting server diagnostics

When a maintainer asks for it, run this and attach the resulting file to your support thread or
GitHub issue:

```sh
docker compose exec -it server diagnostics
```

It bundles the server's build identity, health, settings, container configuration, installed server
mods, connected players/farmhands/cabins, live diagnostics, and the server logs into a single
timestamped zip. The file lands **on the host** at `./diagnostics/state-<timestamp>.zip` (via the
`./diagnostics` bind mount) — no `docker cp` needed. `make diagnostics` runs the same command.

Run with `-it` (a terminal) and the tool prompts for the few things the server can't see itself —
client-side mods, the affected player, client platforms, whether the server shares a Steam account
with a game client, whether the server is local or remote, and reproducibility — all optional. Without `-it` (e.g. `docker compose exec server diagnostics`) it
skips the prompts and writes a short "Technical details to include" template into the report for you
to fill in.

The zip contains `report.md` (a readable summary plus your answers), `server-output.log`,
`SMAPI-latest.txt`, and `SMAPI-crash.txt` (only if a crash occurred). Logs are included as-is,
trimmed to their last 32 MB. The report echoes this server's own container settings (tick rate, API,
logging); passwords and API keys are shown only as set/not set, never by value. It is not a "file a
bug" form — describe your problem in your own words first, then attach this for the technical facts.

> The command ships in the standard server image only. The [modern (Alpine)
> image](./modern-docker.md) does not include it.

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
| `settings newgame [--confirm]` | Preview a new game, or with `--confirm` clear the active save and create one on restart |

### cabins

Manage player cabins:

| Command | Description |
|---------|-------------|
| `cabins` | List all cabins with position, owner, strategy info |
| `cabins add` | Create a new cabin (hidden or visible per strategy; refused at the `None` cabin cap) |
| `cabins stackspot [<x> <y>]` | Show — or set — the `CabinStack` shared stack spot (coordinates validated, refused during a staged migration); in-game equivalent: `!stackspot` |
| `cabins migrate start <strategy>` | Begin a staged strategy migration: auto-places what fits on designated spots, reports the rest |
| `cabins migrate status` | Show staged/remaining placements for the active migration |
| `cabins migrate place <x> <y>` | Stage the next cabin at a tile (coordinates validated, never destructive); in-game equivalent: `!migrate place` |
| `cabins migrate commit` | Finish the migration: flips the strategy and updates `server-settings.json` (refused while placements remain) |
| `cabins migrate abort` | Undo the staging: staged cabins return to the hidden stack |

The staged migration is required for strategy switches that place a cabin on the live farm
(`CabinStack`/`FarmhouseStack` → `None`, and `FarmhouseStack` → `CabinStack`); pure-hide switches
stay on the settings-file + reload path. See
[Cabin Strategies](/features/cabin-strategies#switching-strategies) for the full walkthrough.

### saves

Manage save files:

| Command | Description |
|---------|-------------|
| `saves` | List available saves (marks currently active) |
| `saves info <name>` | Show save details (farm type, cabins, players) |
| `saves import <name> [--swap-host-to <id>] [--reload\|--force-reload]` | Import an existing save |
| `saves reload [--force]` | Reload the active world in-process to apply a manual save edit |

The flags select how the import behaves. `--swap-host-to <id>` keeps the save's owner a player instead
of letting the server take over their farmer. `--reload` loads the save right away rather than on the
next restart, and `--force-reload` (or `--force` on `saves reload`) kicks connected players first. The
full walkthrough is on the [Importing Saves](/admins/operations/importing-saves) page.

### farmhand

Manage farmhand ownership (see
[Farmhand Ownership](/admins/configuration/server-settings#farmhand-ownership)):

| Command | Description |
|---------|-------------|
| `farmhand release <name\|uid>` | Unlock a farmhand: it becomes visible and claimable on every connection method; the next Steam/GOG player to select it becomes the owner, while a direct-IP claim returns it to the shared [LAN pool](/admins/configuration/server-settings#direct-ip-connections) |
| `farmhand rebind <name\|uid> <platformId>` | Re-point a farmhand's ownership to another platform id (e.g. a player who switched accounts) |

The platform id is the Steam64 or GOG Galaxy id shown in the server's
`Client connected via ... (platform id ...)` log line when that player connects. The same id may own
several farmhands, so rebinding to an id that already owns one is fine. Rebinding a farmhand nobody
has customized yet is also fine — the pre-assignment sticks until that player claims it.

Both commands take effect immediately. When the server is empty, the change is also written to disk
on the spot (no day transition needed); with players online it is written at the next day save.

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

Players and admins use chat commands in-game. Type in the chat box (press `T` or `/`). Admin-only commands are hidden from non-admins in the `!help` list.

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
| `!migrate place` | During a staged strategy migration, stage the next cabin to the right of your player (see `cabins migrate`) |
| `!stackspot [place]` | Show the `CabinStack` shared stack spot, or with `place` move it to the right of your player (see `cabins stackspot`) |
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

### Irreversible Commands

These require admin role:

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

### Collecting Diagnostics

```sh
docker compose exec -it server diagnostics   # writes ./diagnostics/state-<timestamp>.zip
```

