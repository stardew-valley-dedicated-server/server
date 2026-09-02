# Discord Integration

Optional Discord bot that connects your server to a Discord channel.

## Features

### Server Status

The bot's Discord presence shows real-time server information:

| Server State | Bot Status | Status Text |
|--------------|------------|-------------|
| Online | 🟢 Online | `2/8 players \| S-ABC123` |
| Busy (saving, day change, festival, wedding) | 🟢 Online | `🟡 Busy — Saving, changing day, or running an event.` |
| Starting: downloading game files (first run) | 🟡 Idle | `🟠 Starting — Downloading game files.` |
| Starting: launching the game | 🟡 Idle | `🟠 Starting — Launching the game.` |
| Starting: loading the save | 🟡 Idle | `🟡 Starting — Loading the save.` |
| Offline (container down or API unreachable) | 🔴 Do Not Disturb | `🔴 Offline — The server is offline.` |

While online, the status displays current player count, max players, and the invite code. You can copy the invite code directly from the bot's status. The startup states come from the game container itself: it answers `/status` with a startup phase until the mod's API takes over, so a first-run download is never mistaken for an outage.

### Status Dashboard

An auto-updating embed posted to a channel of your choice, showing farm name and layout, in-game date and time, player count, and the invite code. The bot edits the same message in place on a configurable interval (via `STATUS_DASHBOARD_CHANNEL_ID`).

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
- A custom name (via `DISCORD_BOT_NICKNAME`)

## Setup

See [Discord Setup](/admins/configuration/discord) for setup instructions.
