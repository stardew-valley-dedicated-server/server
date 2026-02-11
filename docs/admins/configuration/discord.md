# Discord Setup

Setup instructions for the Discord bot. See [Discord Integration](/features/discord) for feature overview.

## 1. Create a Discord Application

1. Go to the [Discord Developer Portal](https://discord.com/developers/applications)
2. Click **New Application** and give it a name
3. Go to the **Bot** tab
4. Click **Reset Token** and copy the token

::: warning Keep Token Secret
Never share your bot token publicly. Treat it like a password.
:::

## 2. Configure Bot Permissions

Your bot needs these permissions:

| Permission | Required For |
|------------|--------------|
| Send Messages | Chat relay (game → Discord) |
| Read Message History | Chat relay context |
| Change Nickname | Displaying farm name as bot nickname |

## 3. Invite the Bot to Your Server

1. Go to **OAuth2** → **URL Generator**
2. Select scopes: `bot`
3. Select permissions: `Send Messages`, `Read Message History`, `Change Nickname`
4. Copy the generated URL and open it in your browser
5. Select your Discord server and authorize

## 4. Set Environment Variables

Add to your `.env` file:

```sh
DISCORD_BOT_TOKEN=your_bot_token_here
```

If you have `API_KEY` set for API authentication, add it here too so the bot can access the server API:

```sh
DISCORD_BOT_TOKEN=your_bot_token_here
API_KEY=your_api_key_here
```

This is enough for server status display (player count and invite code in bot presence).

## Chat Relay Setup

To enable two-way chat between Discord and the game, additional setup is required.

### 1. Create a Text Channel

Create a dedicated **text channel** in your Discord server for the chat relay:

1. Right-click your server → **Create Channel**
2. Select **Text** as the channel type
3. Name it something like `#stardew-chat`
4. (Optional) Set channel permissions so only certain roles can send messages

::: tip Dedicated Channel Recommended
Use a dedicated channel for chat relay. All messages in this channel will be sent to the game, and all game chat will appear here.
:::

### 2. Enable Message Content Intent

1. Go to [Discord Developer Portal](https://discord.com/developers/applications)
2. Select your application → **Bot**
3. Under **Privileged Gateway Intents**, enable **Message Content Intent**
4. Save changes

::: info Why This Is Needed
Discord requires explicit permission to read message content. Without this, the bot can't relay messages from Discord to the game.
:::

### 3. Get Channel ID

1. In Discord, go to **User Settings** → **Advanced**
2. Enable **Developer Mode**
3. Right-click the chat relay channel → **Copy ID**

### 4. Configure Environment

Add the channel ID to your `.env`:

```sh
DISCORD_BOT_TOKEN=your_bot_token
DISCORD_CHAT_CHANNEL_ID=123456789012345678
```

If you have API authentication enabled:

```sh
DISCORD_BOT_TOKEN=your_bot_token
DISCORD_CHAT_CHANNEL_ID=123456789012345678
API_KEY=your_api_key
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
DISCORD_CHAT_CHANNEL_ID=123456789012345678
DISCORD_BOT_NICKNAME=Junimo Farm

# API authentication (required if API_KEY is set on server)
API_KEY=your_api_key_here
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

### Bot Shows "Server Offline" But Server Is Running

1. If `API_KEY` is set on the server, ensure the same key is set for the Discord bot
2. Check the bot logs for authentication errors: `docker compose logs discord-bot`
3. Verify the bot can reach the server API (network/firewall issues)

