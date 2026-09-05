---
description: Show your server's live status on a website — expose /status over HTTPS through a Cloudflare Worker or your own reverse proxy, and embed the status widget.
---

# Public Server Status

The API's `/status` endpoint reports whether the server is online, how many players are connected, the current invite codes, and the in-game date. It needs no API key, so a web page can read it directly and show your players a live status card.

## What `/status` exposes

The endpoint returns the fields documented in the [API reference](/developers/api/introduction). The sensitive-looking ones are the invite codes, and they are already public information: JunimoServer forces the lobby public, so the code alone never gates entry. Turn on [password protection](/features/password-protection/) before publishing the status anywhere. Every other endpoint stays behind `API_KEY`.

## The HTTPS requirement

A browser on an HTTPS page refuses to fetch plain `http://<ip>:8080/status` as mixed content. The server already allows cross-origin reads (every JSON response carries `Access-Control-Allow-Origin: *`), so the only missing piece is an HTTPS origin in front of the API. There are two ways to get one.

### Route 1: Cloudflare Worker

A Worker on the free plan gives you a `workers.dev` hostname with TLS and a short cache, and nothing new runs on your server. The script lives in [`tools/status-worker/`](https://github.com/stardew-valley-dedicated-server/server/tree/master/tools/status-worker).

Prerequisites:

- **A DNS name for your server.** Workers cannot fetch an IP literal, so point an A record at the server (a DNS-only record; it does not need to be proxied).
- **A supported port.** Workers only reach ports on [Cloudflare's supported port list](https://developers.cloudflare.com/fundamentals/reference/network-ports/). The default `API_PORT` of 8080 is on it.

Deploy from a checkout of the repository:

```sh
cd tools/status-worker
# Set UPSTREAM_URL in wrangler.toml to http://<your-dns-name>:<API_PORT>
npx wrangler login
npx wrangler deploy
```

The Worker answers `GET /` with the upstream JSON, caches it for 30 seconds, and returns an empty `502` when the server does not answer so the widget shows it as unreachable.

#### Firewall rule

Once the Worker is the only intended client, restrict the API port to [Cloudflare's published IP ranges](https://www.cloudflare.com/ips/). Docker publishes container ports through its own iptables chain, which bypasses host rules on `INPUT`, so add the rule to the `DOCKER-USER` chain:

```sh
API_PORT=8080
for range in $(curl -s https://www.cloudflare.com/ips-v4); do
  iptables -I DOCKER-USER -p tcp --dport "$API_PORT" -s "$range" -j ACCEPT
done
for range in $(curl -s https://www.cloudflare.com/ips-v6); do
  ip6tables -I DOCKER-USER -p tcp --dport "$API_PORT" -s "$range" -j ACCEPT
done
iptables  -A DOCKER-USER -p tcp --dport "$API_PORT" -j DROP
ip6tables -A DOCKER-USER -p tcp --dport "$API_PORT" -j DROP
```

Persist the rules with your distribution's iptables save mechanism. Cloudflare changes its ranges rarely and announces additions ahead of time, so a periodic refresh of this script is enough.

### Route 2: your own TLS reverse proxy

If you already have a domain and a reverse proxy, put it in front of the API port and point the widget straight at `https://<host>/status`. The server's own CORS header does the rest. A minimal Caddy site:

```text
status.example.com {
    reverse_proxy localhost:8080
}
```

Caddy obtains the certificate automatically. With this route the firewall rule is your proxy's concern; the API port itself should not be reachable from the internet.

## Embedding the widget

### VitePress sites

Copy [`ServerStatusWidget.vue`](https://github.com/stardew-valley-dedicated-server/server/blob/master/docs/.vitepress/theme/ServerStatusWidget.vue) into your theme, register it in `enhanceApp`, and place it on a page:

```md
<ServerStatusWidget api-url="https://<your-worker>.workers.dev/" title="My Farm" />
```

Props:

| Prop | Description | Default |
|------|-------------|---------|
| `api-url` | HTTPS URL returning the `/status` JSON (the Worker root, or `https://<host>/status`) | required |
| `title` | Header text | `Server Status` |
| `refresh-interval` | Poll interval in milliseconds; `0` disables polling | `30000` |

The widget shows four states: **live** (`isOnline` true), **offline** (server running, no farm loaded), **unreachable** (fetch failed or a non-200 response), and **stale** (`lastUpdated` older than two minutes, meaning the server stopped refreshing its snapshot).

### Anywhere else

The whole contract is one `fetch`:

```js
const res = await fetch("https://<your-worker>.workers.dev/", { cache: "no-store" });
if (!res.ok) throw new Error("unreachable");
const status = await res.json();
// status.isOnline, status.playerCount, status.maxPlayers,
// status.steamInviteCode (null until the Steam lobby is published), status.gogInviteCode,
// status.farmName, status.season, status.day, status.year, status.lastUpdated
```
