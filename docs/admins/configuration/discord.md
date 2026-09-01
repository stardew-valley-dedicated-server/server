# Discord Setup

Setup instructions for the Discord bot. See [Discord Integration](/features/discord) for the feature overview.

The bot runs alongside your server, so its identity is your farm's: its nickname shows your farm name, and its presence shows your player count and invite code. That requires a Discord app of your own — free, one-time, about 5 minutes.

## 1. Create the Bot

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and click **New Application**
2. On **General Information**, copy the **Application ID** (needed in step 2)
3. On the **Bot** tab, click **Reset Token** and copy the token (needed in step 3)

::: warning Keep Token Secret
The token is your bot's password. Never share it publicly.
:::

## 2. Invite the Bot

Open this URL with your Application ID filled in, pick your Discord server, and authorize:

```
https://discord.com/oauth2/authorize?client_id=YOUR_APPLICATION_ID&scope=bot&permissions=67193920
```

`permissions=67193920` grants exactly what the bot uses:

| Permission | Used for |
|------------|----------|
| View Channels, Send Messages | Chat relay and status dashboard |
| Embed Links | Status dashboard embed |
| Read Message History | Chat relay, recovering the dashboard message after restarts |
| Add Reactions | ❌ marker on Discord messages that failed to reach the game |
| Change Nickname | Showing the farm name as the bot's nickname |

## 3. Configure

Add to your `.env`:

```sh
DISCORD_BOT_TOKEN=your_bot_token_here
# Only if the server API uses authentication:
API_KEY=your_api_key_here
```

Restart with `docker compose up -d`. The bot comes online and its presence shows the player count and invite code — no further setup needed. The features below are optional.

## Chat Relay

Two-way chat between a Discord channel and the game.

1. Create a dedicated text channel — every message in it is sent to the game, and all game chat appears there
2. In the Developer Portal, open your app → **Bot** → enable **Message Content Intent**. Discord gates reading message content behind this switch; without it the bot cannot see Discord messages
3. Get the channel ID: enable **User Settings** → **Advanced** → **Developer Mode**, then right-click the channel → **Copy ID**
4. Add to `.env`:

```sh
DISCORD_CHAT_CHANNEL_ID=123456789012345678
```

| Direction | Format |
|-----------|--------|
| Game → Discord | `**PlayerName**: message` |
| Discord → Game | `(Web) DiscordName: message` |

::: tip Spam Protection
The relay has no rate limit of its own. If spam is a concern, set Discord's slowmode on the channel (**Edit Channel** → **Slowmode**).
:::

## Status Dashboard

A status embed (farm name, date, players, invite code) posted to a channel and kept up to date by editing the same message in place.

```sh
STATUS_DASHBOARD_CHANNEL_ID=123456789012345678
# Seconds between updates (default 30)
STATUS_DASHBOARD_REFRESH_RATE=60
```

The dashboard may share a channel with the chat relay.

The dashboard message is owned by your deployment: the bot stamps an ownership id into the embed footer and persists it in the `discord-bot-data` volume (shipped in the compose file), so the same message survives restarts and server resets. If a second server posts its dashboard to the same channel, the bot detects the foreign dashboard, logs a warning, and leaves it untouched instead of overwriting it.

::: warning One bot application per server
The ownership id protects only the dashboard message. Presence and nickname are global to the bot user, so two servers sharing one bot token still overwrite each other's presence and nickname. Run one bot application per server.
:::

## Bot Nickname

| Configuration | Behavior |
|---------------|----------|
| Not set | Uses the farm name from the game |
| `DISCORD_BOT_NICKNAME=value` | Uses a fixed custom name |

## Troubleshooting

### Bot Not Coming Online

1. Verify `DISCORD_BOT_TOKEN` is correct
2. If `DISCORD_CHAT_CHANNEL_ID` is set, **Message Content Intent** must be enabled — login fails with `Used disallowed intents` otherwise
3. Check logs: `docker compose logs -f discord-bot`

### Messages Not Relaying

1. Verify Message Content Intent is enabled
2. Check `DISCORD_CHAT_CHANNEL_ID` is correct
3. Ensure the bot can read and send in that channel

### Bot Shows "Server Offline" But Server Is Running

1. If `API_KEY` is set on the server, the bot needs the same key
2. Check for authentication errors: `docker compose logs discord-bot`

### Wrong Bot Nickname

1. Check `DISCORD_BOT_NICKNAME` in `.env` and restart
2. The bot needs the Change Nickname permission in your Discord server
