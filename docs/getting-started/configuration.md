# Configuration

JunimoServer uses environment variables for customization. When running the server with Docker, you can set these variables in your `.env` file at runtime or during the build process.

## Environment Variables

### Runtime Variables

These variables are used during server operation, either at startup or throughout the server's lifecycle:

| Variable Name | Description | Default | Available in |
|---------------|-------------|---------|--------------|
| `GAME_PORT` | Game port for multiplayer connections | 24642 | 1.0.0 |
| `DISABLE_RENDERING` | Disables rendering in VNC (improves performance) | true | 1.0.0 |
| `STEAM_USERNAME` | Steam username (required for initial game download and updates) | - | 1.0.0 |
| `STEAM_PASSWORD` | Steam password (required for initial game download and updates) | - | 1.0.0 |
| `STEAM_REFRESH_TOKEN` | Steam refresh token for automated/CI builds (alternative to username/password) | - | 1.2.0 |
| `VNC_PORT` | Web VNC port for browser access | 5800 | 1.0.0 |
| `VNC_PASSWORD` | Web VNC password for authentication | - | 1.0.0 |
| `ALLOW_IP_CONNECTIONS` | Allow direct IP connections (disabled by default as they don't provide user IDs for farmhand ownership) | false | 1.2.0 |
| `STEAM_AUTH_PORT` | Port for the steam-auth service HTTP API | 3001 | 1.2.0 |

::: tip
Set `DISABLE_RENDERING=true` to improve performance when using VNC. The game will still run normally, but rendering will be optimized for server environments.
:::

::: warning Security Note
Steam credentials (`STEAM_USERNAME` and `STEAM_PASSWORD`) are only used locally to download the game server files. They are never shared externally. Consider using a dedicated Steam account with only Stardew Valley for additional security.
:::

### Build Variables

These variables are only relevant during the build process when compiling from source:

| Variable Name | Description | Default | Available in |
|---------------|-------------|---------|--------------|
| `GAME_PATH` | Path to local Stardew Valley installation (used by Directory.Build.props for building the mod) | `C:/Program Files (x86)/Steam/steamapps/common/Stardew Valley` | 1.0.0 |
| `BUILD_CONFIGURATION` | Build configuration for the mod (Debug or Release) | Debug | 1.0.0 |

::: info
Build variables are only needed when building from source. If you're using the pre-built Docker images, you can ignore these settings.
:::

## Example Configuration

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

# Optional: Networking (disabled by default)
# ALLOW_IP_CONNECTIONS=false
```

## Next Steps

With your server configured, learn how to use it in [Using the Server](/guide/using-the-server).
