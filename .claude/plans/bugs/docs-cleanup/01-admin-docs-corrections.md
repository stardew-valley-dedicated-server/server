# 01 — Admin Docs Corrections (operator-breaking first)

**Status:** ready-to-implement
**Priority:** 4 (critical)
**GitHub Issue(s):** none
**Area:** docs
**Related:** [`README.md`](README.md)
**Observed:** docs audit 2026-06-13; C1–C3 re-confirmed by hand against compose, `startapp.sh`, and the doc text
**Next step:** open a PR fixing C1–C3 first; resolve D1–D3 with the maintainer before touching H1/H2

**Objective:** Fix every statement in the operator-facing docs that, if followed, breaks the server,
loses data, or sends the operator chasing signals that don't exist.

**Scope:** `docs/admins/**`, `docs/features/backup.md` (operator content), companion touchpoints
`.env.example` and the D1–D3 decision-register items (see README).

**Verification gate:** after edits, `make docs` builds clean; `grep -rn "Ready for players" docs/`
returns nothing; every command quoted in the changed pages was run-or-traced against the code path
cited below.

---

## Critical

### C1. `environment.md` "Changing Ports" — the API_PORT example breaks the API  **[DECISION D1]**
- **File:** `docs/admins/configuration/environment.md` (~lines 162-171: "VNC_PORT=5801 / API_PORT=8081 … only the host mapping changes")
- **Problem:** `docker-compose.yml` maps `${API_PORT:-8080}:8080` (fixed container port), but
  `docker-compose.yml` ALSO forwards `API_PORT` into the container, and the mod listens on it
  (`mod/JunimoServer/Env.cs`, `Services/Api/ApiService.cs`). Setting `API_PORT=8081` makes
  the mod listen on 8081 in-container while the host maps 8081→8080 → no listener; the Discord bot
  (`API_URL: http://server:8080` in compose) breaks too.
- **Fix (doc-side default):** remove `API_PORT` from the example; state that `API_PORT` currently
  changes the port both inside and outside the container and is **not** safely remappable until the
  compose file is fixed. If D1 is resolved code-side instead (stop forwarding API_PORT into the
  container), the existing prose becomes true — coordinate with the decision.

### C2. `upgrading.md` — "server automatically downloads the latest game files on startup" is false
- **File:** `docs/admins/operations/upgrading.md` (after the `docker volume rm server_game-data` step)
- **Problem:** after removing the volume the server blocks in a poll loop printing a "run
  `make setup`" banner (`docker/rootfs/startapp.sh`); the steam-auth container's default
  command is `serve` (`tools/steam-service/Dockerfile`), which never downloads
  (`Program.cs`; `DownloadAllAsync` is reached only from `setup`/`download`,
  `Program.cs`). Only SMAPI is auto-downloaded (`startapp.sh`). An operator
  following the documented procedure ends up stuck on "Waiting for game files to appear...".
- **Fix:** replace that sentence with: after removing the volume, run `make setup` (or
  `docker compose run --rm -it steam-auth setup`; non-interactive when a saved session exists),
  then `docker compose up -d`. Also fix the same assumption in the Troubleshooting bullet
  "Try removing and recreating the game volume" — add "then re-run `make setup`".

### C3. `backup.md` — save-import command puts the save where the game never looks
- **File:** `docs/features/backup.md` (`… ubuntu cp -r /backup/YourFarm_123456789 /saves/`)
- **Problem:** the `saves` volume root is the StardewValley *config* dir, not the Saves dir
  (`docker-compose.yml` mounts `saves:/config/xdg/config/StardewValley`). The command lands the
  save at `…/StardewValley/YourFarm_123456789` instead of `…/StardewValley/Saves/…`; the game never
  sees it. The page's own SMAPI-restore section  correctly uses the `Saves/` subdir.
- **Fix:** change the target to `/saves/Saves/` and call out the `Saves/` subfolder explicitly.

## High

### H1. `environment.md` — four documented vars never reach the container  **[DECISION D2]**
- **Problem:** `VERBOSE_LOGGING`, `HEALTH_CHECK_SECONDS`, `ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS`,
  `FORCE_NEW_DEBUG_GAME` have code consumers (`Env.cs`; `ModEntry.cs`;
  `GameManagerService.cs`; `ServerOptimizer.cs`) but compose has no `env_file` and its
  `environment:` block (`docker-compose.yml`) doesn't list them — setting them in `.env` does
  nothing.
