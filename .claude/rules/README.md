# `.claude/rules/` — Index

Project policy is layered. `CLAUDE.md` (L0) and `rules/universal/*.md` (L1) load every session. `rules/*.md` (L2) load when a `paths:` glob matches. Authoring guidance: `.claude/skills/extract-session-rules/SKILL.md`.

`paths:` semantics: gitignore-style globs, OR'd together. A rule loads when an edited path matches any entry.

## L1 — `universal/` (always-on)

| File | One-liner |
|---|---|
| `adversarial-review-split-findings.md` | Split findings honestly (inherent vs OOS, reachable vs latent), keep a valid claim when only its framing was weak, hand deferrals back as open user decisions — and in workflow reviews, batch verifiers per producer and salvage REFUTED sub-findings |
| `answer-then-stop.md` | A question answered *by the transcript* gets a one-line answer, then stop — world-facts still defer to `verify-claims` even when you feel sure |
| `diff-flaky-runs-before-theorizing-mechanism.md` | Localize before theorizing: tabulate one signal across pass vs fail runs, or bisect pipeline stages via intermediate probes |
| `git-workflow.md` | Project-specific git rules (no `git add .`, no Co-Authored-By trailers, chained PRs, PR descriptions, worktrees at `../worktrees/` via the WorktreeCreate hook) |
| `holistic-or-explicit-todo.md` | Don't hedge with empty scaffolding — build the holistic solution or write a concrete TODO; never a TODO about a hypothetical feature not in the tree |
| `improvement-claims-need-controlled-ab.md` | A measured-improvement claim needs a same-host same-config A/B against the parent commit — not plan numbers or a different-commit/TPS run; statically provable deltas are exempt |
| `mirror-target-component-resolution.md` | A probe that detects another component's state must mirror that component's full resolution logic, not just the happy path |
| `no-refactor-history-in-code.md` | Published text serves the cold reader — no change-history, comments earn their length (zero side-effect additions), environment-neutral public text |
| `one-parser-per-contract.md` | A contract parsed at multiple sites gets one canonical typed record + parser; when hoisting duplicated literals, grep every syntactic form |
| `orthogonal-fields.md` | Split a field only when a later write erases a still-needed earlier write — not for lifecycle-progression where the earlier value is no longer current |
| `own-the-whole-file.md` | Cleanup passes own every line — no "not mine" deferrals; implementations sweep their blast radius proactively before "done" |
| `passing-test-isnt-proof-the-scenario-ran.md` | A green test proves its assertions held, not that the scenario ran — read the run artifact to confirm the intended events fired |
| `plan-discipline.md` | Adversarial pre-verification, cross-step invalidation check, announced edit counts, every item checked off — and delete the plan when its code lands |
| `protocol-invariant-not-file-workaround.md` | When a remote protocol enforces an invariant on an identifier, a file/container-state workaround is theatre — eliminate it at the enforcement layer or accept it |
| `retry-is-evidence-of-root-cause.md` | Existing retry/fallback code is a symptom — investigate the underlying failure before extending it |
| `rule-applies-only-when-failure-mode-matches.md` | A rule's license covers its own incident only — re-read its `**Why:**` before citing it to justify a design, excuse a mistake, or stop early |
| `runtime-post-conditions-are-gates.md` | Runtime evidence outranks plan claims — run the experiment before declaring done, and first confirm the edit actually landed in the produced artifact (green build ≠ edit in binary) |
| `scope-means-no-reads-or-writes.md` | "Don't touch X" excludes link repair inside X — preserve cited filenames during refactors |
| `simplest-solution.md` | Prefer the simplest, most direct solution — no wrappers when a one-liner works; and count existing sites before calling your own code's shape a defect |
| `subagent-findings-are-claims.md` | A subagent's confident bottom-line is a claim to verify, not ground truth — open the cited file:line before building a plan on it |
| `verify-claims.md` | Verify named claims (identifiers, framework behavior, numbers, "no consumer", documented config knobs, new fail-fast rejections vs committed config) before publishing; .NET edge-case behavior gets a probe, not memory |

## L2 — `rules/` (paths-gated)

