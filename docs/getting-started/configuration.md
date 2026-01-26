# Configuration

JunimoServer uses two configuration mechanisms:

- **`server-settings.json`** — Game and server settings (farm name, cabin strategy, player limit, etc.)
- **`.env` file** — Docker infrastructure, credentials, and networking

## Game Settings (`server-settings.json`)

Game and server settings are configured via a JSON file. On first startup, the mod auto-creates `server-settings.json` with sensible defaults inside the Docker `settings` volume.

To customize, edit the file directly. When running in Docker, you can access it with:

```sh
docker compose exec server cat /data/settings/server-settings.json
```

Or copy it out for editing:

```sh
docker compose cp server:/data/settings/server-settings.json ./server-settings.json
# ... edit the file ...
docker compose cp ./server-settings.json server:/data/settings/server-settings.json
```

### Default Settings File

```json
{
  "Game": {
    "FarmName": "Junimo",
    "FarmType": 0,
    "ProfitMargin": 1.0,
    "StartingCabins": 1,
    "SpawnMonstersAtNight": "auto"
  },
  "Server": {
    "MaxPlayers": 10,
    "CabinStrategy": "CabinStack",
    "SeparateWallets": false,
    "ExistingCabinBehavior": "KeepExisting",
    "VerboseLogging": false,
    "AllowIpConnections": false,
    "LobbyMode": "Shared",
    "ActiveLobbyLayout": "default"
  }
}
```

### Game Creation Settings

These settings only take effect when creating a **new** game. They are ignored when loading an existing save.

| Setting | Description | Default |
|---------|-------------|---------|
| `FarmName` | Farm name displayed in-game | `"Junimo"` |
| `FarmType` | Farm map type (see table below) | `0` |
| `ProfitMargin` | Sell price multiplier (`1.0` = normal, `0.75`/`0.5`/`0.25` for harder economy) | `1.0` |
| `StartingCabins` | Number of cabins created with a new game | `1` |
| `SpawnMonstersAtNight` | Monster spawning: `"true"`, `"false"`, or `"auto"` (auto = only for Wilderness farm) | `"auto"` |

**Farm Types:**

| Value | Farm Type |
|-------|-----------|
| `0` | Standard |
| `1` | Riverland |
| `2` | Forest |
| `3` | Hilltop |
| `4` | Wilderness |
| `5` | Four Corners |
| `6` | Beach |
| `7` | Meadowlands |

### Server Runtime Settings

These settings are applied on every startup and can be changed between runs.