- **Fix:** per D2 — either annotate each row ("requires adding to docker-compose.yml") or add the
  keys to compose in the same PR. Don't leave the rows implying `.env` alone works.

### H2. `environment.md` + `.env.example` — ENABLE_MOD_INCOMPATIBLE_OPTIMIZATIONS default is wrong  **[DECISION D3]**
- **Problem:** documented default `true` (`environment.md`, `.env.example`); code default
  `false` (`Env.cs`), and compose never sets it, so every deployment runs `false`.
- **Fix:** change both surfaces to `false` (or flip the code default per D3).

### H3. `first-setup.md` — log line "Ready for players" doesn't exist
- **Problem:** project-wide grep finds the string only in this doc. The real startup-complete line
  is `"Services loaded and ready!"` (`ModEntry.cs`); `info` prints `Status: Ready`
  (`ServerCommand.cs`).
- **Fix:** point at `"Services loaded and ready!"`, or better, a verifiable signal:
  `docker compose exec server curl -fsS http://localhost:8080/health` or `info` → `Status: Ready`.

### H4. `vnc.md` + `troubleshooting.md` — `docker compose restart` does not apply `.env` changes
- **Files:** `docs/admins/operations/vnc.md` (enable-rendering steps), `docs/admins/troubleshooting.md` (black-screen step 2)
- **Problem:** compose interpolates `.env` at container *creation* (`docker-compose.yml`);
  `restart` reuses the frozen environment, so `SERVER_FPS=10` + restart leaves the "Rendering
  Disabled" notice — the exact symptom the sections claim to fix. (Runtime alternative `rendering
  10` via CLI is correct — keep it.)
- **Fix:** both pages: `docker compose up -d` (recreates the container), not `restart`.

### H5. `server-settings.md` — quoted `"null"` for RandomSeed can destroy operator settings
- **Problem:** the table quotes booleans and null as JSON strings. For `RandomSeed` (`ulong?`,
  `ServerSettings.cs`) the literal string `"null"` makes Newtonsoft throw; the loader's failure
  path falls through to `SaveToFile(defaults)` — **overwriting the operator's settings file with
  defaults on disk** (`ServerSettingsLoader.cs`). Quoted bools merely coerce (lesser issue).
- **Fix:** unquote `false` for RemixBundles/RemixMines; document RandomSeed as unquoted `null` or a
  number; add a warning that a malformed settings file silently reverts everything to defaults.

### H6. `environment.md` — SERVER_TPS missing entirely
- **Problem:** the primary CPU knob is absent from the reference page: compose forwards it
  (`docker-compose.yml`), `Env.cs` consumes it (default 60, floor 1), `.env.example`
  features it. The page documents SERVER_FPS but not its sibling.
- **Fix:** add a SERVER_TPS row (default 60, min 1) + a short Variable Details subsection mirroring
  the `.env.example` guidance.

### H7. `commands.md` — "Owner Commands" section describes a permission level that doesn't exist
- **File:** `docs/admins/operations/commands.md`
- **Problem:** no owner/first-player role exists (`RoleService.cs` — enum is Admin |
  Unassigned; `RoleService.cs`: the host is the server process, never a human). `!joja`
  gates on `IsPlayerAdmin` (`JojaCommand.cs`). As written, the requirement is impossible to
  satisfy on a dedicated server.
- **Fix:** delete the Owner Commands section; move `!joja` into Admin Commands (keep the
  irreversibility warning).

## Medium

- **M1. `environment.md` — API_KEY exemption list incomplete.** Actual public endpoints:
  `/health`, `/wait/health`, `/stats`, `/docs`, `/swagger/v1/swagger.json`, `/diagnostics/state`
  (`ApiService.cs`); `/ws` upgrade is also accepted pre-auth (authenticates in-protocol).
  `/stats` and `/diagnostics/state` (farmhand/cabin/owner state) being key-exempt is
  security-relevant next to the "exposing port 8080" warning. Fix: list the full set + the WS
  handshake note. (Same fix needed in `api/introduction.md` — see plan 04.)
- **M2. `first-setup.md` + `troubleshooting.md` — health-check examples use `wget`,
  image only has `curl`.** Verified empirically against the built image (`command -v wget` → none;
  Dockerfile installs curl, its own HEALTHCHECK uses curl, `docker/Dockerfile`).
  Fix both to `docker compose exec server curl -fsS http://steam-auth:3001/health`.
