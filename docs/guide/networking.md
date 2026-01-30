# Networking & Connectivity

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 24642 | UDP | Direct IP connections (disabled by default) |
| 5800 | TCP | VNC web interface |

GOG Galaxy connections use random UDP ports and don't require port forwarding in most cases.

## Connection Methods

### GOG Galaxy (Default)

Players connect using invite codes (prefixed with "G"). This works through most NATs and firewalls automatically without port forwarding.

### Direct IP

Disabled by default via `Server.AllowIpConnections` in `server-settings.json`.

::: warning
Direct IP connections don't provide user IDs, so the server can't track farmhand ownership. Players may lose access to their farmhands if they reconnect from a different IP. Only enable if you understand these limitations.
:::

## Troubleshooting

### Network Diagnostics

The container includes `netdebug`, a diagnostic tool for troubleshooting connectivity:

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

#### NAT Type Results

| Result | Description |
|--------|-------------|
| Open | Full Cone NAT - best for P2P, all connections work |
| Moderate | Restricted Cone - most connections work |
| Strict | Symmetric NAT - may require relay for some connections |

### Players Can't Connect

1. Check the server is running: `docker compose ps`
2. Run `netdebug nat` to check your network configuration

**Remote players** (different network than server) usually connect without issues.

**Same-network players** (on your LAN or same machine as server) may have trouble due to "hairpinning" - when traffic goes out to the internet and needs to come back to the same network. Many home routers don't support this.

Check `netdebug nat` output:
- If **Hairpinning** shows "Not supported", same-network connections won't work reliably
- If **NAT Type** shows "Strict", some connections may fail

::: tip Workaround for Same-Network Players
If hairpinning isn't supported, players on the same network as the server may need to connect from a different network (e.g., mobile hotspot) or use a VPN.
:::

::: warning Under Investigation
NAT traversal with GOG Galaxy is still being investigated. These workarounds may not resolve all connection issues.
:::

### VNC Not Loading

1. Check `VNC_PASSWORD` is set in `.env`
2. Verify firewall allows TCP 5800
3. Use `http://` not `https://`
