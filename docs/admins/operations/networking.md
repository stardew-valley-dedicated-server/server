# Networking

JunimoServer supports both Steam and GOG clients connecting to the same server. Most setups require no port forwarding.

## Connection Methods

| Method | For | Port Forwarding | Reliability |
|--------|-----|-----------------|-------------|
| Steam SDR | Steam clients | Not required | ~99% success |
| GOG Galaxy | GOG clients | Not required | ~50% success |
| Direct IP | Any client | Required (UDP 24642) | Depends on network |

### Steam SDR (Recommended)

Steam clients connect through Valve's Steam Datagram Relay network. All traffic routes through Valve's servers, which handles NAT traversal automatically.

**How it works:**

1. Server logs in anonymously to Steam's GameServer API
2. Valve assigns the server a Steam ID
3. Steam-auth service creates a lobby with this ID
4. Players join the lobby and connect via Valve's relay

### GOG Galaxy

GOG clients connect using Galaxy P2P networking. Invite codes start with "G". Works through most NATs without port forwarding, but has lower success rates than Steam SDR.

### Direct IP

Disabled by default. Enable via `AllowIpConnections` in `server-settings.json`.

::: warning
Direct IP connections don't provide user IDs. Players may lose farmhand ownership if they reconnect from a different IP.
:::

## Ports

| Port | Protocol | Purpose | Forward Required? |
|------|----------|---------|-------------------|
| 24642 | UDP | Steam SDR game port | No (relay handles NAT) |
| 27015 | UDP | Steam SDR query port | No (relay handles NAT) |
| 5800 | TCP | VNC web interface | Only for remote access |
| 8080 | TCP | HTTP API | Only for external tools |

Steam SDR uses these ports internally but traffic goes through Valve's relay — no port forwarding required for most setups.

### Changing Ports

To change host-side port mappings (for conflicts):

```sh
# In .env
VNC_PORT=5801
API_PORT=8081
GAME_PORT=24643
QUERY_PORT=27016
```

## Troubleshooting

### Network Diagnostics

The container includes `netdebug` for troubleshooting:

```sh
# Check NAT type
docker compose exec server netdebug nat

# Test latency to GOG servers
docker compose exec server netdebug ping

# Test download speed
docker compose exec server netdebug speed

# Monitor GOG Galaxy traffic
docker compose exec server netdebug gog-ports
docker compose exec server netdebug gog-requests
```

### NAT Types

| Result | Description | Connection Quality |
|--------|-------------|-------------------|
| Open | Full Cone NAT | All connections work |
| Moderate | Restricted Cone | Most connections work |
| Strict | Symmetric NAT | May need relay for some |

### Players Can't Connect

**Basic checks:**

1. Server running? `docker compose ps`
2. Run `netdebug nat` to check network config

**Remote players** (different network) usually connect fine.

**Same-network players** often have issues due to "hairpinning" — traffic going out and coming back to the same network.

Check `netdebug nat` output:

- **Hairpinning: Not supported** — same-network connections won't work
- **NAT Type: Strict** — some connections may fail

::: tip Same-Network Workaround
Players on the same network can try:
- Connect from mobile hotspot
- Use a VPN service
- Enable direct IP connections (if acceptable)
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

SDR takes a few seconds to initialize — wait and retry.

### GOG Clients Can't Connect

1. Verify invite code starts with "G"
2. Run `netdebug nat` to check NAT type
3. Try having player connect from different network

## Firewall Configuration

If you need to configure firewalls, allow:

**For Steam/GOG players (default setup):**

- Outbound UDP (any port) — for relay connections
- Outbound TCP 443 — for Steam API

**For direct IP connections (if enabled):**

- Inbound UDP 24642 — game traffic

**For remote VNC access:**

- Inbound TCP 5800 — VNC web interface

**For external API access:**

- Inbound TCP 8080 — REST API

