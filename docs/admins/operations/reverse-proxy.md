---
description: Serve the API and VNC web UI over HTTPS with a Let's Encrypt certificate — for a bare IP or a domain, with path or subdomain routing — using the bundled proxy or your own.
---

# HTTPS & Reverse Proxy

By default the REST API (`API_PORT`) and the VNC web UI (`VNC_PORT`) are plain HTTP, which is fine for a home or LAN server. HTTPS is needed to show a live status widget on a web page, since browsers block plain-HTTP requests from HTTPS sites, and to keep your VNC and API credentials off the wire. This page sets it up with or without a domain: Let's Encrypt also issues certificates for a bare public IP.

## The three setups

| Setup | For | You set | Reachable from outside |
|-------|-----|---------|------------------------|
| No proxy (default) | Home and LAN servers | nothing | plain HTTP on `API_PORT` and `VNC_PORT` |
| Bundled proxy | One server on a VPS | `COMPOSE_PROFILES=proxy`, `PUBLIC_HOST`, `SERVER_URL_NAME`, `PROXY_ROUTING` | HTTPS on 443 only |
| Your own proxy | Hosts that already run a proxy | `PUBLIC_HOST`, `SERVER_URL_NAME`, your proxy's routing | whatever your proxy exposes |

Setting `PUBLIC_HOST` declares that a proxy sits in front of the server: the plain-HTTP ports then bind to loopback and are unreachable from outside. Set it only with a proxy in place.

## Variables and URLs

| Variable | Meaning | Example |
|----------|---------|---------|
| `PUBLIC_HOST` | Public IP or domain the certificate is issued for | `203.0.113.10`, `example.com` |
| `SERVER_URL_NAME` | This server's name in its URL: lowercase letters, digits, hyphens | `farm`, `preview` |
| `PROXY_ROUTING` | `path` (default) or `subdomain` | `subdomain` |

`SERVER_URL_NAME` is not the farm name.

| Routing | API | VNC web UI |
|---------|-----|------------|
| `path` | `https://PUBLIC_HOST/SERVER_URL_NAME/status`, `/players`, `/docs`, … | `https://PUBLIC_HOST/SERVER_URL_NAME/vnc/` |
| `subdomain` | `https://SERVER_URL_NAME.PUBLIC_HOST/status`, … | `https://SERVER_URL_NAME.PUBLIC_HOST/vnc/` |

`path` works for an IP and for a domain. `subdomain` needs a domain, with a DNS record (or wildcard) for that name pointing at the host; with an IP host the certificate request fails, visible in `docker compose logs proxy`.

## How the certificate works

The proxy requests short-lived Let's Encrypt certificates (six days, renewed automatically), the type Let's Encrypt issues for IP addresses; they work for domains too. Validation runs over port 80, so you need:

- a public IP or a domain resolving to this host,
- ports 80 and 443 free and reachable from the internet.

A server behind a home router without port forwarding cannot get one, which is why the default setup stays plain HTTP.

## Bundled proxy

Add to `.env`:

```sh
PUBLIC_HOST=203.0.113.10   # a proxy sits in front (or example.com)
COMPOSE_PROFILES=proxy     # ...and it is the bundled one
SERVER_URL_NAME=farm
```

Then `docker compose up -d`. The `proxy` service (Caddy) starts alongside the server, obtains the certificate, and serves `https://203.0.113.10/farm/`. For `https://farm.example.com/` instead, set `PUBLIC_HOST=example.com` and `PROXY_ROUTING=subdomain`.

The proxy configuration lives in [`docker/proxy/`](https://github.com/stardew-valley-dedicated-server/server/tree/master/docker/proxy): `common.caddy` holds the routing for one server, and `Caddyfile.path` / `Caddyfile.subdomain` supply the site address. It reads the variables above from the environment and needs no editing.

Check it worked:

```sh
docker compose logs proxy        # "certificate obtained successfully"
curl https://203.0.113.10/farm/health
```

## Your own proxy

Skip the `proxy` profile, keep `PUBLIC_HOST` and `SERVER_URL_NAME` in `.env`, attach your proxy to the stack's compose network, and route the server's URL to the container. The proxy must strip the path prefix, and the VNC route needs WebSocket pass-through. With Caddy, reuse the same snippet:

```text
import common.caddy

# path routing
203.0.113.10 {
    import server /farm
}

# or subdomain routing
farm.example.com {
    import server ""
}
```

Other proxies work the same way as long as their ACME client supports the `shortlived` profile.

## Firewall

With a proxy, open only TCP 80 and 443 for HTTP traffic; `API_PORT` and `VNC_PORT` stay closed. The game ports are unchanged, see [Networking](/admins/operations/networking#ports).