- **M3. `commands.md` — `!event` is not admin-gated** **[DECISION D6]**: handler checks only
  festival context (`AlwaysOnFestivals.cs`), and the mod broadcasts "Type !event to start
  now" to all players. Move to General Commands ("only while at a festival"). Companion
  player-doc addition in plan 02.
- **M4. `commands.md` — Quick Reference `curl …/status` omits auth.** `/status` is not
  public; with API_KEY set (which startup effectively forces, `startapp.sh`) it 401s. Fix:
  `curl -s -H "Authorization: Bearer $API_KEY" http://localhost:8080/status | jq`.
- **M5. `troubleshooting.md` — circular advice for creating the first admin.** `!admin`
  itself requires admin (`RoleCommands.cs`); bootstrap is `AdminSteamIds` in
  server-settings.json with auto-promote on join (`RoleService.cs`, `ServerSettings.cs`).
  Rewrite accordingly, link server-settings.md.
- **M6. `troubleshooting.md` — missing the most likely first-run failure.** The security
  preflight aborts ("Refusing to start with insecure configuration", `startapp.sh`) when
  VNC_PASSWORD is empty or API_KEY empty with API enabled. Add a cause entry + link to
  ALLOW_INSECURE_SETUP docs.
- **M7. `backup.md` — SMAPI backup cadence is per-launch, not daily.** SaveBackup runs only in its
  `Entry` (once per game launch); a 24/7 server gets no new backups between restarts. Also
  retention: only the 10 newest zips are kept. Fix frequency wording + add the retention note +
  warn long-running servers to schedule manual backups.
- **M8. `server-settings.md` creation-vs-runtime split is wrong for five settings.**
  MushroomCave/BuyJoja/PetName are re-read every startup and applied when the in-game event fires
  (`ModEntry.cs`, `AlwaysOn.cs`); PetBreed is split (the `-1` no-pet sentinel
  is runtime, the breed number is creation-only); SeparateWallets is creation-only +
  `!changewallet` (`GameCreatorService.cs`, `PersistentOptions.cs`). Annotate the rows /
  move SeparateWallets to the creation table.
- **M9. `environment.md` — STEAM_KEEP_LANGUAGES and IMAGE_VERSION missing from the reference.**
  Both documented elsewhere (`steam-auth.md`'s language-filtering section; `upgrading.md`) — add rows that **cross-link**
  those sections; do not duplicate prose (see README theme 3).

## Low (riders)

- `environment.md`: AUTH_TIMEOUT_SECONDS row lacks "0 = disabled" (code: `Env.cs`); add
  STEAM_AUTH_PORT row (internal-only, default 3001).
- `.env.example` — "minimum: 10" for SERVER_TPS is wrong; code floor is 1 (`Env.cs`). Keep the
  "20-30 suits production" prose (deliberately conservative; see `.claude/rules/server-tps-headless.md`).
- `.env.example` — STEAM_ACCOUNTS presented as a `.env` key but nothing reads it from `.env`
  (compose doesn't forward it; harness reads `.env.test`). Reduce to a pointer comment.
- `prerequisites.md` — "only to download game files" understates: the sidecar keeps a live Steam
  session for lobby tickets (that's *why* a dedicated account is recommended). Reword.
- `server-settings.md` — Monster Spawning "auto" is incomplete for mod farms: it resolves
  to Wilderness OR the selected mod farm's `SpawnMonstersByDefault` flag (matches the vanilla
  new-game UI). Reword since the page documents mod-farm selection in detail.
- `operations/index.md` — VNC is not "disabled by default"; the web server always runs, it's
  *rendering* that defaults off (SERVER_FPS=0). Reword.
- `upgrading.md` — version output is in `docker compose logs` / CLI, not visible over VNC.
- `networking.md` — drop the invented `[Steam]` log-line prefix (mod logs unprefixed;
  troubleshooting.md already quotes them correctly).
- `commands.md`: add `settings verbose [on|off]` row (`SettingsCommand.cs`); document
  F10 as the host-visibility hotkey alongside F9 (`AlwaysOnConfig.cs`); `!unban` takes
  `<id|username>` not a farmer name (`!listbans` shows IDs); fix `!cabin` description ("right of
  player, must be on farm, clears basic debris" — `CabinCommand.cs`; the players-doc
  copy is already correct); note SMAPI console is reachable via `attach-cli`, not VNC; collapse the
  three `cli exit/quit/detach` alias rows; align `info`/`!info` output summaries.
