# Discord Integration

The optional Discord bot provides server status display and two-way chat relay between Discord and the game.

## Features

- **Server Status** — Bot status shows if server is online
- **Chat Relay** — Messages sync between Discord and in-game chat
- **Custom Nickname** — Bot can display your farm name or custom text

## Setup

### 1. Create a Discord Application

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications)
2. Click **New Application** and give it a name
3. Go to the **Bot** tab
4. Click **Reset Token** and copy the token

::: warning Keep Token Secret
Never share your bot token publicly. Treat it like a password.
:::

### 2. Configure Bot Permissions

Your bot needs these permissions:

- Send Messages
- Read Message History

### 3. Invite the Bot to Your Server

1. Go to **OAuth2** → **URL Generator**
2. Select scopes: `bot`
3. Select permissions: `Send Messages`, `Read Message History`
4. Copy the generated URL and open it in your browser
5. Select your Discord server and authorize

### 4. Set Environment Variables

Add to your `.env` file:

```sh
DISCORD_BOT_TOKEN=your_bot_token_here
```

This is enough for server status display.

## Chat Relay Setup

To enable two-way chat between Discord and the game, additional setup is required.

### 1. Enable Message Content Intent

1. Go to [Discord Developer Portal](https://discord.com/developers/applications)
2. Select your application → **Bot**
3. Under **Privileged Gateway Intents**, enable **Message Content Intent**
4. Save changes

::: info Why This Is Needed
Discord requires explicit permission to read message content. Without this, the bot can't relay messages from Discord to the game.
:::

### 2. Get Channel ID

1. In Discord, go to **User Settings** → **Advanced**
2. Enable **Developer Mode**
3. Right-click the channel you want to use → **Copy ID**

### 3. Configure Environment

Add the channel ID to your `.env`:

```sh
DISCORD_BOT_TOKEN=your_bot_token
DISCORD_CHAT_CHANNEL_ID=123456789012345678
```

### How Chat Relay Works

Once configured:

| Direction | Format |
|-----------|--------|
| Game → Discord | `**PlayerName**: message` |
| Discord → Game | `(Web) DiscordName: message` |

## Bot Nickname

The bot's nickname in Discord servers can be configured:

| Configuration | Behavior |
|---------------|----------|
| Not set | Uses farm name from game |
| `DISCORD_BOT_NICKNAME=value` | Uses fixed custom name |

Example:

```sh
DISCORD_BOT_NICKNAME=Stardew Valley Server
```

## Example Configuration

Complete Discord setup in `.env`:

```sh
# Discord Bot
DISCORD_BOT_TOKEN=MTIzNDU2Nzg5MDEyMzQ1Njc4OQ.XXXXXX.XXXXXXXXXXXXXXXXXXXXXXXXXXX
DISCORD_BOT_NICKNAME=Junimo Farm
DISCORD_CHAT_CHANNEL_ID=123456789012345678
```

## Security Considerations

::: warning Rate Limiting
The chat relay does not currently implement rate limiting. Discord users could potentially spam the game chat. Consider using Discord's built-in slowmode on the relay channel if this is a concern.
:::

### Slowmode Setup

1. Right-click the relay channel in Discord
2. Click **Edit Channel**
3. Under **Slowmode**, set a delay (e.g., 5 seconds)

## Troubleshooting

### Bot Not Coming Online

1. Verify `DISCORD_BOT_TOKEN` is correct
2. Check server logs: `docker compose logs -f`
3. Ensure the bot was invited with correct permissions

### Messages Not Relaying

1. Verify Message Content Intent is enabled
2. Check `DISCORD_CHAT_CHANNEL_ID` is correct
3. Ensure bot has permission to read/send in that channel

### Wrong Bot Nickname

1. Check `DISCORD_BOT_NICKNAME` in `.env`
2. Restart the server: `docker compose restart`
3. Bot may need "Change Nickname" permission in Discord server

## Next Steps

- [Environment Variables](/admins/configuration/environment) — All configuration options
- [Server Operations](/admins/operations/) — Managing your server
