---
description: Serve the API and VNC web UI over HTTPS with a Let's Encrypt IP-address certificate — no domain needed — using the bundled proxy or your own.
---

# HTTPS & Reverse Proxy

By default the server publishes its HTTP surfaces as plain HTTP: the REST API on `API_PORT` and the VNC web UI on `VNC_PORT`. That is fine for a home or LAN server and needs no setup.

Two things need HTTPS: showing the [live status widget](/admins/operations/public-status) on a web page, since browsers refuse plain-HTTP requests from an HTTPS site, and not sending your VNC and API credentials in clear text over the internet. This page covers both with no domain name: the certificate is issued for the server's public IP address.

## The three setups

| Setup | For | You set | Reachable from outside |
|-------|-----|---------|------------------------|
| No proxy (default) | Home and LAN servers | nothing | plain HTTP on `API_PORT` and `VNC_PORT` |
| Bundled proxy | One server on a VPS | `COMPOSE_PROFILES`, `PUBLIC_IP`, `SERVER_SLUG`, `HTTP_BIND` | HTTPS on 443 only |
| Your own proxy | Hosts that already run a proxy, several servers on one host | `SERVER_SLUG`, `HTTP_BIND`, your proxy's routing | whatever your proxy exposes |

Whichever proxy runs, every server lives under its own URL prefix, `SERVER_SLUG`, so the URL scheme is the same for one server or many:

| Surface | URL |
|---------|-----|
| API | `https://PUBLIC_IP/SERVER_SLUG/status`, `/players`, `/docs`, and so on |
| VNC web UI | `https://PUBLIC_IP/SERVER_SLUG/vnc/` |

`SERVER_SLUG` is a deployment setting, not the farm name: lowercase letters, digits, and hyphens. Pick something stable like `farm` or `preview`.

## How the certificate works

Let's Encrypt issues certificates for IP addresses since January 2026. They are short-lived, valid for six days, and the proxy renews them automatically. Validation happens over port 80, so the requirements are:

- a public IPv4 address on this host,
- ports 80 and 443 free and reachable from the internet.

A server behind a home router cannot get one, which is why the default setup stays plain HTTP.

## Bundled proxy

Add to `.env`:

```sh
COMPOSE_PROFILES=proxy
PUBLIC_IP=203.0.113.10   # your server's public IP
SERVER_SLUG=farm
HTTP_BIND=127.0.0.1
```

Then `docker compose up -d`. The `proxy` service (Caddy) starts alongside the server, obtains the certificate, and serves `https://203.0.113.10/farm/`. `HTTP_BIND=127.0.0.1` keeps the plain-HTTP ports on the host's loopback interface so they are no longer reachable from outside; the proxy talks to the server over the compose network.

The proxy configuration is [`docker/proxy/Caddyfile`](https://github.com/stardew-valley-dedicated-server/server/blob/master/docker/proxy/Caddyfile). It reads `PUBLIC_IP`, `SERVER_SLUG`, and `API_PORT` from the environment and needs no editing.

Check it worked:

```sh
docker compose logs proxy        # "certificate obtained successfully"
curl https://203.0.113.10/farm/health
```

## Your own proxy

Skip the `proxy` profile, keep `SERVER_SLUG` and `HTTP_BIND=127.0.0.1` in `.env`, and route the prefix to the container yourself. The proxy must strip the prefix, and the VNC route needs WebSocket pass-through. With Caddy, the site block per server is:

```text
handle_path /farm/vnc/* {
    reverse_proxy sdvd-server:5800
}
handle_path /farm/* {
    reverse_proxy sdvd-server:8080
}
```

with `tls { issuer acme { profile shortlived } }` on the site and your proxy attached to the stack's compose network. Add a block per server with its own slug for several servers on one host. Other proxies work the same way as long as their ACME client supports the `shortlived` profile for IP certificates; Traefik does since v3.6.

## Firewall

With a proxy, only TCP 80 and 443 need to be open for HTTP traffic. `API_PORT` and `VNC_PORT` stay closed. The game ports are unchanged, see [Networking](/admins/operations/networking#ports).
