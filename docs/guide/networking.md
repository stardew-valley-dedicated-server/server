# Networking & Connectivity

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 24642 | UDP | Game traffic |
| 5800 | TCP | VNC web interface |

## Connection Methods

### GOG Galaxy (Default)

Players connect using invite codes (prefixed with "G"). This works through most NATs and firewalls automatically without port forwarding.

### Direct IP

Disabled by default via `ALLOW_IP_CONNECTIONS=false`.

::: warning
Direct IP connections don't provide user IDs, so the server can't track farmhand ownership. Players may lose access to their farmhands if they reconnect from a different IP. Only enable if you understand these limitations.
:::

## Troubleshooting

### Players Can't Connect

1. Check the server is running: `docker compose ps`
2. Have someone on the same LAN try first
3. Verify firewall allows UDP 24642
4. If behind NAT, configure port forwarding for UDP 24642

### VNC Not Loading

1. Check `VNC_PASSWORD` is set in `.env`
2. Verify firewall allows TCP 5800
3. Use `http://` not `https://`
