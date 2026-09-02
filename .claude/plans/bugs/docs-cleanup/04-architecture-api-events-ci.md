# 04 — Architecture, API Reference, Events Schema & CI Docs

**Status:** ready-to-implement
**Priority:** 3 (high)
**GitHub Issue(s):** none
**Area:** docs
**Related:** [`README.md`](README.md)
**Observed:** docs audit 2026-06-13, re-verified in a second pass over the seven developer pages
**Next step:** decide D4/D5 (doc-side vs generator change), then open a PR for H1–H3

**Objective:** Catch the architecture pages up to the multi-account/multi-host reality, make the
API reference match the dispatcher, and sync the CI docs with the current workflow set.

**Scope:** `docs/developers/architecture/**`, `docs/developers/advanced/decompiling.md` +
`client-manipulation-techniques.md`, `docs/developers/events-schema.md`, `docs/developers/api/**`,
`docs/developers/contributing/**`, `docs/assets/openapi.json` (+ generator, per decision D5).
Companion touchpoint: `CONTRIBUTING.md` (repo root) dead anchor.

**Verification gate:** `make docs` builds clean (regenerates/needs `openapi.json`); grep zero doc
hits for `sdvd-steam-auth-shared-`, `session-{username}.json` (old token path); every endpoint
listed in `introduction.md`'s public list matches `ApiService.cs` exactly.

---

## High

### H1. `api/introduction.md` — public (no-auth) endpoint list incomplete (security-relevant)
- **Problem:** doc lists `/health`, `/docs`, `/swagger/v1/swagger.json`; code also exempts
  `/wait/health`, `/stats`, and `/diagnostics/state` (`ApiService.cs`; deliberate, per
  the comment above the exemption list). `/diagnostics/state` returns farmhand/cabin/owner/world state
  unauthenticated (`HandleGetDiagnosticsStateAsync`) — operators sizing their exposure need to know. Confirmed twice
  (independent re-verification after the first verifier crashed).
- **Fix:** add the three rows + a note on what `/diagnostics/state` exposes and why it's open
  (test-harness diagnosis), so operators can firewall accordingly. Companion fix in
  `environment.md` (plan 01 M1).

### H2. `api/introduction.md` — "returns a 400" is false, twice  **[DECISION D4]**
- **Problem:** the `POST /roles/admin` and `DELETE /farmhands` entries claim 400 on
  invalid/missing params. Handlers return `Success=false` bodies
  through `WriteJsonAsync`, which never sets StatusCode → HTTP 200. **No 400 exists
  anywhere in ApiService.cs** (full grep; non-200s are 401/404/405/408/409/500/503/504).
  `response.ok`-style automation treats validation failures as success.
- **Fix (doc-side default):** document "200 with `success:false` + `error` field; check the body".
  Code-side alternative per D4.

### H3. `architecture/steam-auth.md` — documents the pre-multi-account sidecar
Four related fixes (do as one rewrite pass; verify against `tools/steam-service/Program.cs`):
1. **API Endpoints section lists 2 of 8.** Actual: `/health`, `/steam/ready`,
   `/steam/app-ticket`, `/steam/refresh-token`, `POST /steam/lobby/create`,
   `POST /steam/lobby/set-data`, `POST /steam/lobby/set-privacy`,
   `GET /steam/lobby/status`  — the lobby endpoints power the invite-code flow the page
   itself describes. Also undocumented: the `?account=N` query param. Update the
   architecture diagram box too.
2. **`/health` example shows the old shape.** Now includes the per-account `accounts` array;
   top-level `logged_in` is the AND across checked accounts; `/health` never triggers
   logins. The harness preflight depends on the array (`SharedSteamAuth.cs`).
3. **Env table omits STEAM_ACCOUNTS** — the JSON multi-account mechanism tried FIRST;
   USERNAME/PASSWORD/REFRESH_TOKEN are the account-0 fallback (`Program.cs`, `DiscoverAccounts`).
   Note how it actually reaches the container (not via compose;
   `make setup --env-from-file .env.test`, `Makefile`).
4. **Token path is the old flat format.** `session-{username}.json` is the *migration* path; current
   is `{SESSION_DIR}/{username}/session.json` (`SteamAuthService.cs`).

## Medium

