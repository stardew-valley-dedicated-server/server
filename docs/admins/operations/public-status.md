---
description: Show your server's live status on a website — what /status exposes, the HTTPS requirement, and how to embed the status widget.
---

# Public Server Status

The API's `/status` endpoint reports whether the server is online, how many players are connected, the invite code, and the in-game date. It needs no API key, so a web page can read it directly and show your players a live status card.

## What `/status` exposes

The endpoint returns the fields documented in the [API reference](/developers/api/introduction). The invite code looks sensitive but is public information: JunimoServer forces the lobby public, so the code alone never gates entry. Turn on [password protection](/features/password-protection/) before publishing the status anywhere. The rest of the API needs `API_KEY`, so set one before exposing anything beyond `/status`.

## The HTTPS requirement

A browser on an HTTPS page refuses to fetch plain `http://<ip>:8080/status`, so the API needs an HTTPS URL. [HTTPS & Reverse Proxy](/admins/operations/reverse-proxy) sets that up, with or without a domain; the status URL is then the server's proxy URL plus `/status`. Cross-origin reads are already allowed, so nothing else is needed.

## Embedding the widget

### VitePress sites

Copy [`ServerStatusWidget.vue`](https://github.com/stardew-valley-dedicated-server/server/blob/master/docs/.vitepress/theme/ServerStatusWidget.vue) and the state mapping it imports, [`serverState.ts`](https://github.com/stardew-valley-dedicated-server/server/blob/master/tools/discord-bot/src/serverState.ts), into your theme and point the widget's import at the copied file. Register the component in `enhanceApp` and place it on a page:

```md
<ServerStatusWidget api-url="https://203.0.113.10/farm" />
```

Props:

| Prop | Description | Default |
|------|-------------|---------|
| `api-url` | HTTPS base URL of the API; the widget fetches `/status` under it | required |
| `refresh-interval` | Poll interval in milliseconds; `0` disables polling | `30000` |

The header shows the server's `SERVER_NAME`, else the farm name, the same rule the Discord bot uses for its nickname. The widget reports the same states as the bot: **Online** (`isOnline` and `isReady`), **Busy** (`isOnline` but saving, changing day, or running an event), **Starting** (`isOnline` false: the container is downloading game files, launching the game, or loading the save), and **Offline** (the request failed). It also shows the image version, the in-game clock, the measured tick rate, the browser's round-trip to the API, and the uptime beside the Online badge. The top accent line fills over one refresh interval, and a reload resumes the cycle from the last poll.

### Anywhere else

The whole contract is one `fetch`:

```js
const res = await fetch("https://203.0.113.10/farm/status", { cache: "no-store" });
if (!res.ok) throw new Error("unreachable");
const status = await res.json();
// status.isOnline, status.isReady, status.phase ("downloading" or "starting" before the game runs),
// status.playerCount, status.maxPlayers,
// status.steamInviteCode (the universal S-code; null until a Galaxy lobby exists),
// status.steamRelayReady (can Steam clients join the code right now),
// status.galaxyLobby ("connected" | "recovering" | "down"; null in LAN mode),
// status.steamSession ("connected" | "lost"), status.authReadiness ("ok" | "expiring" | "unavailable"),
// status.farmName, status.serverName (SERVER_NAME, empty when unset),
// status.season, status.day, status.year, status.timeOfDay,
// status.serverVersion (the running image version), status.gameVersion,
// status.tps (measured game ticks per second, a 30-second average),
// status.startedAtUtc (when the server process started; null until known)
```
