# Verify: Steam-behind-a-proxy on the default bridge network (issue #157)

## Context

Issue #157: a reporter in China switched `network_mode: host` to make their outbound
Steam proxy work, which broke xvnc ("not ready after 10000 msec") and would also break
this stack's service-name DNS (`http://steam-auth:3001`, `http://server:8080`).

Static code analysis (already done) established a strong hypothesis: **the reporter does
not need host networking at all.** Only the `steam-auth` sidecar talks to Steam, it uses
SteamKit2 v3.4.0 with default config, that default connects over a **WebSocket (HTTPS)**
built from a default `new HttpClient()`, and a default .NET `HttpClient` honors
`HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` env vars on Linux. So setting a proxy env var on the
**`steam-auth` service only**, on the **default bridge network**, should route Steam
auth/CM traffic through the proxy — no xvnc breakage, no DNS breakage.

This plan proves that hypothesis **end-to-end in throwaway containers**, then posts an
evidence-backed comment on #157. Code chain verified: `SteamAuthService.cs:98`
(`new SteamClient()`), `SteamConfigurationBuilder.cs` (`ProtocolTypes = Tcp|WebSocket`,
`DefaultHttpClientFactory`), `CMClient.cs:415` (factory → `WebSocketConnection`),
`WebSocketContext.cs:50` (`socket.ConnectAsync(uri, invoker, …)`).

**The verification builds the existing image and runs throwaway containers/networks, then
tears them down — no source edits, no commits, no changes to `.env`/compose.** The only
outward-facing action is the final #157 comment (Step 6), posted _after_ evidence is in hand
and shown to the user.

## What this test proves — and what it deliberately does NOT (honesty bound)

**Proves:** our software stack honors a proxy env var on the _default bridge network_ for the
Steam **auth/CM** path — i.e. the reporter does **not** need `network_mode: host` (and the
xvnc breakage it causes) to proxy Steam login.

**Does NOT prove (state plainly in the comment):**

- **Their environment.** We test from an unrestricted network and _simulate_ "proxy required"
  with a Docker `--internal` net. We cannot test from inside China, and we don't know their
  proxy software/address (Shadowsocks/Clash/HTTP/etc.). Green here ≠ guaranteed green there.
- **UDP gameplay relay.** Stardew multiplayer runs over Steam Datagram Relay on **UDP**
  (`GAME_PORT=24642/udp`, `QUERY_PORT=27015/udp`), which an HTTP/SOCKS proxy does **not**
  carry. So even green auth-through-proxy may still leave actual hosting blocked from China.
  This is out of scope for any HTTP/SOCKS proxy and must be flagged as an open caveat.

## What "verified" must mean (the trap to avoid)

Per `runtime-post-conditions-are-gates.md`: a login succeeding **with a proxy env set
proves nothing** — the host has direct Steam egress, so login would succeed even if the
proxy var were silently ignored. The decisive gate is a **deny-direct / force-proxy** setup:

1. **Negative control** — block direct Steam egress, set **no** proxy → login must **fail**
   (proves the blocker actually blocks Steam).
2. **Force-proxy** — block direct Steam egress, set proxy env → login must **succeed** AND
   the proxy's access log must show Steam CM hostnames passing through it (proves the proxy
   var is honored and is the _only_ path that worked).
3. **Baseline** — default network, no proxy → login succeeds (sanity: creds/build are fine).

Only if #1 fails-closed and #2 succeeds-through-proxy is the hypothesis confirmed.

## Prerequisites (read-only checks first)

- Steam creds: `.env` has `STEAM_USERNAME`/`STEAM_PASSWORD` (account `hzlpo73263`) plus a
  `STEAM_ACCOUNTS` JSON line. **These accounts have no Steam Guard** (confirmed by user), so
  login is fully non-interactive directly from `STEAM_USERNAME`/`STEAM_PASSWORD` — no
  `make setup` / token-export needed. Pass these env vars straight into each throwaway
  container.
- Verify Docker Desktop is running and `make build-steam-service` succeeds (wraps
  `docker compose build steam-auth`, Makefile:73-75).

## Verification harness (all throwaway, all in `/tmp`-style scratch)

Build the image once, then run the scenarios below on throwaway networks/containers. The
decisive force-proxy test is run **twice** — once with SOCKS5 (`ALL_PROXY`) and once with
HTTP CONNECT (`HTTPS_PROXY`) — since China setups use both (user choice: cover both).

### Network topology (egress isolation = the decisive ingredient)

User choice: **Docker `internal` network + proxy bridge**. Two throwaway networks:

- `sdvd-157-internal` — created with `--internal` (no host/internet egress). `steam-auth`
  attaches **only** here, so it has zero direct path to Steam.
- `sdvd-157-egress` — normal bridge (has internet egress). The **proxy** attaches to **both**
  networks, so it is the only route from the internal net to the outside world.

This makes the negative control fail-closed by construction: with no proxy var,
`steam-auth` on `sdvd-157-internal` simply cannot reach Steam.

### Step 0 — Build

- `make build-steam-service` (image tag `sdvd/steam-service:<IMAGE_VERSION>`; reuse whatever
  tag compose produces).
- `docker network create --internal sdvd-157-internal`
- `docker network create sdvd-157-egress`

### Step 1 — Throwaway proxy container (dual: SOCKS5 + HTTP)

