# Discord Integration

Optional Discord bot that connects your server to a Discord channel.

## Features

### Server Status

The bot's Discord presence shows real-time server information:

| Server State | Bot Status | Status Text |
|--------------|------------|-------------|
| Online | 🟢 Online | `2/8 players \| S-ABC123` |
| Offline | 🟡 Idle | `Server Offline` |

The status displays current player count, max players, and the invite code. You can copy the invite code directly from the bot's status.

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
