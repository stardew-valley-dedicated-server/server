# Networking Internals

This document covers the technical implementation of JunimoServer's networking system.

## Architecture Overview

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

## Connection Methods

### Steam SDR (Steam Datagram Relay)

Steam clients connect through Valve's relay network:

1. Server logs in anonymously to Steam's GameServer API
2. Valve assigns the server a Steam ID
3. Steam-auth service creates a lobby with this ID
4. Players join the lobby and connect via Valve's relay

Benefits:
- No port forwarding required
- NAT traversal handled automatically
- ~99% connection success rate

### GOG Galaxy P2P

GOG clients connect using Galaxy's P2P networking:

- Invite codes prefixed with "G"
- Works through most NATs without port forwarding
- Lower success rate (~50%) than Steam SDR

### Direct IP (Lidgren)

Direct IP connections for specific use cases:

- Disabled by default
- Requires port forwarding (UDP 24642)
- No user ID tracking (farmhand ownership issues)

## Key Implementation Files

| File | Purpose |
|------|---------|
| `SteamGameServerService.cs` | GameServer API and SDR initialization |
| `SteamGameServerNetServer.cs` | Steam client connection handling |
| `GalaxyNetServer.cs` | GOG client connection handling |
| `steam-service/Program.cs` | Lobby management HTTP endpoints |

## Steamworks API Usage

The server uses GameServer mode, not Client mode:

| Feature | Client Mode | GameServer Mode |
|---------|-------------|-----------------|
| Init | `SteamAPI.Init()` | `GameServer.Init()` |
| Callbacks | `SteamAPI.RunCallbacks()` | `GameServer.RunCallbacks()` |
| Networking | `SteamNetworkingSockets` | `SteamGameServerNetworkingSockets` |
| Matchmaking | `SteamMatchmaking` | Not available (via steam-auth) |

## Lobby Creation

Steam lobbies require an authenticated Steam client. Since the GameServer API runs headless, the steam-auth container creates lobbies:

1. Steam-auth logs in with user credentials
2. Creates lobby via SteamMatchmaking
3. Sets lobby metadata with server's Steam ID
4. Clients discover server through lobby system

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 24642 | UDP | Steam SDR game port |
| 27015 | UDP | Steam SDR query port |
| 5800 | TCP | VNC web interface |
| 8080 | TCP | HTTP REST API |
| 3001 | TCP | Steam auth internal API |

## NAT Traversal

### Steam SDR

Traffic routes through Valve's global relay network, handling NAT automatically.

### GOG Galaxy

Uses STUN/TURN-like mechanisms for P2P connections. Less reliable than Steam SDR.

### Direct IP

Requires port forwarding or open NAT (Full Cone).

## Diagnosing Connection Issues

The `netdebug` tool provides network diagnostics:

```sh
docker compose exec server netdebug nat    # Check NAT type
docker compose exec server netdebug ping   # Test GOG server latency
docker compose exec server netdebug speed  # Test bandwidth
```

## References

- [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay)
- [ISteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets)
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET)

## Related Documentation

- [Networking (Admin Guide)](/admins/operations/networking) — Connection troubleshooting
- [Troubleshooting](/admins/troubleshooting) — Common issues
