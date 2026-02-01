# Networking

JunimoServer supports both Steam and GOG clients connecting to the same server. No port forwarding is needed for most setups.

## Connection Methods

| Method | For | Port Forwarding | Reliability |
|--------|-----|-----------------|-------------|
| Steam SDR | Steam clients | Not required | ~99% success |
| GOG Galaxy | GOG clients | Not required | ~50% success |
| Direct IP | Any client | Required (UDP 24642) | Depends on network |

### Steam SDR (Recommended for Steam)

Steam clients connect through Valve's Steam Datagram Relay network. All traffic is routed through Valve's servers, which handles NAT traversal automatically.

**How it works:**
1. Server logs in anonymously to Steam's GameServer API
2. Valve assigns the server a Steam ID
3. Steam-auth service creates a lobby with this ID
4. Players join the lobby and connect via Valve's relay

### GOG Galaxy

GOG clients connect using Galaxy P2P networking with invite codes prefixed with "G". Works through most NATs without port forwarding, but has lower success rates than Steam SDR.

### Direct IP

Disabled by default. Enable via `Server.AllowIpConnections` in [`server-settings.json`](/getting-started/configuration#server-runtime-settings).

::: warning
Direct IP connections don't provide user IDs, so the server can't track farmhand ownership. Players may lose access to their farmhands if they reconnect from a different IP.
:::

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 24642 | UDP | Direct IP connections (disabled by default) |
| 5800 | TCP | VNC web interface |

Steam SDR and GOG Galaxy use dynamic ports and handle NAT traversal automatically.

## Troubleshooting

### Network Diagnostics

The container includes `netdebug` for troubleshooting:

```bash
# Check your NAT type
docker compose exec server netdebug nat

# Test latency to GOG servers
docker compose exec server netdebug ping

# Test download speed
docker compose exec server netdebug speed

# Monitor GOG Galaxy traffic
docker compose exec server netdebug gog-ports
docker compose exec server netdebug gog-requests
```

**NAT Types:**
| Result | Description |
|--------|-------------|
| Open | Full Cone NAT - all connections work |
| Moderate | Restricted Cone - most connections work |
| Strict | Symmetric NAT - may need relay for some connections |

### Players Can't Connect

1. Check the server is running: `docker compose ps`
2. Run `netdebug nat` to check network config

**Remote players** (different network) usually connect fine.

**Same-network players** often have issues due to "hairpinning" - when traffic goes out and comes back to the same network. Many home routers don't support this.

Check `netdebug nat` output:
- **Hairpinning: Not supported** - same-network connections won't work
- **NAT Type: Strict** - some connections may fail

::: tip Workaround
Players on the same network as the server can try connecting from a different network (mobile hotspot) or VPN.
:::

### Steam Clients Can't Connect

Check server logs for these messages:

**GameServer init failed:**
```
[Steam] GameServer.Init() failed
```
Steamworks SDK may be missing or `steam_appid.txt` incorrect.

**Can't reach Steam:**
```
[Steam] Failed to connect to Steam servers: ...
```
Check outbound network access and firewall rules for UDP.

**SDR not ready:**
```
[Steam] SDR relay status: k_ESteamNetworkingAvailability_Unknown
```
SDR takes a few seconds to initialize - wait and retry.

### VNC Not Loading

1. Check `VNC_PASSWORD` is set in `.env`
2. Verify firewall allows TCP 5800
3. Use `http://` not `https://`

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                     Game Server Container                                │
├─────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌────────────────────┐       │
│  │ LidgrenServer   │  │ GalaxyNetServer │  │ SteamGameServer    │       │
│  │ (Direct IP)     │  │ (GOG P2P)       │  │ NetServer (SDR)    │       │
│  └─────────────────┘  └─────────────────┘  └────────────────────┘       │
└─────────────────────────────────────────────────────────────────────────┘
                                   │
                                   │ HTTP (lobby creation)
                                   ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                     Steam Auth Container                                 │
├─────────────────────────────────────────────────────────────────────────┤
│  Creates Steam lobbies via SteamKit2                                     │
│  Lobby metadata includes GameServer's Steam ID for client discovery      │
└─────────────────────────────────────────────────────────────────────────┘

Connections:
  Steam Client ──► Valve SDR Relay ──► Game Server
  GOG Client ──► Galaxy P2P ──► Game Server
  Any Client ──► Direct IP (UDP 24642) ──► Game Server
```

Steam lobbies require an authenticated Steam client. Since the GameServer API runs headless, the steam-auth container creates lobbies and includes the server's Steam ID for discovery.

## For Developers

### Key Implementation Files

- `SteamGameServerService.cs` - GameServer API and SDR initialization
- `SteamGameServerNetServer.cs` - Steam client connection handling
- `GalaxyNetServer.cs` - GOG client connection handling
- `steam-service/Program.cs` - Lobby management HTTP endpoints

### Steamworks API Notes

The server uses GameServer mode, not Client mode:

| Feature | Client Mode | GameServer Mode |
|---------|-------------|-----------------|
| Init | `SteamAPI.Init()` | `GameServer.Init()` |
| Callbacks | `SteamAPI.RunCallbacks()` | `GameServer.RunCallbacks()` |
| Networking | `SteamNetworkingSockets` | `SteamGameServerNetworkingSockets` |
| Matchmaking | `SteamMatchmaking` | Not available (via steam-auth) |

### References

- [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay)
- [ISteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets)
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET)
