# Discord Integration

Optional Discord bot that connects your server to a Discord channel.

## Features

### Server Status

The bot's Discord presence shows real-time server information:

| Server State | Bot Status | Status Text |
|--------------|------------|-------------|
| Online | 🟢 Online | `2/8 players, code SGF0LUHHTYF5`, alternating with `v1.5.0, code SGF0LUHHTYF5` |
| Busy (saving, festival, wedding) | 🟢 Online | `Saving or running an event.` |
| Starting: downloading game files (first run) | 🟡 Idle | `Downloading game files.` |
| Starting: launching the game | 🟡 Idle | `Launching the game.` |
| Starting: loading the save | 🟡 Idle | `Loading the save.` |
| Offline (container down or API unreachable) | 🔴 Do Not Disturb | `Currently unreachable.` |

While online, the status line has room for the invite code plus one more item, so it alternates between the player count and the image version on each refresh. You can copy the invite code directly from the bot's status. The startup states come from the game container itself: it answers `/status` with a startup phase until the mod's API takes over, so a first-run download is never mistaken for an outage.

### Status Dashboard

An auto-updating embed posted to a channel of your choice (`STATUS_DASHBOARD_CHANNEL`), showing the server state, player count, in-game date and time, image version, uptime, and the invite code — falling back to the last-known snapshot while the server is unreachable. The bot edits the same message in place on a configurable interval (`STATUS_DASHBOARD_REFRESH_RATE`).

### Chat Relay

Two-way chat between Discord and the game:

| Direction | Format |
|-----------|--------|
| Game → Discord | `**PlayerName**: message` |
| Discord → Game | `(Web) DiscordName: message` |

Players on Discord can chat with players in-game and vice versa.

### Bot Nickname

The bot's nickname in your Discord server can be:
- Your farm name (automatic)
- The server's display name (via `SERVER_NAME`)

## Setup

See [Discord Setup](/admins/configuration/discord) for setup instructions.
