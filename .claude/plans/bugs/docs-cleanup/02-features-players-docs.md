# 02 — Features & Players Docs Corrections

**Objective:** Make the player- and feature-facing pages describe what the mod actually does —
especially password-protection flow, invite codes, cabin behavior, and the REST feature summary.

**Scope:** `docs/features/**` (except backup.md → plan 01), `docs/players/**`, `docs/community/`
link fixes. `docs/features/rest-api.md` content fixes live here; the API *reference* fixes live in
plan 04.

**Verification gate:** `make docs` builds clean; the corrected behavioral claims each cite the code
path below (re-check the cited line before rewording — don't transcribe blind).

---

## High

### H1. `rest-api.md` — claims a settings-write capability that doesn't exist
- **Files:** `docs/features/rest-api.md:10` ("Read and modify settings"), `:20` ("Settings | Read/write server configuration")
- **Problem:** the only settings route is `GET /settings` (`ApiService.cs:3756`); the POST
  dispatcher switch (`:2018-2058`) has no `/settings`, no PUT/PATCH anywhere. Settings changes go
  through editing `server-settings.json` + `POST /reload` (`:4544-4548`). Independently confirmed
  twice. (`introduction.md:14` words it correctly.)
- **Fix:** "Read server settings" / "Settings | Read server configuration", plus one sentence on
  the edit-file-then-`POST /reload` path.

## Medium

### Password protection (`docs/features/password-protection/`)

- **M1. `index.md` "How It Works" step 3 + `players/commands.md:24` — no "last position" restore.**
  After `!login`, *every* player warps to their cabin entry — the code comments say "Always warp to
  cabin entry" (`PasswordProtectionService.cs:875-906`). `joining.md` already says it right. Fix
  both pages to "you'll warp to your cabin".
- **M2. `commands.md` — `!authstatus` example output is fabricated** **[DECISION D9]**: real output
  is header `=== Player Authentication Status ===` with `[OK] Name (userName)` / `[PENDING] …`
  lines; no countdown exists (`AuthStatusCommand.cs:59-73`). Replace the example (or implement the
  countdown per D9).
- **M3. `lobby-layouts.md` intro — the default layout is pre-decorated, not a plain cabin.**
  `LobbyService.cs:100-113,1544-1570` imports a decorated default; empty cabin is only the
  import-failure fallback.
- **M4. `lobby-layouts.md` "Exporting" — export string goes to SMAPI logs only.** Chat gets just a
  summary ("Check the server logs for the export string", `LobbyCommands.cs:627-635`). Say where to
  find it. Same wording bug in `commands.md:61` ("also prints").
- **M5. `security.md` "Layout Not Applying" — wrong for Shared mode (the default).** `!lobby set`
  re-applies immediately to the shared lobby (`LobbyService.cs:2244-2262,2478-2483`); only
  Individual-mode pre-existing lobbies keep the old layout (`:1948-1966`). Split the answer by mode.
- **M6. `lobby-layouts.md` Edit Mode table — inventory swap undocumented.** Entering edit mode backs
  up and **clears the admin's inventory** (restored on save/cancel/disconnect, crash-recovered;
  `LobbyService.cs:1303-1374,2308,2359,2462,2788,723-777`). Admins will panic without this row.

### Invite codes & joining

- **M7. `cross-platform.md:25-32` — the two-code model is misdescribed.** Every documented retrieval
  path (`info`, `!invitecode`) prints only the S-prefixed code (`ServerCommand.cs:43-139`,
  `InviteCodeCommand.cs:34-42`); the G form exists only in `GET /status → gogInviteCode`
  (`ApiService.cs:2562-2572`) and the masked startup banner. AND the "GOG player can't use the S
  code" dead-end is wrong: vanilla parsers accept both prefixes (decompiled
  `GalaxyNetHelper.cs:197-201`, `SteamNetHelper.cs:264-271` — prefix only sets a Steam client's
  transport preference). Rewrite: one base code, two prefixes; show both retrieval paths.
- **M8. `players/joining.md:48` — Direct IP presented as generally available; it's off by default.**
  `AllowIpConnections = false` (`ServerSettings.cs:52`; `IpConnectionService.cs:34,47` logs
  "Players must use invite codes"). Add "only if the admin enabled IP connections (off by default)".

### Other features pages

