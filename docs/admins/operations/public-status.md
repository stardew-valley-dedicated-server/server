---
description: Show your server's live status on a website — what /status exposes, the HTTPS requirement, and how to embed the status widget.
---

# Public Server Status

The API's `/status` endpoint reports whether the server is online, how many players are connected, the current invite codes, and the in-game date. It needs no API key, so a web page can read it directly and show your players a live status card.

## What `/status` exposes

The endpoint returns the fields documented in the [API reference](/developers/api/introduction). The sensitive-looking ones are the invite codes, and they are already public information: JunimoServer forces the lobby public, so the code alone never gates entry. Turn on [password protection](/features/password-protection/) before publishing the status anywhere. Every other endpoint stays behind `API_KEY`.

## The HTTPS requirement

A browser on an HTTPS page refuses to fetch plain `http://<ip>:8080/status` as mixed content, so the API needs an HTTPS URL. [HTTPS & Reverse Proxy](/admins/operations/reverse-proxy) sets that up without a domain; the status URL is then `https://PUBLIC_IP/SERVER_SLUG/status`. The server already allows cross-origin reads (every JSON response carries `Access-Control-Allow-Origin: *`), so nothing else is needed.

## Embedding the widget

### VitePress sites

Copy [`ServerStatusWidget.vue`](https://github.com/stardew-valley-dedicated-server/server/blob/master/docs/.vitepress/theme/ServerStatusWidget.vue) into your theme, register it in `enhanceApp`, and place it on a page:

```md
<ServerStatusWidget api-url="https://203.0.113.10/farm/status" title="My Farm" />
```

Props:

| Prop | Description | Default |
|------|-------------|---------|
| `api-url` | HTTPS URL of the `/status` endpoint | required |
| `title` | Header text | `Server Status` |
| `refresh-interval` | Poll interval in milliseconds; `0` disables polling | `30000` |

The widget shows four states: **live** (`isOnline` true), **offline** (server running, no farm loaded), **unreachable** (fetch failed or a non-200 response), and **stale** (`lastUpdated` older than two minutes, meaning the server stopped refreshing its snapshot).

### Anywhere else

The whole contract is one `fetch`:

```js
const res = await fetch("https://203.0.113.10/farm/status", { cache: "no-store" });
if (!res.ok) throw new Error("unreachable");
const status = await res.json();
// status.isOnline, status.playerCount, status.maxPlayers,
// status.steamInviteCode (null until the Steam lobby is published), status.gogInviteCode,
// status.farmName, status.season, status.day, status.year, status.lastUpdated
```
