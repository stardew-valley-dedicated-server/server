# 04 — Architecture, API Reference, Events Schema & CI Docs

**Objective:** Catch the architecture pages up to the multi-account/multi-host reality, make the
API reference match the dispatcher, and sync the CI docs with the current workflow set.

**Scope:** `docs/developers/architecture/**`, `docs/developers/advanced/decompiling.md` +
`client-manipulation-techniques.md`, `docs/developers/events-schema.md`, `docs/developers/api/**`,
`docs/developers/contributing/**`, `docs/assets/openapi.json` (+ generator, per decision D5).
Companion touchpoint: `CONTRIBUTING.md` (repo root) dead anchor.

**Verification gate:** `make docs` builds clean (regenerates/needs `openapi.json`); grep zero doc
hits for `sdvd-steam-auth-shared-`, `session-{username}.json` (old token path); every endpoint
listed in `introduction.md`'s public list matches `ApiService.cs:1918-1924` exactly.

---

## High

### H1. `api/introduction.md` — public (no-auth) endpoint list incomplete (security-relevant)
- **Problem:** doc lists `/health`, `/docs`, `/swagger/v1/swagger.json`; code also exempts
  `/wait/health`, `/stats`, and `/diagnostics/state` (`ApiService.cs:1918-1924`; deliberate, per
  comment :1912-1917). `/diagnostics/state` returns farmhand/cabin/owner/world state
  unauthenticated (`:148-211`) — operators sizing their exposure need to know. Confirmed twice
  (independent re-verification after the first verifier crashed).
- **Fix:** add the three rows + a note on what `/diagnostics/state` exposes and why it's open
  (test-harness diagnosis), so operators can firewall accordingly. Companion fix in
  `environment.md:129` (plan 01 M1).

### H2. `api/introduction.md` — "returns a 400" is false, twice  **[DECISION D4]**
- **Problem:** `:204` (POST /roles/admin) and `:218` (DELETE /farmhands) claim 400 on
  invalid/missing params. Handlers return `Success=false` bodies (`:4086-4111`, `:4170-4195`)
  through `WriteJsonAsync` (`:4675-4679`), which never sets StatusCode → HTTP 200. **No 400 exists
  anywhere in ApiService.cs** (full grep; non-200s are 401/404/405/408/409/500/503/504).
  `response.ok`-style automation treats validation failures as success.
- **Fix (doc-side default):** document "200 with `success:false` + `error` field; check the body".
  Code-side alternative per D4.

### H3. `architecture/steam-auth.md` — documents the pre-multi-account sidecar
Four related fixes (do as one rewrite pass; verify against `tools/steam-service/Program.cs`):
1. **API Endpoints section lists 2 of 8.** Actual: `/health` (:473), `/steam/ready` (:508),
   `/steam/app-ticket` (:538), `/steam/refresh-token` (:566), `POST /steam/lobby/create` (:602),
   `POST /steam/lobby/set-data` (:635), `POST /steam/lobby/set-privacy` (:665),
   `GET /steam/lobby/status` (:703) — the lobby endpoints power the invite-code flow the page
   itself describes. Also undocumented: the `?account=N` query param (:25-27, 443-452). Update the
   architecture diagram box too.
2. **`/health` example shows the old shape.** Now includes the per-account `accounts` array
   (:485-504); top-level `logged_in` is the AND across checked accounts; `/health` never triggers
   logins (:469-472). The harness preflight depends on the array (`SharedSteamAuth.cs:571-584`).
3. **Env table omits STEAM_ACCOUNTS** — the JSON multi-account mechanism tried FIRST;
   USERNAME/PASSWORD/REFRESH_TOKEN are the account-0 fallback (`Program.cs:16-27`, DiscoverAccounts
   :72-132). Note how it actually reaches the container (not via compose;
   `make setup --env-from-file .env.test`, `Makefile:107`).
4. **Token path is the old flat format.** `session-{username}.json` is the *migration* path; current
   is `{SESSION_DIR}/{username}/session.json` (`SteamAuthService.cs:418-445`).

## Medium

### `architecture/` + `advanced/`
- **M1. `steam-auth.md` STEAM_AUTH_URL "default" is compose's value, not a code default.** Unset →
  ticket fetch disabled (`AuthService.cs:114-131`); compose sets
  `http://steam-auth:${STEAM_AUTH_PORT:-3001}` (`docker-compose.yml:23`). Reword the cell.