- Run a proxy attached to **both** networks, name `verify-proxy`, with an **accessible
  access log** (so we can read which upstream hosts it dialed — the proof traffic went
  through it). Candidates:
    - SOCKS5: `serjs/go-socks5-proxy` (logs dialed hosts) on :1080.
    - HTTP CONNECT: `ubuntu/squid` (access.log shows CONNECT targets) on :3128.
    - If one image can't do both, run two proxy containers (one per style) — both on both nets.
- Confirm the proxy itself has egress (it's on `sdvd-157-egress`).

All `steam-auth` runs below pass `STEAM_USERNAME` + `STEAM_PASSWORD` (from `.env`) and run
the default `serve` command. Poll `GET /health` (`logged_in` field) and `GET /steam/ready`
(`ready` field; also fetches a ticket = full-readiness proof) for a bounded window
(~45–60s). Map the API port to the host (e.g. `-p 18080:3001`) only on the egress net, or
`docker exec` the in-container healthcheck — either way, the API probe must not become a
back-channel that defeats the egress isolation.

### Step 2 — Baseline (sanity, normal egress, no proxy)

- Run `steam-auth serve` on `sdvd-157-egress` only, no proxy env, normal egress.
- **Gate:** baseline login succeeds (`logged_in:true` / `ready:true`). If it fails, creds or
  build are the problem — stop and fix before drawing any proxy conclusions.

### Step 3 — Negative control (internal net, no proxy)

- Run `steam-auth serve` on **`sdvd-157-internal` only**, **no** proxy env.
- **Gate:** login must **fail** (`logged_in:false` / `ready:false`) for the whole window.
  Proves the isolation is real. If it _succeeds_, the internal net is leaking egress — fix
  isolation before trusting Step 4 (re-create the network with `--internal`, confirm no
  other network is attached).

### Step 4a — Force-proxy via SOCKS5 — THE decisive test

- Run `steam-auth serve` on **`sdvd-157-internal` only** (no direct egress), with
  `ALL_PROXY=socks5://verify-proxy:1080`.
- **Gates (both required):**
    1. login **succeeds** (`logged_in:true` / `ready:true`); and
    2. the **proxy access log shows Steam CM WebSocket hosts** (e.g. `*.steamserver.net` /
       `cm*.cm.steampowered.com`, port 443) — proving traffic went through the proxy and the
       proxy was the _only_ path that could have worked (Step 3 proved no direct path exists).
- Capture the `/health`+`/steam/ready` JSON and the matching proxy log lines as evidence.

### Step 4b — Force-proxy via HTTP CONNECT

- Same as 4a but `HTTPS_PROXY=http://verify-proxy:3128` (Squid). Note: SteamKit's CM
  connection is WebSocket-over-TLS (443), so an HTTP proxy must allow **CONNECT** to :443 —
  verify Squid's default ACLs permit CONNECT 443 (they normally do); if not, the test config
  (not the repo) needs the ACL, and we note that as a proxy-setup requirement for docs.
- **Gates:** same two as 4a, reading Squid `access.log` for `CONNECT cm*...:443`.

### Step 5 — Teardown

- Stop/rm all throwaway containers (`steam-auth` runs, `verify-proxy`),
  `docker network rm sdvd-157-internal sdvd-157-egress`. Nothing untracked/generated is
  deleted — only ephemeral containers and the throwaway networks created by this run. No
  volumes touched (creds come from env, not a seeded session).

### Step 6 — Present evidence, then post the #157 comment (gated on results)

- First, show the user the captured verdict: baseline pass, negative-control fail-closed,
  4a + 4b pass-through-proxy with proxy-log excerpts. **Do not post if the gates didn't pass**
  the way the hypothesis predicts — if Step 3 leaked or Step 4 failed, report that instead
  and stop (runtime evidence outranks the plan's prediction).
- If green, draft and post a single #157 comment containing:
    1. **Root cause**: the xvnc timeout is a side effect of `network_mode: host`; host mode is
       unnecessary here and also breaks service-name DNS (`http://steam-auth:3001`,
       `http://server:8080`).
    2. **Verified fix for the auth path**: keep the default (bridge) network; set
       `HTTPS_PROXY`/`ALL_PROXY` on the **`steam-auth` service only** (the sole container that
       talks to Steam). State that we verified both SOCKS5 and HTTP-CONNECT proxies route
       Steam login/CM traffic on the bridge net, with a one-line note on how we tested.
    3. **Honest caveats** (per the "honesty bound" section): we couldn't reproduce from China;
       and **UDP gameplay relay** (24642/27015 udp) is not carried by an HTTP/SOCKS proxy, so
       auth-through-proxy may not be sufficient for full hosting from China.
    4. **Ask** for their proxy type + sidecar logs so we can help further, rather than closing
       blind on a 4-month-stale thread.
- Leave open vs. close: recommend leaving **open** pending their reply (we just gave concrete,
  verified guidance). Confirm posting wording with the user before it goes out.

## Deliverable

- A short written verdict with captured evidence (baseline / negative-control / 4a / 4b +
  proxy-log excerpts + the China/UDP caveats), shown to the user.
- A posted #157 comment (Step 6), gated on green results and on the user approving the
  wording.

## Out of scope (explicit)

- Editing repo source, `.env`, `docker-compose.yml`, or production docs (a "Steam behind a
  proxy" docs section is a possible _future_ follow-up, not part of this plan).
- Closing #157 (recommend leaving open for the reporter's reply).
- Fixing the xvnc-under-host-networking timeout (base-image owned; the whole point is that
  the reporter shouldn't need host networking).
- Verifying UDP gameplay relay through a proxy (not solvable via HTTP/SOCKS proxy) or
  reproducing connectivity from inside China.