| File | Triggers on | One-liner |
|---|---|---|
| `abandoned-claim-is-steam-only.md` | test `**/*.cs`, `CabinManager/`, `Lobby/` | Live abandoned-claim repro needs `[TestServer(WithSteam=true)]` (LAN never stamps `userID`); client stamps are Galaxy-space on both transports — validate ids as bare ulong |
| `asynclocal-pitfalls.md` | `mod/`, `tests/`, `tools/` `.cs` | `AsyncLocal` doesn't flow across external pump boundaries — capture and rebind |
| `bot-review-blind-spots.md` | `.claude/**/*.md`, `CLAUDE.md` | Review bots can't see gitignored `decompiled/` — their "identifier doesn't exist" verdicts are structural false negatives; answer with the local `file:line`, judge reasoning separately from evidence-gathering |
| `cabin-system.md` | `CabinManager/`, `GameLoader/`, `GameCreator/` | Ten cabin-allocation invariants: startingCabins timing, EnsureAtLeastXCabins, SlotSelectionGate, coordinates, master-only build, live-vs-persisted Farmer split, claim sweep, protected-slot assignment |
| `chat-font-language-tag.md` | `mod/**/*.cs` | Chat font follows the per-message `LanguageCode` tag (no glyph fallback) — infer script for relayed messages; a render-suppressed server still loads fonts via the MeasureString path |
| `colocate-event-emit.md` | test `Containers/`, `Infrastructure/` | Emit state-transition events from inside the producer, not an outer coordinator |
| `debugging.md` | `mod/**/*.cs`, `tests/test-client/**` | `LogLevel.Error` in mod code is test poison — `\b(ERROR\|FATAL)\b` triggers cancellation; but the scan is SERVER-side only (`ServerContainer.cs`), so test-client Error is loud, not poison |
| `disconnect-settles-client-not-server.md` | `tests/JunimoServer.Tests/**/*.cs` | `DisconnectAsync` settles the client only — gate offline/persisted-state assertions on `WaitForPlayersRemovedByIdAsync` |
| `display-scaling.md` | `mod/JunimoServer/**`, `mod/JunimoServer.Shared/**` | Zoom out via Harmony-patched `desired*` getters, not scale-field pokes — `Game1.Update` reconciles pokes away and save-load clobbers the persisted `zoomLevel` |
| `docker-save-format-source-daemon.md` | `tests/JunimoServer.TestRunner/Distribution/**`, `e2e-tests.yml` | `docker save` format follows the SOURCE daemon's image store — CI enables the containerd store in-place on the ONE pre-installed daemon; don't reintroduce a second daemon |
| `docker-test-resources.md` | `tests/**/Containers`, `Helpers/Docker*`, `Helpers/ContainerStatsCollector.cs`, `Infrastructure` | Testcontainers patterns + mandatory `WithDockerEndpoint(host.EndpointConfig)` |
| `drain-before-consume-disposal.md` | `tests/JunimoServer.TestRunner/**`, test `Containers/**` | Drain producer streams explicitly before consumer disposal — `await using` ordering isn't enough |
| `ffmpeg-pixel-measurement.md` | `ContainerRecorder.cs`, `TestOverlay.cs`, `RenderingTests.cs`, `tools/.playground/recording-validator/**` | Measure ffmpeg-rendered pixels with per-column `crop=1:H` + rgb24, not a full-frame `format=gray` raw scan — the raw stream's stride drifts and reports phantom edges |
| `follow-true-created-state-eof.md` | test `Containers/**`, `Helpers/Docker*.cs` | `GetContainerLogsAsync(Follow=true)` returns immediate EOF for a `Created` (not yet running) container — retry on first-read EOF with no prior reads |
| `glibc-execstack-dlopen.md` | `docker/**`, `tools/steam-service/**` | glibc >= 2.41 refuses to dlopen executable-stack libs — clear the ELF flag via `ExecstackPatcher`, never the `glibc.rtld.execstack` tunable (deadlocks .NET under emulated amd64) |
| `harmony-patch-reachability.md` | `mod/JunimoServer/**` | Three reachability bounds on a Harmony patch: the registering constructor must complete, patches never reach farmhand clients, and the target's shape/timing must admit a patch (else source-patch SMAPI) |
| `host-automation.md` | `AlwaysOnServer/`, `HostAutomation/`, `Lobby/`, `CabinManager/` | Decompiled-first; `hasDedicatedHost = false`, `netReady` formula, festival repro caveat, draw-coupled FarmEvent completion, host farmhouse internal-only |
| `image-runtime-deps-must-be-explicit.md` | `docker/**/Dockerfile*` | Removing image packages can silently drop the app's transitive runtime deps (libicu) — boot the image after package removals; declare real runtime deps explicitly with a consumer comment |
| `master-mail-gates-world-state.md` | `SaveImport/`, `GameLoader/` | World geometry (CC/greenhouse/island) is gated on `MasterPlayer.mailReceived`/`eventsSeen` (per-Farmer, NOT team-stored) — a master swap must copy mail/events or the world reverts |
| `masterplayer-is-player-on-server.md` | `mod/JunimoServer/**` | `multiplayerMode = 2` on this server makes `IsMasterGame` always true, so `Game1.MasterPlayer` always resolves to `Game1.player` (same `Farmer`) — reject any bug/design that hinges on host-vs-master divergence |
| `minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md` | `tests/**/*.cs` | `docker exec` degrades ~24× under parallel load — one in-shell wait loop, not N C# polls; cut diagnostic execs with no consumer |
| `mod-game-thread-allocation.md` | `mod/**/*.cs` | Minimize per-tick/per-scan allocations on the game thread up front (reuse buffers, double-buffer + swap to prune a keyed map) — the stated-perf-constraint exception to `simplest-solution.md`, scoped to the hot path |
| `modern-docker.md` | `docker/modern/`, `ServerOptim/` | musl gotchas: pthread_shim, RunSynchronously deadlock, BlockOnUIThread can't be patched |
| `netdictionary-public-surface.md` | `mod/JunimoServer/**` | Mutate `NetDictionary` via public API, not `FieldDict.Remove/Add` |
| `netfield-revert-pattern.md` | `mod/JunimoServer/**` | Don't revert peer-replicated NetField writes inside `fieldChangeEvent` — interpolation makes Set a no-op |
| `no-level-0-marriage-map.md` | `mod/**/*.cs`, `tests/**/*.cs` | A farmhand must be `houseUpgradeLevel >= 1` before marrying — no level-0 marriage map exists, so a level-0 married farmhouse crashes `_newDayAfterFade` |
| `one-second-update-ticked-fires-per-game-tick.md` | `mod/**/*.cs` | Fires every 60 game ticks (12s at `SERVER_TPS=5`): sequential handlers go per-tick with wall-clock gating; one-shot host writes go on the `OnSaveLoaded` pre-seed path, or they race other writers |
| `one-writer-per-artifact.md` | `tests/JunimoServer.TestRunner/**`, `tests/JunimoServer.Tests/Fixtures/**` | Two producers of the same artifact = silent schema drift — merge upstream state, not downstream files |
| `plans-cite-files-not-lines.md` | `.claude/plans/**` | Plans cite files and symbols, never `:123` — positions and growing counts drift over a plan's months-long shelf life; in-session findings still cite exact lines |
| `prefer-live-stream-over-disk-artifact.md` | `tests/test-ui/**`, `TestRunner/**` | Check if the WebSocket already carries the data before fetching it from disk; a translation map between two event-name vocabularies means the wrong source was picked |
| `prejoin-control-surface.md` | `AuthService/`, `Lobby/`, `SteamGameServer/` | The whole pre-join surface is type 11 status text (unhideable `Strings\UI:` prefix), type 9 slot list (any list disarms the rebuild latch), and type 23 `forceKick` — no input channel, no durable LAN identity |
| `provision-up-front-when-startup-exceeds-serviceable-tail.md` | `tests/JunimoServer.Tests/Infrastructure/**`, `tests/JunimoServer.TestRunner/**` | When startup cost (~41s boot) exceeds the serviceable tail window, provision at prestart instead of reacting harder to contention |
| `recorder-anchor-first-frame.md` | `tests/**/Helpers/ContainerRecorder.cs`, `Helpers/Recording*.cs` | Load-bearing recorder invariants (timing flags, segment format, anchor, phase-lock, extraction, filenames) — reverting any brings its bug back; verify claims against both `_useGpu` branches |
| `renovate-nuget-allowedversions-needs-semver.md` | `renovate.json` | `allowedVersions` silently no-ops under the default `nuget` scheme — add `versioning: semver`, use explicit comparators, verify with a local dry-run |
| `runner-ui-pipeline-plumbing.md` | `SetupEventBus.cs`, `ContainerStatsCollector.cs`, `TestRunner/**`, test-ui types/store | Adding a field to a runner→UI event needs end-to-end plumbing — every hop is hand-written, and the pipe silently drops JSONL lines over 4 KB |
| `save-import-layer-timing.md` | `SaveImport/`, `GameLoader/`, `CabinManager/` | `saves import` = Layer A (pre-load, pure XML, no `Game1`) + Layer B (SaveLoaded finalizer, live engine: map bind first, then cabin + `AssignFarmhand`); don't let the `/test` path's live engine leak into Layer A — plus Steam64-bind, pet-resolution, and cellar-move invariants on host swap |
| `sdv-xmlignore-field-vs-serialized-property.md` | `mod/**/*.cs` | `[XmlIgnore]` on a *field* doesn't mean unserialized — SDV serializes any public property lacking the attribute; unserialized only when property AND field are both ignored |
| `server-tps-headless.md` | `Env.cs`, `.env*`, `tests/**/*.cs`, `e2e-tests.yml` | `SERVER_TPS=5` is the proven-stable headless value (CI runs the full suite at it); the `.env.example` "20-30" prose is conservative docs, not the floor |
| `smapi-api-surface.md` | `mod/**/*.cs` | SMAPI gotchas: `SemanticVersion` lives in `StardewModdingAPI.Toolkit` (ctor throws, `TryParse` doesn't); `ICommandHelper` has only `Add` — invoke commands in tests by reflecting `SCore.Instance` |
| `startup-cold-start-measurement.md` | `GameManager/**`, `ApiService.cs`, `RenderingController.cs`, `ServerContainer.cs` | Boot-band cost is dominated by host + recording config — check `host_id`/`SERVER_FPS` before quoting numbers; the listed startup dead-ends are verified non-wins |
| `test-broker-invariants.md` | `tests/JunimoServer.Tests/Infrastructure`, `Helpers` | KeepConnected capacity, exclusive deadlocks, Steam singletons, session liveness, polling budgets, config-hash keys, snapshot purity, SSH-master capture — walk the DO-NOTs before touching broker code |
| `test-day-transition-needs-connected-driver.md` | `tests/**/*.cs` | Day transitions need a connected driver (`SleepToSaveAsync` or a `SecondFarmer`) — an empty server won't advance its own clock, so driverless `SetTime` flakes |
| `test-overlay-pixel-contract.md` | `TestOverlay.cs`, `RenderingTests.cs` | Overlay geometry is a hidden pixel contract — different TFMs prevent sharing, grep both sides before editing |
| `test-state-setter-runs-engine-reconcile.md` | `mod/JunimoServer/Services/Api/**` | A `/test/*` state-setter must run the engine's reconciliation, not just poke `Game1` fields — `/time` needs `UpdateFromGame1()` to replicate; `/test/set_date` needs the new-day reset (`timeOfDay=600`, `whereIsTodaysFest=null`, `updateWeatherIcon()`) or stale festival weather crashes the loop |
| `tests-assert-via-http-api.md` | `tests/JunimoServer.Tests/**/*.cs` | E2E tests assert via the server HTTP API snapshot (`/cabins`, `/players`, `/farmhands`), never mod events; a stuck-but-uncustomized slot still counts "available" |
| `test-timing.md` | `tests/**/*.cs` | Per-test overhead ≠ wall-clock cost; `queueDurationMs` is xUnit dispatch-wait, not a broker bottleneck |
| `vanilla-client-control-primitives.md` | `mod/**/*.cs` | Vanilla clients obey only the verified primitives — join-time placement (message 3), the fee-gated passout warp, synced `location.warps` rewrites; map any "make the client do X" design onto these first |

## Public documentation

Reference material and runbooks for contributors live in the public VitePress site under [`docs/developers/`](../../docs/developers/) — game engine notes, mod architecture, test harness reference, manual-testing runbooks, and the test-failure debugging procedure. The Alpine/musl image reference lives at [`docs/admins/operations/modern-docker.md`](../../docs/admins/operations/modern-docker.md).