- **M2. `steam-auth.md` + `networking.md` — invite-code story is G-only.** The mod derives both
  codes (S via `SteamInvitePrefix + baseCode`, `ApiService.cs:2568-2571`, `ServerBanner.cs:73-76`);
  `features/cross-platform.md` documents both. Add the Steam-lobby step; cross-link. (Pairs with
  plan 02 M7 — same base-code model, fix consistently.)
- **M3. `networking.md` "Key Implementation Files" mixes mod files with vanilla classes.**
  `SteamGameServerService.cs`/`SteamGameServerNetServer.cs` are in
  `mod/JunimoServer/Services/SteamGameServer/`; `GalaxyNetServer.cs`/`LidgrenServer` exist only
  under `decompiled/`. Add a column/prefix distinguishing them.
- **M4. `client-manipulation-techniques.md` message-type table:** 8 invented names
  (3→`locationIntroduction`, 4→`forceEvent`, 7→`locationSprites`, 8→`characterWarp`,
  12→`worldDelta`, 19→`disconnecting`, 27→`digBuriedNut`, 28→`requestPassout` — vs decompiled
  `Multiplayer.cs:46-115`; all numbers are correct); type 3 direction is backwards (server→client,
  `GameServer.cs:677-680`, client `case 3:` at `Multiplayer.cs:1577-1579`). Use the real constant
  names so rows are grep-able.
- **M5. `events-schema.md:55` — container naming/id model is doubly stale.** Names are
  `sdvd-steam-auth-{hostId}-{runId}` (NOT `…-shared-{runId}`), and the `{runId}` in names/labels is
  a fresh per-container 8-char GUID (`SharedSteamAuth.cs:113,119,141`; `ServerContainer.cs:220,297`;
  `GameClientContainer.cs:171,219`) — `RunMetadata.RunId` keys the run directory + flakiness only
  (`RunMetadata.cs:41-43,104`; `FlakinessTracker.cs:36`). `forwardedVia: steam-auth-shared` stays
  correct (`SharedSteamAuth.cs:89`) — don't "fix" it.
- **M6. `events-schema.md:53` — flakiness.jsonl is at `TestResults/` root, not repo root**
  (`FlakinessTracker.cs:9-14`). (Same family as plan 03 M2.)
- **M7. `events-schema.md:30` — phase list wrong.** Only `connect`, `artifacts`, `cleanup` are ever
  pushed (`ConnectionRetryHelper.cs:35,78`; `TestLifecycle.cs:177,261`); no `PushPhase("setup")`
  exists; "checkpoint labels" are screenshot names, never phases. (Stale example in
  `TestIdentityContext.cs:51` — companion comment fix.)
- **M8. `events-schema.md:122-126` — requestId stitching is gated by SDVD_TEST_TRACING, default
  OFF** (`TracingHandler.cs:53-76`; `TestTracingLevel.cs:59-72` — unset → None → no header). One
  sentence saves a debugging session.

### `api/` + spec  **[DECISION D5 throughout]**
- **M9. `openapi.json`: 0 of 20 operations carry `parameters`/`requestBody`** (jq-verified; root
  cause `OpenApiGenerator.cs:50-115` builds only Summary/Description/OperationId/Tags/Responses).
  Generated per-endpoint pages can't show `fps`/`value`/`multiplier`/`name`/`playerId` or the
  `POST /newgame` body. Doc-side mitigation: complete `introduction.md`'s hand-written params
  section (M10). Code-side: extend the generator — mind
  `.claude/rules/openapi-generator-reflection-invoke.md` (reflection-invoked, fixed positional
  args; optional params break the Docker build while `dotnet build` stays green).
- **M10. `introduction.md` "POST Endpoint Parameters" covers 4 of 8 write ops.** Missing:
  `POST /clock-speed` (`?multiplier=`, double > 0, :4006-4077), `POST /auth/timeout` (`?value=`,
  :3891), `POST /newgame` (JSON body `NewGameRequest` :606-631 — also contradicts the section's
  "via query string" claim), `POST /reload` (no params; fails while clients connected, :4544-4554).