| Setting | Description | Default |
|---------|-------------|---------|
| `MaxPlayers` | Maximum concurrent players | `10` |
| `CabinStrategy` | Cabin management strategy (see below) | `"CabinStack"` |
| `SeparateWallets` | Whether each player has their own wallet | `false` |
| `ExistingCabinBehavior` | How to handle visible cabins that already exist on the farm (see below) | `"KeepExisting"` |
| `VerboseLogging` | Enable detailed debug logging for troubleshooting | `false` |
| `AllowIpConnections` | Allow direct IP connections (see [Networking](/guide/networking)) | `false` |
| `LobbyMode` | Lobby mode for password protection (see [Password Protection](#password-protection)) | `"Shared"` |
| `ActiveLobbyLayout` | Name of the active lobby layout for waiting players | `"default"` |

### Cabin Strategies

| Strategy | Description |
|----------|-------------|
| `CabinStack` | Cabins are hidden off-map. Each player sees only their own cabin at a shared visible position. |
| `FarmhouseStack` | Cabins are hidden off-map. All players warp to the shared farmhouse interior. |
| `None` | Vanilla-like behavior. Cabins are placed at real farm positions with normal doors and warps. |

### Existing Cabin Behavior

Controls what happens to visible cabins already on the farm when the server starts with a stacked strategy. This applies to imported saves, map changes, game updates that add cabins, or any other scenario where cabins end up at real farm positions.

| Behavior | Description |
|----------|-------------|
| `KeepExisting` | Leave existing cabins at their current positions. Only new cabins follow the active strategy. |
| `MoveToStack` | Relocate all visible cabins to the hidden stack on startup. |

## Environment Variables

Environment variables in `.env` control Docker infrastructure, credentials, and networking. They are **not** used for game settings.

### Runtime Variables

| Variable Name | Description | Default |
|---------------|-------------|---------|
| `GAME_PORT` | Game port for multiplayer connections | 24642 |
| `DISABLE_RENDERING` | Disables rendering in VNC (improves performance) | true |
| `STEAM_USERNAME` | Steam username (required for initial game download and updates) | - |
| `STEAM_PASSWORD` | Steam password (required for initial game download and updates) | - |
| `STEAM_REFRESH_TOKEN` | Steam refresh token for automated/CI builds (alternative to username/password) | - |
| `VNC_PORT` | Web VNC port for browser access | 5800 |
| `VNC_PASSWORD` | Web VNC password for authentication | - |
| `STEAM_AUTH_PORT` | Port for the steam-auth service HTTP API | 3001 |
| `API_ENABLED` | Enable HTTP API for external tools and monitoring | true |
| `API_PORT` | Port for the HTTP API server | 8080 |
| `VERBOSE_LOGGING` | Override verbose logging setting (overrides `server-settings.json`) | - |
| `DISCORD_BOT_TOKEN` | Discord bot token for server status and chat relay (see [Discord Integration](#discord-integration)) | - |
| `DISCORD_BOT_NICKNAME` | Custom bot nickname in Discord servers (optional, defaults to farm name) | - |
| `DISCORD_CHAT_CHANNEL_ID` | Discord channel ID for two-way chat relay (optional) | - |

::: tip
Set `DISABLE_RENDERING=true` to improve performance when using VNC. The game will still run normally, but rendering will be optimized for server environments.
:::

::: warning Security Note
Steam credentials (`STEAM_USERNAME` and `STEAM_PASSWORD`) are only used locally to download the game server files. They are never shared externally. Consider using a dedicated Steam account with only Stardew Valley for additional security.
:::

### Build Variables

These variables are only relevant during the build process when compiling from source:

| Variable Name | Description | Default |
|---------------|-------------|---------|
| `GAME_PATH` | Path to local Stardew Valley installation (used by Directory.Build.props for building the mod) | `C:/Program Files (x86)/Steam/steamapps/common/Stardew Valley` |
| `BUILD_CONFIGURATION` | Build configuration for the mod (Debug or Release) | Debug |

::: info
Build variables are only needed when building from source. If you're using the pre-built Docker images, you can ignore these settings.
:::

## Discord Integration

The optional Discord bot provides server status display and two-way chat relay between Discord and the game.

### Setup

1. Create a Discord application at the [Discord Developer Portal](https://discord.com/developers/applications)
2. Go to the **Bot** tab, click "Reset Token" and copy it
3. Invite the bot to your server with permissions: Send Messages, Read Message History
4. Set `DISCORD_BOT_TOKEN` in your `.env` file

This is enough for server status display. For chat relay, see below.

### Bot Nickname

The bot's nickname in Discord servers can be configured:

- **Default**: Uses the farm name from the game server
- **Custom**: Set `DISCORD_BOT_NICKNAME` to override with a fixed name

### Chat Relay

To enable two-way chat between Discord and the game:

1. Enable the [Message Content Intent](https://support-dev.discord.com/hc/en-us/articles/4404772028055-Message-Content-Privileged-Intent-FAQ) in the [Developer Portal](https://discord.com/developers/applications) (Bot → Privileged Gateway Intents)
2. Enable Developer Mode in Discord (Settings → Advanced → Developer Mode)
3. Right-click the channel you want to use → Copy ID
4. Set `DISCORD_CHAT_CHANNEL_ID` in your `.env` file

Once configured:
- Messages from players in-game appear in the Discord channel as `**PlayerName**: message`
- Messages from Discord users appear in-game as `(Web) DiscordName: message`

::: warning Security Note
The chat relay does not currently implement rate limiting. Discord users could potentially spam the game chat. Consider using Discord's built-in slowmode on the relay channel if this is a concern.
:::

## Variable Details

### DISABLE_RENDERING

When `true`, the game skips rendering frames to the display. The game still runs normally and VNC still works, but CPU usage is significantly reduced. Only set to `false` if you need to debug visual issues.

### STEAM_REFRESH_TOKEN

Alternative to username/password for automated environments. After running `setup` once, you can export the refresh token for CI use:

```sh
docker compose run --rm steam-auth export-token
```

See [Authentication](/getting-started/auth) for details.

## Example `.env` File

Here's a complete example `.env` file with all common settings:

```sh
# Required: Steam Credentials
STEAM_USERNAME=your_steam_username
STEAM_PASSWORD=your_steam_password

# Optional: Use refresh token instead of username/password (for CI/automation)
# STEAM_REFRESH_TOKEN=your_refresh_token

# Required: VNC Access
VNC_PASSWORD=your_secure_password
VNC_PORT=5800

# Optional: Game Settings
GAME_PORT=24642
DISABLE_RENDERING=true

# Optional: Discord Bot
# DISCORD_BOT_TOKEN=your_bot_token
# DISCORD_BOT_NICKNAME=My Stardew Server
# DISCORD_CHAT_CHANNEL_ID=123456789012345678
```

::: info
Game settings (farm name, farm type, cabin strategy, etc.) are configured in `server-settings.json`, not in the `.env` file. See [Game Settings](#game-settings-server-settings-json) above.
:::

## Password Protection

Optional server password requires players to authenticate before they can play.

::: tip Full Guide Available
For comprehensive documentation including lobby customization, layout sharing, and advanced configuration, see the [Password Protection & Lobby System](/guide/password-protection) guide.
:::

### How It Works

When password protection is enabled:

1. New players connect and are warped to a **lobby cabin** (a holding area)
2. Players can only use `!login <password>` or `!help` commands
3. On successful authentication, players are warped to their destination:
   - **New players**: Their cabin entry point
   - **Returning players**: Their last saved position
4. Unauthenticated players are kicked after a configurable timeout

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `SERVER_PASSWORD` | Server password. Leave empty to disable password protection. | (empty) |
| `MAX_LOGIN_ATTEMPTS` | Maximum failed login attempts before player is kicked | `3` |
| `AUTH_TIMEOUT_SECONDS` | Seconds before unauthenticated players are kicked. Set to `0` to disable timeout. | `120` |

### Lobby Modes

The `LobbyMode` setting in `server-settings.json` controls how the lobby cabin works:

| Mode | Description |
|------|-------------|
| `Shared` | All unauthenticated players wait in the same lobby cabin. Good for smaller servers. |
| `Individual` | Each unauthenticated player gets their own isolated lobby cabin. Prevents interaction between unauthenticated players. |

### Custom Lobby Layouts

Admins can customize the lobby cabin's appearance using the `!lobby` commands:

1. `!lobby create my-lobby` - Create a new layout and enter edit mode
2. Decorate the cabin (furniture, wallpaper, flooring, objects)
3. `!lobby spawn` - Stand where you want players to appear and set the spawn point
4. `!lobby save` - Save the current layout
5. `!lobby set my-lobby` - Activate the layout for new players

Layout names can only contain letters, numbers, dashes (`-`), and underscores (`_`).

See [Chat Commands](/guide/using-the-server#chat-commands) for full command reference.

### Example Configuration

**.env:**
```sh
SERVER_PASSWORD=your_secure_password
MAX_LOGIN_ATTEMPTS=3
AUTH_TIMEOUT_SECONDS=120
```

**server-settings.json:**
```json
{
  "Server": {
    "LobbyMode": "Shared",
    "ActiveLobbyLayout": "default"
  }
}
```

## Next Steps

With your server configured, learn how to use it in [Using the Server](/guide/using-the-server).
