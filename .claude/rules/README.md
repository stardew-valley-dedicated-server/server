# Rule Index

Rules are layered:

- `CLAUDE.md` — repository-wide guidance.
- `.claude/rules/universal/*.md` — always loaded.
- `.claude/rules/*.md` — loaded when an edited path matches `paths:`.

`paths:` uses gitignore-style globs; multiple patterns are OR'd. Universal rules
carry no index row — they are always loaded; discover them by globbing
`universal/`. Only the path-scoped rules below need an index entry, because the
index is the sole discovery mechanism for a rule that has not yet been loaded.

For rule authoring/extraction, see `.claude/skills/extract-session-rules/SKILL.md`.

## L2 — `rules/` (paths-gated)

Each one-liner is a trigger clause — enough to decide *whether* to open the rule.
The rule loads in full on Read of a matching path.

| File | Triggers on | One-liner |
|---|---|---|
| `apiservice-entry-is-conditional.md` | `mod/JunimoServer/**` | `ApiService.Entry` returns early when `API_ENABLED=false`; shared infra must own its own `UpdateTicked`, not an `ApiService` handler |
| `abandoned-claim-is-steam-only.md` | test `**/*.cs`, `CabinManager/`, `Lobby/` | Live abandoned-claim repro needs `[TestServer(WithSteam=true)]` (LAN never stamps `userID`); validate ids as bare ulong |
| `asynclocal-pitfalls.md` | `mod/`, `tests/`, `tools/` `.cs` | `AsyncLocal` doesn't flow across external pump boundaries — capture and rebind |
| `autopause-is-the-sole-ispaused-writer.md` | `mod/JunimoServer/**` | `HandleAutoPause` is the sole lifecycle writer of `IsPaused`; a stray one-tick unpause banks clock an unauthenticated client can farm |
| `bot-review-blind-spots.md` | `.claude/**/*.md`, `CLAUDE.md` | Review bots can't see gitignored `decompiled/` — "identifier doesn't exist" is a false negative; answer with the local `file:line` |
| `cabin-system.md` | `CabinManager/`, `GameLoader/`, `GameCreator/` | Eleven cabin-allocation invariants (unconditional patches, `EnsureAtLeastXCabins`, slot gating, claim sweep, staged migration) |
| `chat-font-language-tag.md` | `mod/**/*.cs` | Chat font follows the per-message `LanguageCode` tag (no glyph fallback); a render-suppressed server still loads fonts via MeasureString |
| `colocate-event-emit.md` | test `Containers/`, `Infrastructure/` | Emit state-transition events from inside the producer, not an outer coordinator |
| `debugging.md` | `mod/**/*.cs`, `tests/test-client/**` | `LogLevel.Error` in server mod code is test poison (`\b(ERROR\|FATAL)\b` cancels); the scan is server-side only |
| `deployment-config-is-env-not-settings-file.md` | `.github/workflows/**`, `docker-compose.yml`, `Env.cs`, `Services/Settings/**` | Per-deployment knobs are env vars from GitHub Environment vars, never a shipped `server-settings.json`; `SDVD_` is test-harness/kill-switch only |
| `diff-flaky-runs-before-theorizing-mechanism.md` | `tests/**/*.cs` | Localize before theorizing: tabulate one signal across pass vs fail runs, or bisect pipeline stages via intermediate probes |
| `disconnect-settles-client-not-server.md` | `tests/JunimoServer.Tests/**/*.cs` | `DisconnectAsync` settles the client only — gate offline/persisted-state assertions on `WaitForPlayersRemovedByIdAsync` |
| `display-scaling.md` | `mod/JunimoServer/**`, `mod/JunimoServer.Shared/**` | Zoom out via Harmony-patched `desired*` getters, not scale-field pokes — `Game1.Update` and save-load clobber pokes |
| `docker-save-format-source-daemon.md` | `tests/JunimoServer.TestRunner/Distribution/**`, `e2e-tests.yml` | `docker save` format follows the source daemon's image store — CI enables the containerd store in-place; don't add a second daemon |
| `docker-test-resources.md` | `tests/**/Containers`, `Helpers/Docker*`, `Helpers/ContainerStatsCollector.cs`, `Infrastructure` | Testcontainers patterns + mandatory `WithDockerEndpoint(host.EndpointConfig)`; stats `Progress<T>` callbacks are concurrent |
| `dotnet-appdata-folder-needs-existing-xdg-dir.md` | `docker/**`, `mod/**/*.cs`, `tests/test-client/**`, `tools/xnb-unpacker/**` | `GetFolderPath(ApplicationData)` returns "" unless the XDG config dir exists — create it in the root init hook or SMAPI writes go astray |
| `drain-before-consume-disposal.md` | `tests/JunimoServer.TestRunner/**`, test `Containers/**` | Drain producer streams explicitly before consumer disposal — `await using` ordering isn't enough |
| `ffmpeg-pixel-measurement.md` | `ContainerRecorder.cs`, `TestOverlay.cs`, `RenderingTests.cs`, `tools/.playground/recording-validator/**` | Measure ffmpeg-rendered pixels with per-column `crop=1:H` + rgb24, not a full-frame `format=gray` raw scan (stride drifts) |
| `follow-true-created-state-eof.md` | test `Containers/**`, `Helpers/Docker*.cs` | `GetContainerLogsAsync(Follow=true)` returns immediate EOF for a `Created` container — retry on first-read EOF |
| `github-run-script-evaluates-expressions.md` | `.github/**` | GitHub evaluates `${{ }}` inside `run:` text (even comments) at compose time — pass data via `env:`/`$VAR` |
| `glibc-execstack-dlopen.md` | `docker/**`, `tools/steam-service/**` | glibc >= 2.41 refuses to dlopen exec-stack libs — clear the ELF flag via `ExecstackPatcher`, never the `glibc.rtld.execstack` tunable |
| `harmony-patch-reachability.md` | `mod/JunimoServer/**` | Three reachability bounds on a Harmony patch: registering ctor completes, patches never reach farmhand clients, target must admit a patch |
| `host-automation.md` | `AlwaysOnServer/`, `HostAutomation/`, `Lobby/`, `CabinManager/` | Host-automation invariants: `hasDedicatedHost=false`, `netReady` formula, empty-server pause needs host activity, grace sleep past 1 AM |
| `image-runtime-deps-must-be-explicit.md` | `docker/**/Dockerfile*` | Removing image packages can silently drop transitive runtime deps (libicu) — boot the image after removals; declare real deps explicitly |
| `installer-local-image-testing.md` | `docs/public/**`, `Makefile`, `docker-compose*.yml` | Test the installer against unpublished images with `IMAGE_VERSION=local` after `make build` (server build needs Steam creds), never a shadow tag — any pull silently replaces it |
| `local-composite-action-needs-head-checkout.md` | `.github/**` | A local `uses: ./…` action must exist in the checked-out tree; under `pull_request_target` it reds in-flight PRs — inline or pin `@ref` |
| `master-mail-gates-world-state.md` | `SaveImport/`, `GameLoader/` | World geometry (CC/greenhouse/island) is gated on `MasterPlayer.mailReceived`/`eventsSeen` (per-Farmer) — a master swap must copy them |
| `masterplayer-is-player-on-server.md` | `mod/JunimoServer/**` | `multiplayerMode=2` makes `IsMasterGame` always true, so `Game1.MasterPlayer` == `Game1.player` — reject host-vs-master-divergence designs |
| `minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md` | `tests/**/*.cs` | `docker exec` degrades ~24× under parallel load — one in-shell wait loop, not N C# polls; cut diagnostic execs with no consumer |
| `mirror-target-component-resolution.md` | `mod/**/*.cs`, `tests/**/*.cs`, `tools/**/*.cs` | A probe detecting another component's state must mirror that component's full resolution logic, not just the happy path |
| `mod-game-thread-allocation.md` | `mod/**/*.cs` | Minimize per-tick allocations on the game thread (reuse buffers, double-buffer + swap) — the stated-perf-constraint exception to `simplest-solution` |
| `modern-docker.md` | `docker/modern/`, `ServerOptim/` | Alpine/musl gotchas: pthread_shim, RunSynchronously deadlock, BlockOnUIThread can't be patched |
| `netdictionary-public-surface.md` | `mod/JunimoServer/**` | Mutate `NetDictionary` via public API, not `FieldDict.Remove/Add` |
| `netfield-revert-pattern.md` | `mod/JunimoServer/**` | Don't revert peer-replicated NetField writes inside `fieldChangeEvent` — interpolation makes Set a no-op |
| `no-level-0-marriage-map.md` | `mod/**/*.cs`, `tests/**/*.cs` | A farmhand must be `houseUpgradeLevel >= 1` before marrying — a level-0 married farmhouse crashes `_newDayAfterFade` |
| `one-parser-per-contract.md` | `mod/**/*.cs`, `tests/**/*.cs`, `tools/**/*.cs` | A contract parsed at multiple sites gets one canonical typed record + parser; grep every syntactic form when hoisting literals |
| `one-second-update-ticked-fires-per-game-tick.md` | `mod/**/*.cs` | Fires every 60 game ticks (12s at `SERVER_TPS=5`): gate sequential handlers on wall-clock; one-shot host writes go on `OnSaveLoaded` |
| `one-writer-per-artifact.md` | `tests/JunimoServer.TestRunner/**`, `tests/JunimoServer.Tests/Fixtures/**` | Two producers of one artifact = silent schema drift — merge upstream state, not downstream files |
| `passing-test-isnt-proof-the-scenario-ran.md` | `tests/**/*.cs` | A green test proves its assertions held, not that the scenario ran — read the run artifact to confirm the intended events fired |
| `plans-cite-files-not-lines.md` | `.claude/plans/**` | Plans cite files and symbols, never `:123` — positions drift over a plan's shelf life; in-session findings still cite exact lines |
| `prefer-live-stream-over-disk-artifact.md` | `tests/test-ui/**`, `TestRunner/**` | Check if the WebSocket already carries the data before fetching from disk; an event-name translation map means the wrong source was picked |
| `prejoin-control-surface.md` | `AuthService/`, `Lobby/`, `SteamGameServer/` | Pre-join surface is type 11 status text, type 9 slot list, type 23 `forceKick` — no input channel, no durable LAN identity |
| `privilege-drop-sets-home-after-drop.md` | `docker/**`, `tools/**/Dockerfile*`, `tools/**/entrypoint.sh` | `gosu`/`su-exec` reset HOME to the passwd home (`/` for unknown uids) — set `env HOME=` after the drop, pre-create what the app writes |
| `provision-up-front-when-startup-exceeds-serviceable-tail.md` | `tests/JunimoServer.Tests/Infrastructure/**`, `tests/JunimoServer.TestRunner/**` | When startup cost (~41s boot) exceeds the serviceable tail, provision at prestart instead of reacting harder to contention |
| `recorder-anchor-first-frame.md` | `tests/**/Helpers/ContainerRecorder.cs`, `Helpers/Recording*.cs` | Load-bearing recorder invariants (timing flags, segment format, anchor, phase-lock, extraction) — verify against both `_useGpu` branches |
| `renovate-nuget-allowedversions-needs-semver.md` | `renovate.json` | `allowedVersions` silently no-ops under the default `nuget` scheme — add `versioning: semver`, verify with a local dry-run |
| `runner-ui-pipeline-plumbing.md` | `SetupEventBus.cs`, `ContainerStatsCollector.cs`, `TestRunner/**`, test-ui types/store | Adding a field to a runner→UI event needs end-to-end hand-written plumbing; the pipe silently drops JSONL lines over 4096 chars |
| `save-import-layer-timing.md` | `SaveImport/`, `GameLoader/`, `CabinManager/` | `saves import` = Layer A (pre-load XML, no `Game1`) + Layer B (SaveLoaded finalizer, live engine); keep the `/test` live engine out of Layer A |
| `sdv-xmlignore-field-vs-serialized-property.md` | `mod/**/*.cs` | `[XmlIgnore]` on a *field* doesn't stop serialization — SDV serializes any public property lacking it; unserialized needs both ignored |
| `server-tps-headless.md` | `Env.cs`, `.env*`, `tests/**/*.cs`, `e2e-tests.yml` | `SERVER_TPS=5` is the proven-stable headless value (CI runs the full suite at it); `.env.example`'s "20-30" is conservative docs, not the floor |
| `smapi-global-data-lives-on-saves-volume.md` | `mod/JunimoServer/**` | Global data is `.smapi/mod-data/<mod-uid>/` on the saves volume — survives `/newgame`/`/reload`, dies with the volume; pick store by lifetime |
| `smapi-api-surface.md` | `mod/**/*.cs` | SMAPI gotchas: `SemanticVersion` in `StardewModdingAPI.Toolkit` (ctor throws); `ICommandHelper` has only `Add`; no built-in commands until after `Entry` |
| `startup-cold-start-measurement.md` | `GameManager/**`, `ApiService.cs`, `RenderingController.cs`, `ServerContainer.cs` | Boot-band cost is dominated by host + recording config — check `host_id`/`SERVER_FPS` before quoting numbers; listed dead-ends are non-wins |
| `test-broker-invariants.md` | `tests/JunimoServer.Tests/Infrastructure`, `Helpers` | Broker DO-NOTs: KeepConnected capacity, exclusive deadlocks, Steam singletons, session liveness, polling budgets, config-hash keys, snapshot purity |
| `test-day-transition-needs-connected-driver.md` | `tests/**/*.cs` | Day transitions need a driver — a connected sleeper or the empty-server grace sleep past 1 AM (`SetTime(2550)`) — never the empty server's clock |
| `test-overlay-pixel-contract.md` | `TestOverlay.cs`, `RenderingTests.cs` | Overlay geometry is a hidden pixel contract — different TFMs prevent sharing, grep both sides before editing |
| `test-state-setter-runs-engine-reconcile.md` | `mod/JunimoServer/Services/Api/**` | A `/test/*` state-setter must run engine reconciliation, not just poke `Game1` fields — else replication lags or stale festival weather crashes |
| `tests-assert-via-http-api.md` | `tests/JunimoServer.Tests/**/*.cs` | E2E tests assert via the server HTTP API snapshot (`/cabins`, `/players`, `/farmhands`), never mod events |
| `test-timing.md` | `tests/**/*.cs` | Per-test overhead ≠ wall-clock cost; `queueDurationMs` is xUnit dispatch-wait, not a broker bottleneck |
| `tmux-bindings-need-attached-client.md` | attach-cli bin trees, `AttachCliTests.cs`, `CommandCatalogTests.cs` | `tmux send-keys` bypasses key-table bindings — drive attach-cli keybinding checks through a nested attached client |
| `vanilla-client-control-primitives.md` | `mod/**/*.cs` | Vanilla clients obey only verified primitives (join placement, fee-gated passout warp, synced `location.warps`) — map "make the client do X" onto these |

## Public documentation

Reference material and runbooks for contributors live in the public VitePress site under [`docs/developers/`](../../docs/developers/) — game engine notes, mod architecture, test harness reference, manual-testing runbooks, and the test-failure debugging procedure. The Alpine/musl image reference lives at [`docs/admins/operations/modern-docker.md`](../../docs/admins/operations/modern-docker.md).