- **M11. `introduction.md` Configuration table:** "API key … for write endpoints" is wrong — ALL
  non-public endpoints need it, including GETs (`:1926-1931`; the doc's own auth section says so);
  "(empty = no auth)" omits that the shipped entrypoint refuses to start that way unless
  ALLOW_INSECURE_SETUP=true (`startapp.sh:31-56`).
- **M12. Spec lacks `securitySchemes`/`security` entirely** (parse-verified) — rendered reference
  gives no auth affordance. Generator change per D5; mirror the public-endpoint split.
- **M13. Five reachable GETs absent from spec and all prose:** `/wait/status`, `/wait/players`,
  `/wait/health`, `/wait/farmhands` (`?since=N` long-poll), `/diagnostics/handler-timing`
  (dispatcher `:1971-1987`; no `[ApiEndpoint]` attributes, so the generator never sees them —
  inconsistent with `/diagnostics/state`, which IS attributed `:2643`). Either attribute them
  (matching the /diagnostics/state precedent) or document the exclusion policy in
  introduction.md.

### `contributing/`
- **M14. `ci-cd.md:200` — stale required-check list.** "merges on Validate Build + Validate
  Commits + Validate Line Endings alone" omits Validate Formatting / JS-TS / PR Title; contradicts
  the doc's own six-check lists (:144, :184, :189) and both workflows
  (`validate-merge-group.yml:6-8`). Sentence predates the CSharpier gate. (High within this file.)
- **M15. `ci-cd.md` E2E section — nightly schedule + `pr` dispatch input missing.** Four entry
  points now (`e2e-tests.yml:7-16,58-66`); "manual and maintainer-gated" needs the nightly
  exception. (Same fact as plan 03 M3 — fix both pages consistently.)
- **M16. `contributing/index.md:113-119` — commit-type list shows 6 of 11.** commitlint accepts
  perf, revert, style, build, ci as well (`commitlint.config.js:10-26`); repo history uses them.
- **M17. `contributing/index.md` — contributor + maintainer flows don't mention the merge queue.**
  ci-cd.md:171 says merges go through the queue; index.md never says "enable auto-merge", and the
  maintainer section describes classic branch protection while the repo uses a ruleset + queue
  (`validate-merge-group.yml`; `lefthook.yml:31-39`). Add the auto-merge step + ruleset/queue
  setup + the "every required check needs a merge_group producer" rule.
- **M18. `ci-cd.md` — DEPLOY_API_KEY marked optional but the workflow aborts without it**
  **[DECISION D7]** (`deploy-server.yml:113-124`).

## Low (riders)

- `steam-auth.md`: add the `healthcheck` command row (`Program.cs:193-204`; used by the image
  HEALTHCHECK + harness wait); note `export-token` emits one JSON object **per saved session** and
  Logger lines can interleave — `> token.json` isn't guaranteed single-document under
  multi-account (`Program.cs:336-363,58-61`).
- `networking.md`: netdebug section lists 3 of 5 subcommands — add `gog-ports`/`gog-requests` or
  link the (complete) admin page (`tools/netdebug/Program.cs:60-71`).
- `mod-architecture.md`: "WebSocket for real-time updates" → "real-time chat relay (used by the
  Discord bot)" — chat broadcast is the only push (`ApiService.cs:2171`); auth/pong are replies.
  (The auto-discovery claim is TRUE — reflection over `IModService`, `ModEntry.cs:29,214-224` —
  keep it.)
- `client-manipulation-techniques.md`: table silently omits types 5/9/11/16 (add or note the
  omission; type 11 matters to this project); "the server dispatches… `Multiplayer.
  processIncomingMessage()`" → that's the *client* dispatcher (server-side is
  `GameServer.processIncomingMessage`, `GameServer.cs:694`).
- `decompiling.md`: note the script requires `GAME_PATH` in `.env` and the `ilspycmd` dotnet tool
  (`tools/decompile-sdv.sh:12-45`).
- `events-schema.md:31`: tickMs emitters → "(`ModEventLog`, `ClientEventLog`)"
  (`ClientEventLog.cs:44,53`).
- `ci-cd.md`: add Label PR row + a one-line "reusable workflows" note for
  `build-image.yml`/`build-docs.yml`; fix the second stale check-enumeration in the CodeQL
  "Advisory, Not Required" paragraph.
- `contributing/index.md`: add a "Working on the documentation" subsection (`make docs` flow;
  bun-only alternative for prose edits) — currently zero published-docs entry point for docs
  contributors (pairs with plan 03 H5).
- `CONTRIBUTING.md:17` (repo root, companion touchpoint): anchor
  `…/community/contributing#ci-cd-pipeline` targets a heading that doesn't exist in the 11-line
  stub — link `…/developers/contributing/ci-cd` directly.
- `mod/JunimoServer/Env.cs:69-75` (companion code-docstring touchpoint): the API_KEY XML doc claims
  "write operations (POST, DELETE) require the X-API-Key header" — actual contract is
  `Authorization: Bearer` on ALL non-public endpoints (`ApiService.cs:1649-1670,1927`). This stale
  docstring is the likely origin of the M11 drift — fix it in the same pass.