### `architecture/` + `advanced/`
- **M1. `steam-auth.md` STEAM_AUTH_URL "default" is compose's value, not a code default.** Unset →
  ticket fetch disabled (`AuthService.cs`); compose sets
  `http://steam-auth:${STEAM_AUTH_PORT:-3001}` (`docker-compose.yml`). Reword the cell.
- **M2. `steam-auth.md` + `networking.md` — invite-code story is G-only.** The mod derives both
  codes (S via `SteamInvitePrefix + baseCode`, `ApiService.cs`, `ServerBanner.cs`);
  `features/cross-platform.md` documents both. Add the Steam-lobby step; cross-link. (Pairs with
  plan 02 M7 — same base-code model, fix consistently.)
- **M3. `networking.md` "Key Implementation Files" mixes mod files with vanilla classes.**
  `SteamGameServerService.cs`/`SteamGameServerNetServer.cs` are in
  `mod/JunimoServer/Services/SteamGameServer/`; `GalaxyNetServer.cs`/`LidgrenServer` exist only
  under `decompiled/`. Add a column/prefix distinguishing them.
- **M4. `client-manipulation-techniques.md` message-type table:** 8 invented names
  (3→`locationIntroduction`, 4→`forceEvent`, 7→`locationSprites`, 8→`characterWarp`,
  12→`worldDelta`, 19→`disconnecting`, 27→`digBuriedNut`, 28→`requestPassout` — vs decompiled
  `Multiplayer.cs`; all numbers are correct); type 3 direction is backwards (server→client,
  `GameServer.cs`, client `case 3:` at `Multiplayer.cs`). Use the real constant
  names so rows are grep-able.
- **M5. `events-schema.md` — container naming/id model is doubly stale.** Names are
  `sdvd-steam-auth-{hostId}-{runId}` (NOT `…-shared-{runId}`), and the `{runId}` in names/labels is
  a fresh per-container 8-char GUID (`SharedSteamAuth.cs`; `ServerContainer.cs`;
  `GameClientContainer.cs`) — `RunMetadata.RunId` keys the run directory + flakiness only
  (`RunMetadata.cs`; `FlakinessTracker.cs`). `forwardedVia: steam-auth-shared` stays
  correct (`SharedSteamAuth.cs`) — don't "fix" it.
- **M6. `events-schema.md` — flakiness.jsonl is at `TestResults/` root, not repo root**
  (`FlakinessTracker.cs`). (Same family as plan 03 M2.)
- **M7. `events-schema.md` — phase list wrong.** Only `connect`, `artifacts`, `cleanup` are ever
  pushed (`ConnectionRetryHelper.cs`; `TestLifecycle.cs`); no `PushPhase("setup")`
  exists; "checkpoint labels" are screenshot names, never phases. (Stale example in
  `TestIdentityContext.cs` — companion comment fix.)
- **M8. `events-schema.md` — requestId stitching is gated by SDVD_TEST_TRACING, default
  OFF** (`TracingHandler.cs`; `TestTracingLevel.cs` — unset → None → no header). One
  sentence saves a debugging session.

### `api/` + spec  **[DECISION D5 throughout]**
- **M9. `openapi.json`: 0 of 20 operations carry `parameters`/`requestBody`** (jq-verified; root
  cause `OpenApiGenerator.cs` builds only Summary/Description/OperationId/Tags/Responses).
  Generated per-endpoint pages can't show `fps`/`value`/`multiplier`/`name`/`playerId` or the
  `POST /newgame` body. Doc-side mitigation: complete `introduction.md`'s hand-written params
  section (M10). Code-side: extend the generator — mind that `OpenApiGenerator.Generate` is
  reflection-invoked with fixed positional args at Docker build time (see its `<remarks>`;
  optional params break the Docker build while `dotnet build` stays green).
- **M10. `introduction.md` "POST Endpoint Parameters" covers 4 of 8 write ops.** Missing:
  `POST /clock-speed` (`?multiplier=`, double > 0), `POST /auth/timeout` (`?value=`),
  `POST /newgame` (JSON body `NewGameRequest` — also contradicts the section's
  "via query string" claim), `POST /reload` (no params; fails while clients connected).
