# Server Settings

Game and server settings are configured via `server-settings.json`. This file is auto-created with sensible defaults on first startup.

## Accessing the Settings File

The settings file is stored on your host machine at `.local-container/settings/server-settings.json` (relative to your `docker-compose.yml`).

To view:

```sh
cat .local-container/settings/server-settings.json
```

To edit:

1. Open `.local-container/settings/server-settings.json` with any text editor
2. Make your changes
3. Restart to apply: `docker compose restart`

::: info First Run
This file is auto-created with defaults on first server startup. If the file doesn't exist yet, start the server once and it will be created.
:::

## Default Settings

```json
{
  "Game": {
    "FarmName": "Junimo",
    "FarmType": 0,
    "ProfitMargin": 1.0,
    "StartingCabins": 1,
    "SpawnMonstersAtNight": "auto",
    "RemixBundles": false,
    "RemixMines": false,
    "CommunityCenterYear1": false,
    "CabinLayoutNearby": false,
    "UseLegacyRandom": false,
    "RandomSeed": null,
    "PetBreed": 1,
    "PetName": "Apples",
    "MushroomCave": true,
    "BuyJoja": false
  },
  "Server": {
    "MaxPlayers": 10,
    "CabinStrategy": "CabinStack",
    "SeparateWallets": false,
    "ExistingCabinBehavior": "KeepExisting",
    "VerboseLogging": false,
    "AllowIpConnections": false,
    "LobbyMode": "Shared",
    "ActiveLobbyLayout": "default",
    "AdminSteamIds": [],
    "NetworkBroadcastPeriod": 1
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
| `RemixBundles` | Use remixed or default CC bundles | `"false"` |
| `RemixMines` | Use remixed or default mines rewards | `"false"` |
| `CommunityCenterYear1` | Guarantee the community center is completable in year 1 | `false` |
| `CabinLayoutNearby` | Whether to use nearby cabin layout. Only has an effect when CabinStrategy is `"None"` | `false` |
| `UseLegacyRandom` | Use the legacy RNG algorithms | `false` |
| `RandomSeed` | Seed to use for RNG. `"null"` for a random seed | `"null"` |
| `PetBreed` | Breed of pet. [0-4] are cats, [5-9] are dogs. -1 for no pet | `1` |
| `PetName` | Name of starting pet | `"Apples"` |
| `MushroomCave` | Makes the farm cave a mushroom cave. Set to false for fruit bats. | `true` |
| `BuyJoja` | Whether to buy the Joja membership when available. | `false` |

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
| `8+` | Mod-added farms in mod load order, excluding Meadowlands (8 = first mod farm, 9 = second, etc.) |

### Profit Margin

| Value | Difficulty |
|-------|------------|
| `1.0` | Normal prices |
| `0.75` | 75% prices (harder) |
| `0.5` | 50% prices (hard) |
| `0.25` | 25% prices (very hard) |

### Monster Spawning

- `"true"`: Monsters spawn at night on all farm types
- `"false"`: No monster spawning
- `"auto"`: Only spawn on Wilderness farm type

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
| `AdminSteamIds` | Steam IDs auto-granted admin on join | `[]` |
| `NetworkBroadcastPeriod` | Ticks between farmer/location/world-state delta broadcasts (1=every tick, 3=vanilla) | `1` |

### Cabin Strategies

| Strategy | Description | Best For |
|----------|-------------|----------|
| `CabinStack` | Cabins hidden off-map. Each player sees only their own at a shared position. | Most servers |
| `FarmhouseStack` | Cabins hidden off-map. All players exit at the main farmhouse's front door (shared entry point). | Co-op focused |
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
| `false` | Shared wallet: all players share one money pool |
| `true` | Separate wallets: each player has their own money |

You can toggle this in-game using the `!changewallet` admin command.

### Direct IP Connections

::: warning
Direct IP connections don't provide user IDs, so the server can't track farmhand ownership. Players may lose access to their farmhands if they reconnect from a different IP.
:::

Only enable if you need it for specific network configurations.

### Network Broadcast Period

Controls how often the server broadcasts farmer, location, and world-state deltas to connected players.

| Value | Behavior |
|-------|----------|
| `1` | Broadcast every tick. Lowest perceived latency, highest bandwidth (mod default). |
| `2` | Broadcast every other tick. Roughly 50% bandwidth reduction with one extra tick of latency (~16ms at 60 TPS). |
| `3` | Vanilla Stardew default. Roughly 66% bandwidth reduction with two extra ticks of latency. |

Allowed range is `1` to `60`. Out-of-range values are clamped and a warning is logged on startup. Raise this value if connected players report bandwidth saturation; lower it if movement and inventory changes feel laggy.

## Password Protection Settings

For lobby mode and layout settings, see [Password Protection](/features/password-protection/).

| Setting | Description |
|---------|-------------|
| `LobbyMode` | `"Shared"` (all in one lobby) or `"Individual"` (separate lobbies) |
| `ActiveLobbyLayout` | Name of the lobby layout for new players |

