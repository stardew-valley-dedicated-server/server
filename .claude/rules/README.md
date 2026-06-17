# `.claude/rules/` — Index

Project policy is layered. `CLAUDE.md` (L0) and `rules/universal/*.md` (L1) load every session. `rules/*.md` (L2) load when a `paths:` glob matches. Authoring guidance: `.claude/skills/extract-session-rules/SKILL.md`.

`paths:` semantics: gitignore-style globs, OR'd together. A rule loads when an edited path matches any entry.

## L1 — `universal/` (always-on)

| File | One-liner |
|---|---|
| `adversarial-review-split-findings.md` | Adversarial review must split findings (no OOS-as-cover), must not collapse a valid recommendation along with weak framing, and in a fan-out verify→synthesize workflow must not let a binary REFUTED filter drop verified sub-findings |
| `answer-then-stop.md` | A question answered *by the transcript* gets a one-line answer, then stop (no pre-narration / re-confirm tool call / next-steps) — but world-facts still defer to `verify-claims` even when you feel sure |
| `comments-earn-their-length.md` | Comments earn their length — why-not-what, cut anything the code/name/another comment already says, rough magnitudes over false precision; much-longer-than-the-code is the signal to compact |
| `git-workflow.md` | Project-specific git rules (no `git add .`, chained PRs, PR descriptions) |
| `holistic-or-explicit-todo.md` | Don't hedge with empty scaffolding — build the holistic solution or write a concrete TODO |
| `mirror-target-component-resolution.md` | A probe that detects another component's state must mirror that component's full resolution logic, not just the happy path |
| `no-refactor-history-in-code.md` | Code/docs describe what is, not how it got there |
| `orthogonal-fields.md` | Split a field only when a later write erases a still-needed earlier write — not for lifecycle-progression where the earlier value is no longer current |
| `own-the-whole-file.md` | Cleanup passes own every line — no "not mine" deferrals |
| `plan-discipline.md` | Adversarial pre-verification, cross-step invalidation check, exact edit count in review cycles |
| `preflight-check-vs-committed-config.md` | New fail-fast preflight throws must be tested against the repo's committed config, not just the example |
| `prefer-live-stream-over-disk-artifact.md` | Check if the WebSocket already carries the data before fetching it from disk; a translation map between two event-name vocabularies means the wrong source was picked |
| `protocol-invariant-not-file-workaround.md` | When a remote protocol enforces an invariant on an identifier, a file/container-state workaround is theatre — eliminate it at the enforcement layer or accept it |
| `retry-is-evidence-of-root-cause.md` | Existing retry/fallback code is a symptom — investigate the underlying failure before extending it |
| `rule-applies-only-when-failure-mode-matches.md` | Don't pattern-match a project rule into design justification — verify the failure mode matches the rule's incident |
| `runtime-post-conditions-are-gates.md` | Runtime evidence outranks plan claims — post-conditions, fix predictions, and root-cause hypotheses are all gates; run the experiment before declaring done |
| `scope-means-no-reads-or-writes.md` | "Don't touch X" excludes link repair inside X — preserve cited filenames during refactors |
| `simplest-solution.md` | Prefer the simplest, most direct solution — no wrappers when a one-liner works |
| `subagent-findings-are-claims.md` | A subagent's confident bottom-line is a claim to verify, not ground truth — open the cited file:line before building a plan on it |
| `verify-claims.md` | Verify named claims (identifiers, framework behavior, numbers, "no consumer", in-tree-pattern risk) before publishing |
| `verify-documented-config-is-consumed.md` | Grep for at least one consumer when adding any env var / flag to user-facing docs |
| `verify-edit-landed-in-artifact.md` | A green build doesn't prove your edit is in the binary — inspect the produced artifact when COPY/include rules are non-obvious |
| `workflow-batched-verifiers.md` | Multi-agent verification defaults to one batch verifier per producer, not per-finding fan-out — estimate worst-case agent count before launching |

## L2 — `rules/` (paths-gated)

