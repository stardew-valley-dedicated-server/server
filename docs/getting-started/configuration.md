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
    "AllowIpConnections": false
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

```

::: info
Game settings (farm name, farm type, cabin strategy, etc.) are configured in `server-settings.json`, not in the `.env` file. See [Game Settings](#game-settings-server-settings-json) above.
:::

## Next Steps

With your server configured, learn how to use it in [Using the Server](/guide/using-the-server).
