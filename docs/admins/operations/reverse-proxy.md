---
description: Serve the API and VNC web UI over HTTPS with a Let's Encrypt certificate — for a bare IP or a domain, with path or subdomain routing — using the bundled proxy or your own.
---

# HTTPS & Reverse Proxy

By default the REST API (`API_PORT`) and the VNC web UI (`VNC_PORT`) are plain HTTP, which is fine for a home or LAN server. HTTPS keeps your API and VNC credentials off the wire, and it is required to show the [live status widget](/admins/operations/public-status) on a web page, since browsers block plain-HTTP requests from HTTPS sites. This page sets it up with or without a domain: Let's Encrypt also issues certificates for a bare public IP.

## Setups

| Setup | Use case | Set | Exposed |
|-------|-----|---------|------------------------|
| No proxy (default) | Home and LAN servers | nothing | plain HTTP on `API_PORT` and `VNC_PORT` |
| Bundled proxy | One server on a VPS | `COMPOSE_PROFILES=proxy`, `PUBLIC_HOST`, `SERVER_URL_NAME`, `PROXY_ROUTING` | HTTPS on 443 only |
| Your own proxy | A host with an existing or shared proxy | `PUBLIC_HOST`, a shared `proxy` network + your proxy's routing | whatever your proxy exposes |

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

## Certificate

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

When the host already runs a reverse proxy — or runs other services that should share one — skip the bundled `proxy` profile and route to the server from your proxy. Keep `PUBLIC_HOST` in `.env` — it binds the plain API and VNC ports to loopback. Routing now lives in your proxy, not in these variables; `SERVER_URL_NAME` just names the path prefix the examples below route to.

Connect the two through a dedicated external Docker network that both the proxy and the server join, rather than attaching the proxy to this stack's network — one shared network scales to every service on the host. Create it once:

```sh
docker network create proxy
```

Attach the server to it with a compose override, `docker-compose.proxy.yml` beside your `.env`:

```yaml
# docker compose -f docker-compose.yml -f docker-compose.proxy.yml up -d
services:
  server:
    networks: [default, proxy]   # default keeps server↔steam-auth; proxy reaches the proxy
    labels:
      - traefik.enable=true
      - traefik.docker.network=proxy
      # API at the domain root
      - traefik.http.routers.api.rule=Host(`farm.example.com`)
      - traefik.http.routers.api.entrypoints=websecure
      - traefik.http.routers.api.tls.certresolver=le
      - traefik.http.routers.api.service=api
      - traefik.http.services.api.loadbalancer.server.port=${API_PORT:-8080}
      # VNC web UI at /vnc/ — prefix stripped, WebSocket passes through natively
      - traefik.http.routers.vnc.rule=Host(`farm.example.com`) && PathPrefix(`/vnc`)
      - traefik.http.routers.vnc.entrypoints=websecure
      - traefik.http.routers.vnc.tls.certresolver=le
      - traefik.http.routers.vnc.service=vnc
      - traefik.http.routers.vnc.middlewares=vnc-strip
      - traefik.http.middlewares.vnc-strip.stripprefix.prefixes=/vnc
      - traefik.http.services.vnc.loadbalancer.server.port=5800

networks:
  proxy:
    external: true
```

The `/vnc` router is more specific than the root API router, so Traefik's default rule-length priority sends `/vnc/*` to the VNC UI and everything else to the API. Reach VNC at `https://farm.example.com/vnc/` (trailing slash), the same URL the bundled proxy uses. This is subdomain routing; for path routing, prefix both routers with `/SERVER_URL_NAME`: give the API router a `PathPrefix` rule for `/SERVER_URL_NAME` and a matching `stripprefix`, and change the VNC rule to match `/SERVER_URL_NAME/vnc` and strip that same prefix.

### Recommended: Traefik with a socket proxy

Run Traefik as its own stack so every service on the host shares it, and give it the Docker API through a read-only socket proxy rather than the raw socket — Traefik never needs write access, and mounting the bare socket is root-equivalent on the host. `docker-compose.traefik.yml` in its own directory:

```yaml
services:
  traefik:
    image: traefik:v3
    command:
      - --providers.docker=true
      - --providers.docker.exposedbydefault=false
      - --providers.docker.endpoint=tcp://dockerproxy:2375   # not the socket
      - --providers.docker.network=proxy
      - --entrypoints.web.address=:80
      - --entrypoints.websecure.address=:443
      - --certificatesresolvers.le.acme.email=you@example.com
      - --certificatesresolvers.le.acme.storage=/letsencrypt/acme.json
      - --certificatesresolvers.le.acme.httpchallenge.entrypoint=web
    ports: ["80:80", "443:443"]
    volumes:
      - ./letsencrypt:/letsencrypt   # persist certs across restarts
    networks: [proxy, socket]
    restart: unless-stopped

  dockerproxy:
    image: tecnativa/docker-socket-proxy
    environment:
      CONTAINERS: 1   # discover containers and their labels
      NETWORKS: 1     # resolve traefik.docker.network
      # Traefik also reads the events stream, on by default; POST and all write access stay denied
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    networks: [socket]
    restart: unless-stopped

networks:
  proxy:
    external: true
  socket:
    internal: true   # traefik ↔ socket proxy only, off the shared network
```

Only `dockerproxy` touches the socket, read-only and limited to container and network listing; Traefik reads it over an internal network and owns ports 80 and 443. Adapt from here for your own needs — pin image tags, add access logs or a dashboard, or swap the ACME challenge.

Already running Caddy or another proxy? Route to `server:$API_PORT` and `server:5800` on the shared network the same way; the VNC route needs WebSocket pass-through, and reusing the bundled [`docker/proxy/`](https://github.com/stardew-valley-dedicated-server/server/tree/master/docker/proxy) config additionally requires an ACME client that supports the `shortlived` profile.

## Firewall

With a proxy, open only TCP 80 and 443 for HTTP traffic; `API_PORT` and `VNC_PORT` stay closed. The game ports are unchanged, see [Networking](/admins/operations/networking#ports).
