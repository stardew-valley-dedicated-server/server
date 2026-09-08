# Discord Setup

Setup instructions for the Discord bot. See [Discord Integration](/features/discord) for the feature overview.

The bot runs alongside your server, so its identity is your farm's: its nickname shows your farm name, and its presence shows your player count, version, and invite code. That requires a Discord app of your own — free, one-time, about 5 minutes.

## 1. Create the Bot

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and click **New Application**
2. On **General Information**, set the bot's name and icon, then copy the **Application ID** (needed in step 2)
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

Restart with `docker compose up -d`. The bot comes online and its presence shows the player count, version, and invite code — no further setup needed. The features below are optional.

## Chat Relay

Two-way chat between a Discord channel and the game.

1. Create a dedicated text channel — every message in it is sent to the game, and all game chat appears there
2. In the Developer Portal, open your app → **Bot** → enable **Message Content Intent**. Discord gates reading message content behind this switch; without it the bot cannot see Discord messages
3. Add the channel to `.env`, by name or by ID:

```sh
DISCORD_CHAT_CHANNEL=farm-chat
```

::: tip Channel names and IDs
A name is resolved in every Discord server the bot is in, so one configuration serves a production server and a test server with the same channel layout. An ID targets exactly one channel; get it by enabling **User Settings** → **Advanced** → **Developer Mode**, then right-click the channel → **Copy ID**.
:::

| Direction | Format |
|-----------|--------|
| Game → Discord | `**PlayerName**: message` |
| Discord → Game | `(Web) DiscordName: message` |

::: tip Spam Protection
The relay has no rate limit of its own. If spam is a concern, set Discord's slowmode on the channel (**Edit Channel** → **Slowmode**).
:::

## Status Dashboard

A status embed (state, players, in-game date, image version, uptime, invite code) posted to a channel and kept up to date by editing the same message in place.

```sh
STATUS_DASHBOARD_CHANNEL=farm-status
# Seconds between updates (default 30, minimum 20)
STATUS_DASHBOARD_REFRESH_RATE=60
```

The channel is given by name or ID, as for the chat relay, and may be the same channel. With a name, every Discord server the bot is in gets its own dashboard.

The dashboard message is owned by your deployment: the bot stamps an ownership id into the embed footer and persists it in the `discord-bot-data` volume (shipped in the compose file), so the same message survives restarts and server resets. If a second server posts its dashboard to the same channel, the bot detects the foreign dashboard, logs a warning, and leaves it untouched instead of overwriting it.

Both guards depend on that volume:

- If it is missing or unwritable, the bot logs a warning and falls back to editing any dashboard in the channel — in a shared channel it can then overwrite another deployment's dashboard.
- If you delete it (`docker compose down -v`, `make clean`), the bot gets a new identity: it posts a fresh dashboard and warns about the old one instead of reusing it. Delete the old message by hand.

::: warning One bot application per server
The ownership id protects only the dashboard message. Presence is global to the bot user, and each deployment rewrites the bot's nickname in every Discord server it is in, so two game servers sharing one bot token still overwrite each other's presence and nickname. Run one bot application per server.
:::

## Commands

Mention the bot with a command to ask it directly, for example `@Preview !status`:

| Command | Reply |
|---------|-------|
| `!status` | Server state, players, in-game date, versions, tick rate, and the invite code |
| `!players` | Who is online and cabin availability |
| `!server` | Tick rate, memory, and gameplay settings |
| `!help` | This list |

The bot answers in any channel it can read, except the chat relay and status dashboard channels, which keep their own purpose. Restrict where it answers by adjusting the bot's channel permissions in Discord. The mention is what tells several bots in one Discord server apart, and keeps the commands from colliding with the in-game `!` commands typed into the chat relay.

## Bot Nickname

The nickname is what your Discord server shows in place of the bot's username; the username and profile picture themselves come from the Developer Portal.

| Configuration | Behavior |
|---------------|----------|
| Not set | Uses the farm name from the game |
| `SERVER_NAME=value` | Uses the server's display name (set on the `server` service) |

## Troubleshooting

### Bot Not Coming Online

1. Verify `DISCORD_BOT_TOKEN` is correct
2. If `DISCORD_CHAT_CHANNEL` is set, **Message Content Intent** must be enabled — otherwise the bot logs `Discord refused the bot's gateway intents` and stops
3. Check logs: `docker compose logs -f discord-bot`

### Messages Not Relaying

1. Verify Message Content Intent is enabled
2. Check `DISCORD_CHAT_CHANNEL` matches the channel's name or ID; the startup log lists the channel it resolved to, or a `not found` warning
3. Ensure the bot can read and send in that channel
4. Run `docker compose logs discord-bot`: `Cannot reach the server WebSocket` means the server API is not up yet or `API_ENABLED` is false; `closed the WebSocket before authentication completed` usually means `API_KEY` differs between the two services; if the keys match, check the server logs

### Bot Shows "Offline" But Server Is Running

1. If `API_KEY` is set on the server, the bot needs the same key
2. Check for authentication errors: `docker compose logs discord-bot`
3. A server that is still downloading or booting shows as "Starting", not "Offline" — "Offline" means the bot got no valid `/status` response, either because the port is unreachable or because the request was rejected

### Wrong Bot Nickname

1. Check `SERVER_NAME` in `.env` and restart both services
2. The bot needs the Change Nickname permission in your Discord server