| File | Triggers on | One-liner |
|---|---|---|
| `abandoned-claim-is-steam-only.md` | test `**/*.cs`, `CabinManager/`, `Lobby/` | The abandoned-claim bug needs a stamped `userID` — LAN's `getUserID()` returns `""` so LAN is immune; live repros need `[TestServer(WithSteam=true)]` + transport-aware `Connect.WithRetryAsync` |
| `asynclocal-pitfalls.md` | `mod/`, `tests/`, `tools/` `.cs` | `AsyncLocal` doesn't flow across external pump boundaries — capture and rebind |
| `cabin-system.md` | `CabinManager/`, `GameLoader/`, `GameCreator/` | Cabin allocation invariants (startingCabins, EnsureAtLeastXCabins, SlotSelectionGate, coordinates, master-only build, disconnect live-vs-persisted Farmer split, abandoned-claim load sweep) |
| `chat-font-language-tag.md` | `mod/**/*.cs` | Chat font is chosen solely by the per-message `LanguageCode` tag (no glyph fallback) — tag relayed/system messages by inferring script; and a render-suppressed server still LOADS `SmallFont.<lang>` via the chat MeasureString path ("Draw patched out" ≠ "no font loads") |
| `colocate-event-emit.md` | test `Containers/`, `Infrastructure/` | Emit state-transition events from inside the producer, not an outer coordinator |
| `debugging.md` | `mod/**/*.cs`, `tests/test-client/**` | `LogLevel.Error` in mod code is test poison — `\b(ERROR\|FATAL)\b` triggers cancellation; but the scan is SERVER-side only (`ServerContainer.cs:811`), so test-client Error is loud, not poison |
| `display-scaling.md` | `mod/JunimoServer/**`, `mod/JunimoServer.Shared/**` | To render Stardew into a small framebuffer, zoom out via Harmony-patched `desired*` getters (revert-proof) — don't poke the scale fields; `Game1.Update` reconciles them away and save-load clobbers the persisted `zoomLevel` |
| `docker-save-format-source-daemon.md` | `tests/JunimoServer.TestRunner/Distribution/**`, `e2e-tests.yml` | `docker save` blob format (compressed OCI vs uncompressed `layer.tar`) is set by the SOURCE daemon's image store, not the images; buildx driver doesn't change it — CI enables the store in-place on the ONE pre-installed daemon (don't reintroduce a second daemon; if you do, `set-host: true` is mandatory or `:local` 404s) |
| `docker-test-resources.md` | `tests/**/Containers`, `Helpers/Docker*`, `Helpers/ContainerStatsCollector.cs`, `Infrastructure` | Testcontainers patterns + mandatory `WithDockerEndpoint(host.EndpointConfig)` |
| `drain-before-consume-disposal.md` | `tests/JunimoServer.TestRunner/**`, test `Containers/**` | Drain producer streams explicitly before consumer disposal — `await using` ordering isn't enough |
| `event-catalog-no-inline-enums.md` | `Helpers/InfrastructureEventLog.cs`, `Helpers/SetupEventBus.cs`, `docs/developers/events-schema.md` | Don't list inline-string variants verbatim in the event catalog — reference enums via `cref` or point at the emitting class; verbatim lists silently drift |
| `ffmpeg-pixel-measurement.md` | `ContainerRecorder.cs`, `TestOverlay.cs`, `RenderingTests.cs`, `tools/.playground/recording-validator/**` | Measure ffmpeg-rendered pixels with per-column `crop=1:H` + rgb24, not a full-frame `format=gray` raw scan — the raw stream's stride drifts and reports phantom edges |
| `follow-true-created-state-eof.md` | test `Containers/**`, `Helpers/Docker*.cs` | `GetContainerLogsAsync(Follow=true)` returns immediate EOF for a `Created` (not yet running) container — retry on first-read EOF with no prior reads |
| `harmony-patch-reachability.md` | `mod/JunimoServer/**` | A Harmony patch's reachability is its registering constructor's reachability — `PasswordProtectionService` early-returns when no password, so its patches no-op on passwordless servers; put always-on patches in an unconditionally-constructed service |
| `host-automation.md` | `AlwaysOnServer/`, `HostAutomation/`, `Lobby/`, `CabinManager/` | Decompiled-first; `hasDedicatedHost = false`, `netReady` formula, festival repro caveat, draw-coupled FarmEvent completion, host farmhouse internal-only |
| `masterplayer-is-player-on-server.md` | `mod/JunimoServer/**` | `multiplayerMode = 2` on this server makes `IsMasterGame` always true, so `Game1.MasterPlayer` always resolves to `Game1.player` (same `Farmer`) — reject any bug/design that hinges on host-vs-master divergence |
| `minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md` | `tests/**/*.cs` | `docker exec` degrades ~24× under parallel load — minimize exec COUNT in hot paths (one in-shell loop, not N C# polls); cut diagnostic execs whose field has no consumer |
| `mod-game-thread-allocation.md` | `mod/**/*.cs` | Minimize per-tick/per-scan allocations on the game thread up front (reuse buffers, double-buffer + swap to prune a keyed map) — the stated-perf-constraint exception to `simplest-solution.md`, scoped to the hot path |
| `modern-docker.md` | `docker/modern/` | musl gotchas: pthread_shim, RunSynchronously deadlock, BlockOnUIThread can't be patched |
| `netdictionary-public-surface.md` | `mod/JunimoServer/**` | Mutate `NetDictionary` via public API, not `FieldDict.Remove/Add` |
| `netfield-revert-pattern.md` | `mod/JunimoServer/**` | Don't revert peer-replicated NetField writes inside `fieldChangeEvent` — interpolation makes Set a no-op |
| `not-dispatched-derivation.md` | `tests/JunimoServer.TestRunner/Rendering/Web/**` | `notDispatched = ExpectedTestCount - (Passed + Failed + Canceled + Skipped)` |
| `one-writer-per-artifact.md` | `tests/JunimoServer.TestRunner/**`, `tests/JunimoServer.Tests/Fixtures/**` | Two producers of the same artifact = silent schema drift — merge upstream state, not downstream files |
| `openapi-generator-reflection-invoke.md` | `tools/openapi-generator/**`, `OpenApiGenerator.cs` | `openapi-generator` calls `Generate` by reflection (`MethodInfo.Invoke`, fixed positional args) at Docker build time — an OPTIONAL param breaks the image build (Invoke ignores C# defaults) while `dotnet build` stays green; pass every arg explicitly |
| `provision-up-front-when-startup-exceeds-serviceable-tail.md` | `tests/JunimoServer.Tests/Infrastructure/**`, `tests/JunimoServer.TestRunner/**` | Reacting harder to tail contention is a churn trap when startup cost (~41s server boot) exceeds the window the new resource can serve — front-load the allocation at prestart instead |
| `recorder-anchor-first-frame.md` | `tests/**/Helpers/ContainerRecorder.cs`, `Helpers/Recording*.cs` | Seven load-bearing recorder invariants (timing flags, segment format, anchor source, phase-lock, extraction, filenames) — revert any and the bug it fixed returns; plus: verify any recorder claim against both the libx264 and NVENC `_useGpu` branches |
| `renovate-nuget-allowedversions-needs-semver.md` | `renovate.json` | Renovate `allowedVersions` silently no-ops under the default `nuget` scheme for prerelease-interleaved packages — add `versioning: semver`, use `=X.Y.Z`/`<X.0.0` (not a bare floor), and verify with `renovate --platform=local --dry-run=lookup` |
| `runner-side-artifact-writer.md` | `tests/JunimoServer.Tests/Fixtures`, `tests/JunimoServer.TestRunner/Rendering` | `summary.json` / `ctrf-report.json` are written by the runner, not the fixture |
| `runner-ui-pipeline-plumbing.md` | `SetupEventBus.cs`, `ContainerStatsCollector.cs`, `TestRunner/**`, test-ui types/store | Adding a field to a runner→UI event needs end-to-end plumbing — every hop is hand-written |
| `server-tps-headless.md` | `Env.cs`, `.env*`, `tests/**/*.cs`, `e2e-tests.yml` | `SERVER_TPS=5` is the proven-stable headless value (CI + `.env.test` run the full suite at it; `Env.cs` clamps min 1); the `.env.example` "20-30" prose is conservative docs, not the floor |
| `smapi-api-surface.md` | `mod/**/*.cs` | SMAPI's concrete `SemanticVersion` is in `StardewModdingAPI.Toolkit` (needs the using); ctor throws `FormatException`, `TryParse` doesn't; `Constants.GamePath`=string, `ApiVersion`=ISemanticVersion; `ModResolver` skips `MinimumApiVersion` when null |
| `startup-cold-start-measurement.md` | `GameManager/**`, `ApiService.cs`, `RenderingController.cs`, `ServerContainer.cs` | Boot-band cost is dominated by where the run executed (local FPS=0 vs VPS+SSH+recording) — check `host_id`/`SERVER_FPS` before quoting it; and these startup dead-ends (Xvnc/GraphicsDevice, live-generated OpenAPI spec, every-test-generates-a-new-game) are not perf wins |
| `test-broker-invariants.md` | `tests/JunimoServer.Tests/Infrastructure`, `Helpers` | KeepConnected capacity, exclusive deadlock, session liveness, polling budgets, server config keys, snapshot purity, SSH master `-E`/`LogLevel=INFO` + RST-drop capture |
| `test-overlay-pixel-contract.md` | `TestOverlay.cs`, `RenderingTests.cs` | Overlay geometry is a hidden pixel contract — different TFMs prevent sharing, grep both sides before editing |
| `test-state-setter-runs-engine-reconcile.md` | `mod/JunimoServer/Services/Api/**` | A `/test/*` state-setter must run the engine's reconciliation, not just poke `Game1` fields — `/time` needs `UpdateFromGame1()` to replicate; `/test/set_date` needs the new-day reset (`timeOfDay=600`, `whereIsTodaysFest=null`, `updateWeatherIcon()`) or stale festival weather crashes the loop |
| `tests-assert-via-http-api.md` | `tests/JunimoServer.Tests/**/*.cs` | E2E tests assert against the server HTTP API snapshot (`/cabins`, `/players`, `/farmhands`), never by reading mod events (diagnostics only); `/cabins` `IsAssigned` needs `isCustomized==true`, so a stuck-but-uncustomized slot still counts "available" |
| `test-timing.md` | `tests/**/*.cs` | Per-test overhead ≠ wall-clock cost; `queueDurationMs` is xUnit dispatch-wait, not a broker bottleneck |
| `test-ui-build.md` | `tests/test-ui/**` | Use `make build-test-ui` to verify test-ui builds (vue-tsc catches what plain vite build misses) |

## Public documentation

Reference material and runbooks for contributors live in the public VitePress site under [`docs/developers/`](../../docs/developers/) — game engine notes, mod architecture, test harness reference, manual-testing runbooks, and the test-failure debugging procedure. The Alpine/musl image reference lives at [`docs/admins/operations/modern-docker.md`](../../docs/admins/operations/modern-docker.md).