- **M9. `server-mechanics.md:52-58` (+ `features/index.md` blurb) — chest protection over-claimed.**
  Locks apply only while the owning farmhand is **offline**; all locks release on owner connect
  (`AlwaysOn.cs:1109-1246`). Reword both copies.
- **M10. `server-mechanics.md` — two undocumented automations with gameplay impact.** Daily at 6:30:
  CC auto-completion when all bundles done (`AlwaysOn.cs:958-986`), and with `BuyJoja=true`
  auto-PURCHASES of Joja membership/upgrades (5,000–40,000g from the — by default shared — wallet,
  `AlwaysOn.cs:993-1047`). Document both, link the BuyJoja setting.
- **M11. `cabin-strategies.md:62-65` — `!cabin` caveats missing.** Refused entirely under
  FarmhouseStack; requires standing on the Farm; placement can be rejected; clears basic debris
  (`CabinCommand.cs:25-43,63-76`). Same caveat rider for `players/playing.md` ("Your Cabin").
- **M12. `cabin-strategies.md` — StartingCabins semantics undocumented.** Stacked strategies ignore
  it (min 1 hidden, auto-grow per join); None places that many visible cabins at map positions
  (`GameCreatorService.cs:106-110,180-187`; `CabinManagerService.cs:294-298,640-693`). Add a short
  subsection or cross-link server-settings.md.

### Players & community

- **M13. `players/commands.md` — `!event` missing** (companion to plan 01 M3 / DECISION D6): any
  player at a festival can run it; the server itself advertises it. Add the row.
- **M14. `players/troubleshooting.md` — game-version mismatch missing as a connect-failure cause.**
  Vanilla rejects lobbies with a different protocol version (decompiled `CoopMenu.cs:601-613`).
  Add a row.
- **M15. `community/getting-help.md:24` — Discussions link 404s** **[DECISION D8]**:
  `has_discussions:false` on the repo (checked 2026-06-13); `feature_request.yml` exists. Reroute
  or enable Discussions.
- **M16. `community/resources.md:12` — dead repo link.** `truman-world/puppy-stardew-server` 404s;
  GitHub search shows `AmigaMeow/puppy-stardew-server` as the surviving repo. Fix or drop.

## Low (riders)

- `password-protection/security.md`: "all game messages blocked" → whitelist reality (farmerDelta,
  playerIntroduction, disconnecting, limited chat pass); add the constant-time-comparison bullet
  (real differentiator); add the missing intro sentence to "Why a Lobby?".
- `password-protection/commands.md`: active layout can't be deleted; layouts being edited can't be
  deleted/renamed — note both restrictions.
- `password-protection/index.md`: note step-3 snippet IS the default (Shared/default — step
  optional); auth-timeout clock starts after character creation, not at connection.
- `players/joining.md`: invite-code examples imply different lengths — both are the same Base36
  payload, only the prefix differs (a 16-char example would exceed the 56-bit lobby-ID limit); the
  ~99%/~50% reliability figures appear twice on one short page (keep once, and consider softening —
  they're uncited; same figures in `cross-platform.md:9-23`); add "log in promptly — unauthenticated
  players are kicked after ~2 minutes (reconnect is fine)".
- `players/commands.md`: fold the dangling debris sentence into the `!cabin` row.
- `players/troubleshooting.md`: quote the real failure message "Wrong password. N tries left."
- `features/discord.md`: example code "S-ABC123" has a hyphen real codes never have; soften
  "copy directly from the bot's status" (Discord statuses aren't click-to-copy).
- `features/cross-platform.md`: example codes should differ only in prefix (e.g. `S1ABC23`/`G1ABC23`).
- `features/cabin-strategies.md`: the existing-cabin sweep excludes player-placed (`!cabin`) cabins;
  KeepExisting is the default — say both; move the farmhouse-reservation note so it covers both
  stacked strategies.
- `features/server-mechanics.md`: "Fishing rod (Day 2)" bullet → host cutscene pre-skip (no rod is
  granted; not day-tied); "Pet naming" → pet creation AND naming from settings; pause rules: no
  pause on festival days.
- `features/rest-api.md`: WebSocket example silently fails on secured deployments — add the
  one-line auth note + `ws.onopen` auth send, pointing at `introduction.md`'s auth sections (full
  contract already documented there).