- **M11. `introduction.md` Configuration table:** "API key … for write endpoints" is wrong — ALL
  non-public endpoints need it, including GETs (the doc's own auth section says so);
  "(empty = no auth)" omits that the shipped entrypoint refuses to start that way unless
  ALLOW_INSECURE_SETUP=true (`startapp.sh`).
- **M12. Spec lacks `securitySchemes`/`security` entirely** (parse-verified) — rendered reference
  gives no auth affordance. Generator change per D5; mirror the public-endpoint split.
- **M13. Five reachable GETs absent from spec and all prose:** `/wait/status`, `/wait/players`,
  `/wait/health`, `/wait/farmhands` (`?since=N` long-poll), `/diagnostics/handler-timing`
  (served from the dispatcher switch only; no `[ApiEndpoint]` attributes, so the generator never sees them —
  inconsistent with `/diagnostics/state`, which IS attributed). Either attribute them
  (matching the /diagnostics/state precedent) or document the exclusion policy in
  introduction.md.

### `contributing/`
- **M14. `ci-cd.md` — stale required-check list.** Resolved: the CodeQL note no longer enumerates
  the required checks — it points at the Validate PR section as the single source of truth — so the
  drift against the full check set is gone.
- **M15. `ci-cd.md` E2E section — nightly schedule + `pr` dispatch input missing.** Four entry
  points now (`e2e-tests.yml`); "manual and maintainer-gated" needs the nightly
  exception. (Same fact as plan 03 M3 — fix both pages consistently.)
- **M16. `contributing/index.md` — commit-type list shows 6 of 11.** commitlint accepts
  perf, revert, style, build, ci as well (`commitlint.config.js`); repo history uses them.
- **M17. `contributing/index.md` — contributor + maintainer flows don't describe the merge process.**
  ci-cd.md documents strict "up to date before merge" plus auto-merge, but index.md never says
  "enable auto-merge", and the maintainer section describes classic branch protection while the repo
  uses a ruleset (`lefthook.yml`). Add the auto-merge step + ruleset setup (strict required
  status checks, `!approve` self-approval).
- **M18. `ci-cd.md` — DEPLOY_API_KEY marked optional but the workflow aborts without it**
  **[DECISION D7]** (`deploy-server.yml`).

## Low (riders)

- `steam-auth.md`: add the `healthcheck` command row (`Program.cs`; used by the image
  HEALTHCHECK + harness wait); note `export-token` emits one JSON object **per saved session** and
  Logger lines can interleave — `> token.json` isn't guaranteed single-document under
  multi-account (`Program.cs`).
- `networking.md`: netdebug section lists 3 of 5 subcommands — add `gog-ports`/`gog-requests` or
  link the (complete) admin page (`tools/netdebug/Program.cs`).
- `mod-architecture.md`: "WebSocket for real-time updates" → "real-time chat relay (used by the
  Discord bot)" — chat broadcast is the only push (`ApiService.cs`); auth/pong are replies.
  (The auto-discovery claim is TRUE — reflection over `IModService`, `ModEntry.cs` —
  keep it.)
- `client-manipulation-techniques.md`: table silently omits types 5/9/11/16 (add or note the
  omission; type 11 matters to this project); "the server dispatches… `Multiplayer.
  processIncomingMessage()`" → that's the *client* dispatcher (server-side is
  `GameServer.processIncomingMessage`, `GameServer.cs`).
- `decompiling.md`: note the script requires `GAME_PATH` in `.env` and the `ilspycmd` dotnet tool
  (`tools/decompile-sdv.sh`).
- `events-schema.md`: tickMs emitters → "(`ModEventLog`, `ClientEventLog`)"
  (`ClientEventLog.cs`).
- `ci-cd.md`: add Label PR row + a one-line "reusable workflows" note for
  `build-image.yml`/`build-docs.yml`; fix the second stale check-enumeration in the CodeQL
  "Advisory, Not Required" paragraph.
- `contributing/index.md`: add a "Working on the documentation" subsection (`make docs` flow;
  bun-only alternative for prose edits) — currently zero published-docs entry point for docs
  contributors (pairs with plan 03 H5).
- `CONTRIBUTING.md` (repo root, companion touchpoint): anchor
  `…/community/contributing#ci-cd-pipeline` targets a heading that doesn't exist in the 11-line
  stub — link `…/developers/contributing/ci-cd` directly.
- `mod/JunimoServer/Env.cs` (companion code-docstring touchpoint): the API_KEY XML doc claims
  "write operations (POST, DELETE) require the X-API-Key header" — actual contract is
  `Authorization: Bearer` on ALL non-public endpoints (`ApiService.cs`). This stale
  docstring is the likely origin of the M11 drift — fix it in the same pass.
