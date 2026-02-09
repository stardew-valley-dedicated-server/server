# Server Settings

Game and server settings are configured via `server-settings.json`. This file is auto-created with sensible defaults inside the Docker `settings` volume on first startup.

## Accessing the Settings File

To view current settings:

```sh
docker compose exec server cat /data/settings/server-settings.json
```

To edit:

```sh
# Copy out
docker compose cp server:/data/settings/server-settings.json ./server-settings.json

# Edit the file with your preferred editor

# Copy back
docker compose cp ./server-settings.json server:/data/settings/server-settings.json

# Restart to apply
docker compose restart
```

## Default Settings

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

## Game Creation Settings

These settings only take effect when creating a **new** game. They are ignored when loading an existing save.

| Setting | Description | Default |
|---------|-------------|---------|
| `FarmName` | Farm name displayed in-game | `"Junimo"` |
| `FarmType` | Farm map type (see table below) | `0` |
| `ProfitMargin` | Sell price multiplier | `1.0` |
| `StartingCabins` | Number of cabins created with new game | `1` |
| `SpawnMonstersAtNight` | Monster spawning: `"true"`, `"false"`, or `"auto"` | `"auto"` |

### Farm Types

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

### Profit Margin

| Value | Difficulty |
|-------|------------|
| `1.0` | Normal prices |
| `0.75` | 75% prices (harder) |
| `0.5` | 50% prices (hard) |
| `0.25` | 25% prices (very hard) |

### Monster Spawning

- `"true"` — Monsters spawn at night on all farm types
- `"false"` — No monster spawning
- `"auto"` — Only spawn on Wilderness farm type

## Server Runtime Settings

These settings apply on every startup and can be changed between runs.

| Setting | Description | Default |
|---------|-------------|---------|
| `MaxPlayers` | Maximum concurrent players | `10` |
| `CabinStrategy` | Cabin management strategy | `"CabinStack"` |
| `SeparateWallets` | Each player has their own money | `false` |
| `ExistingCabinBehavior` | How to handle visible cabins | `"KeepExisting"` |
| `VerboseLogging` | Enable detailed debug logging | `false` |
| `AllowIpConnections` | Allow direct IP connections | `false` |
| `LobbyMode` | Lobby mode for password protection | `"Shared"` |
| `ActiveLobbyLayout` | Active lobby layout name | `"default"` |

### Cabin Strategies

| Strategy | Description | Best For |
|----------|-------------|----------|
| `CabinStack` | Cabins hidden off-map. Each player sees only their own at a shared position. | Most servers |
| `FarmhouseStack` | Cabins hidden off-map. All players warp to shared farmhouse interior. | Co-op focused |
| `None` | Vanilla behavior. Cabins placed at real farm positions. | Traditional multiplayer |

### Existing Cabin Behavior

Controls what happens to visible cabins already on the farm when using a stacked strategy.

| Behavior | Description |
|----------|-------------|
| `KeepExisting` | Leave existing cabins at their positions. Only new cabins follow the strategy. |
| `MoveToStack` | Relocate all visible cabins to the hidden stack on startup. |

### Wallet Modes

| Setting | Description |
|---------|-------------|
| `false` | Shared wallet — all players share one money pool |
| `true` | Separate wallets — each player has their own money |

You can toggle this in-game using the `!changewallet` admin command.

### Direct IP Connections

::: warning
Direct IP connections don't provide user IDs, so the server can't track farmhand ownership. Players may lose access to their farmhands if they reconnect from a different IP.
:::

Only enable if you need it for specific network configurations.

## Password Protection Settings

For lobby mode and layout settings, see [Password Protection](/features/password-protection/).

| Setting | Description |
|---------|-------------|
| `LobbyMode` | `"Shared"` (all in one lobby) or `"Individual"` (separate lobbies) |
| `ActiveLobbyLayout` | Name of the lobby layout for new players |

## Next Steps

- [Environment Variables](/admins/configuration/environment) — Docker and infrastructure settings
- [Discord Integration](/admins/configuration/discord) — Set up Discord bot
- [Password Protection](/features/password-protection/) — Secure your server
