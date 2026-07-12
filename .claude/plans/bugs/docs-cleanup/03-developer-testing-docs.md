# 03 — Developer & Testing Docs Corrections

**Objective:** Remove fictional infrastructure from the testing docs, catch them up to the
multi-host refactor, and make the from-source/docs-site workflows actually followable.

**Scope:** `docs/developers/testing/**`, `docs/developers/advanced/building-from-source.md`,
`docs/README.md`. Companion touchpoints (flag in PR, don't change silently): `CLAUDE.md` runbook
step count; stale code comments named below.

**Verification gate:** `make docs` builds clean; greps return zero doc hits for: `E2E_VNC_PORT`,
`E2E_API_PORT`, `health.slow_response`, `SaveLogsToFileAsync`, `ValidateRequirements` (in
remote-host-setup.md); a fresh-contributor walkthrough of building-from-source.md and the
e2e Quick Start succeeds on paper against `Makefile` + `.env.test.example`.

---

## High

### H1. `e2e-testing.md` — documents a compose-based E2E environment that does not exist
- **Sections:** "During Compose Environment" + "Compose Environment Variables" table
- **Problem:** `E2E_VNC_PORT`, `E2E_CLIENT_VNC_PORT`, `E2E_API_PORT`, `E2E_CLIENT_API_PORT`,
  `E2E_GAME_PORT` appear nowhere in the repo outside this doc (confirmed twice by project-wide
  grep). The only compose file is the production stack (no client service; vars are
  `VNC_PORT`/`API_PORT`/`GAME_PORT`/`QUERY_PORT`, `docker-compose.yml:8-13`); compose's
  IMAGE_VERSION default is `latest`, not `local`. All E2E observation goes through Testcontainers
  dynamic ports / `make test-web`.
- **Fix:** delete both subsections (preferred) or rewrite against the real stack.

### H2. `e2e-testing.md` Quick Start — missing the mandatory `.env.test` step
- **Problem:** `SDVD_DOCKER_HOSTS` is required — the runner throws at startup when unset
  (`HostPool.cs:128-135`); `STEAM_ACCOUNTS` is needed for Steam tests. A new contributor running
  `make test` per Quick Start hits an immediate InvalidOperationException; the requirement only
  appears much later in the Multi-Host section.
- **Fix:** add a prerequisite step: copy `.env.test.example` → `.env.test`, set `SDVD_DOCKER_HOSTS`
  (single local entry suffices, e.g. `[{"id":"local","serverSlots":3,"clientSlots":6}]`), optional
  `STEAM_ACCOUNTS`.

### H3. `test-harness.md` — wrong watchdog event name and threshold
- **Problem:** doc says `health.slow_response` (>3s); code emits `health.slow_tick` when
  `lastTickMs > 5000` within the healthy (<30s) branch (`ManagedServer.cs:1061-1067`;
  catalog `InfrastructureEventLog.cs:280`). Grepping the documented name finds nothing.
- **Fix:** `health.slow_tick` (game thread ticked but lastTickMs > 5s), alongside the (correct)
  `health.check_failed` / `health.check_error` / `health.poison`.

### H4. `building-from-source.md` — instructs hardcoding `<GamePath>` in the .csproj
- **Problem:** the supported mechanism is `GAME_PATH` in `.env`, read by
  `Directory.Build.props:46-62` and consumed as `$(GamePath)`
  (`JunimoServer.csproj:54-66` has no GamePath property); CLAUDE.md explicitly prohibits hardcoding
  it. Following the doc dirties a tracked file.
- **Fix:** replace steps 2-3 with: set `GAME_PATH` in `.env` (documented at `.env.example:107-111`,
  forward slashes, Windows/Linux examples); mention `Directory.Build.props` and the `/p:GamePath=`
  override.

### H5. `docs/README.md` — fresh-clone instructions fail; page describes a different project
- **Problems (three confirmed):**
  1. `config.ts:6` statically imports `../assets/openapi.json`, which is **gitignored**
     (`docs/.gitignore:9`) and generated from the server image — `npm run dev` on a fresh clone
     fails at config load. Never mentioned; neither are `make docs` (Makefile:135-143) or
     `docs/scripts/fetch-openapi.sh`.
  2. The toolchain is **Bun** (bun.lock, no package-lock.json; Makefile and
     `build-docs.yml:42-53` both use bun) — the README prescribes npm throughout.
  3. The "Project Structure" block is fiction: no `website/`, no `guide.md`, no `docs/biome.json`
     (lint config is repo-root `biome.jsonc`); real dirs (assets/, public/, scripts/, five content
     sections) are absent.
- **Fix:** rewrite: prerequisite (built server image + `make docs`, or `fetch-openapi.sh`), bun
  commands, real tree. Plan 05 additionally proposes `srcExclude` so this README stays out of the
  published site.

## Medium

### `e2e-testing.md`
- **M1. Recording pipeline outdated.** Extraction is now two-pass (concat stream-copy → seek with
  re-encode libx264/NVENC, `ContainerRecorder.cs:923,1670-1671`), bounded per-host by
  `DockerExtractLimiter` — "zero CPU"/"no re-encoding" no longer true.
- **M2. Output tree misplaces `flakiness.jsonl`.** Cross-run file at `TestResults/flakiness.jsonl`
  (root), not per-run (`FlakinessTracker.cs:9-14`; `Makefile:248`). Same fix in `test-harness.md`
  per-run list.
- **M3. CI Usage omits the nightly trigger.** Fourth entry point: cron `0 8 * * *` full suite on
  master for the README badge (`e2e-tests.yml:62-66`); "all manual" is wrong (the never-a-merge-gate
  claim stays true). Same fix in `ci-cd.md` — plan 04.
- **M4. `SDVD_VOLUME_PREFIX` scope overstated.** Only `DownloadValidationFixture.cs:38` reads it;
  the main suite hardcodes `server_game-data`/`server_steam-session`
  (`ServerContainerOptions.cs:19,24`, `GameClientOptions.cs:23`). Scope the row or wire it through
  before documenting it as general.
- **M5. Configuration Reference incomplete and unanchored.** Omits consumed knobs
  (SDVD_TEST_TRACING, SDVD_TEST_STATS, SDVD_STOP_ON_FAIL, SDVD_PARALLEL,
  SDVD_CLIENT_LEASE_PATIENCE_S, SDVD_MAX_CONCURRENT_STARTS/EXTRACTIONS, transfer-retry vars,
  SDVD_TEST_RECORDING_SEGMENT_TIME, SERVER_TPS/CLIENT_TPS, DISPLAY_WIDTH/HEIGHT — consumers
  verified individually). Fix: declare `.env.test.example` the authoritative reference, link it,
  keep only the highest-value rows inline.
- **M6. Multi-Host section duplicates `remote-host-setup.md` and has drifted.** e2e copy lists only
  endpoint/sshKey as optional; parser also supports socketPath, inline sshKey, gpu,
  concurrentStarts, concurrentExtractions (`HostPool.cs:19-25,453-468`; `.env.test.example:47-63`).
  Zero cross-links in either direction. **Consolidation:** remote-host-setup.md becomes the single
  schema/provisioning/SSH-troubleshooting owner (NOTE: provisioning steps 1-5 currently exist ONLY
  in e2e-testing.md — move them, don't delete); e2e keeps concepts + link.

### `test-harness.md`
- **M7. Per-test observability stale.** `SaveLogsToFileAsync` doesn't exist (doc-only grep hit);
  no per-test `server.log`. Logs are per-container, always-on, at
  `containers/{server-N|client-N|steam-auth-*}/container.log` (`TestArtifacts.cs:23-34`;
  `make test-container-log`).
- **M8. Component catalog predates multi-host.** ServerContainer no longer manages steam-auth
  (per-host `SharedSteamAuth`, "not per-server" — `ServerContainer.cs:19`); ClientPool is per-host
  via the host's `DockerStartLimiter`; "ClientCapacity" is the per-host `HostCapacityQueue`.
  Add SharedSteamAuth + HostPool/DockerHost as the ownership root.

### `remote-host-setup.md`
- **M9. Per-host resources table wrong twice.** (1) runId suffixes are per-resource random 8-char
  GUIDs (`SharedSteamAuth.cs:113,119`; `TestNetworkManager.cs:49-56`) — nothing run-wide ties them;
  (2) the game-data volume is created **without labels** (`GameDataDistributor.cs:356-358`) so the
  `sdvd.test` sweep never touches it — that's why it survives as a cross-run cache
  (`EmergencyCleanup.cs:312-360`). Fix both cells.
- **M10. STEAM_ACCOUNTS slicing claims contradict the slicer.** Once `remaining < 2`, every later
  host gets 0 and the leftover account stays **unassigned** — "the next host picks it up" is
  impossible; worked-example row 3 is `local=3, vps=0` (not vps=1)
  (`SteamAccountSlicer.cs:73-114`). NOTE: the slicer's own comments (:19-20, :81-82) carry the same
  wrong claim — flag fixing them in the same PR (companion code-comment touchpoint).
- **M11. Sizing rule cites a method that doesn't exist.** No
  `ServerConfigDiscovery.ValidateRequirements`; the check lives in
  `ServerConfigDiscovery.DiscoverRequiredConfigs` (validation block). The behavior claim itself is
  correct. (Stale comment at `TestResourceBroker.cs:150` — same companion pass.)

### `test-failure-runbook.md`
- **M12. Step count drifted: doc has 7 steps; CLAUDE.md and `developers/index.md` say 6.** Either
  renumber step 7 as a conditional appendix or update both references (companion CLAUDE.md
  touchpoint — explicit, not silent).
- **M13. Never mentions `make test-diagnose`** (dumps last `failure_context` — purpose-built for
  step-1/2 triage, `Makefile:250-255`) **or `make test-metadata`**. Add both.

### `building-from-source.md`
- **M14. ".NET SDK 6" requirement is wrong for everything but the mod itself.** Tests/runner are
  net10.0; CI installs 10.0.x for csharpier; the Docker build uses sdk:10.0. Fix: SDK 10 (mod
  targets net6.0 but builds under newer SDKs), or split per activity.
- **M15. Prerequisites omit Node.js/npm (make install → npm ci) and bun (make docs/logs,
  test-ui)**; the Make Commands table lists 7 of ~25 targets. Replace with the relevant handful +
  "run `make help`".

## Low (riders)

- `e2e-testing.md`: steam-auth is NOT on jlesage/baseimage-gui (plain `dotnet/aspnet:10.0`, no
  VNC) — "all containers" → "server and client containers"; quoted error "Steam-auth container
  failed to start within 60 seconds" doesn't exist (real: "Steam preflight failed…"); image
  distribution phrasing → "streamed docker-save tar over the SSH-tunneled Docker API".
- `test-harness.md`: `test_enrichment` producer site is `TestFailureReporter.cs:112` (invoked from
  the TestBase dispose flow), not TestBase.DisposeAsync itself.
- `remote-host-setup.md`: add the missing `concurrentExtractions` schema row (fallback
  SDVD_MAX_CONCURRENT_EXTRACTIONS → serverSlots+clientSlots).
- `festivals-manual.md`: point "stuck at Waiting for players" at the actually-logged
  `[Festival] … ready=N/M, CheckOthersReady=…` line (SetLocalReady is not logged); annotate which
  festivals have a main event (Spirit's Eve and Winter Star are leave-only —
  `AlwaysOnFestivals.cs:152,171,361-368`).
