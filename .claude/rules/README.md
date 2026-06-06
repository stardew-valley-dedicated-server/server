# `.claude/rules/` — Index

Project policy is layered. `CLAUDE.md` (L0) and `rules/universal/*.md` (L1) load every session. `rules/*.md` (L2) load when a `paths:` glob matches. Authoring guidance: `.claude/skills/extract-session-rules/SKILL.md`.

`paths:` semantics: gitignore-style globs, OR'd together. A rule loads when an edited path matches any entry.

## L1 — `universal/` (always-on)

| File | One-liner |
|---|---|
| `adversarial-review-split-findings.md` | Adversarial review must split findings (no OOS-as-cover) and must not collapse a valid recommendation along with weak framing |
| `answer-then-stop.md` | A question answered *by the transcript* gets a one-line answer, then stop (no pre-narration / re-confirm tool call / next-steps) — but world-facts still defer to `verify-claims` even when you feel sure |
| `git-workflow.md` | Project-specific git rules (no `git add .`, chained PRs, PR descriptions) |
| `holistic-or-explicit-todo.md` | Don't hedge with empty scaffolding — build the holistic solution or write a concrete TODO |
| `mirror-target-component-resolution.md` | A probe that detects another component's state must mirror that component's full resolution logic, not just the happy path |
| `move-not-delete.md` | Never delete generated/untracked files without moving them first |
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

## L2 — `rules/` (paths-gated)

| File | Triggers on | One-liner |
|---|---|---|
| `asynclocal-pitfalls.md` | `mod/`, `tests/`, `tools/` `.cs` | `AsyncLocal` doesn't flow across external pump boundaries — capture and rebind |
| `cabin-system.md` | `CabinManager/`, `GameLoader/`, `GameCreator/` | Cabin allocation invariants (startingCabins ordering, EnsureAtLeastXCabins, SlotSelectionGate, coordinates, master-only build) |
| `colocate-event-emit.md` | test `Containers/`, `Infrastructure/` | Emit state-transition events from inside the producer, not an outer coordinator |
| `debugging.md` | `mod/**/*.cs` | `LogLevel.Error` in mod code is test poison — `\b(ERROR\|FATAL)\b` triggers cancellation |
| `display-scaling.md` | `mod/JunimoServer/**`, `mod/JunimoServer.Shared/**` | To render Stardew into a small framebuffer, zoom out via Harmony-patched `desired*` getters (revert-proof) — don't poke the scale fields; `Game1.Update` reconciles them away and save-load clobbers the persisted `zoomLevel` |
| `docker-test-resources.md` | `tests/**/Containers`, `Helpers/Docker*`, `Helpers/ContainerStatsCollector.cs`, `Infrastructure` | Testcontainers patterns + mandatory `WithDockerEndpoint(host.EndpointConfig)` |
| `drain-before-consume-disposal.md` | `tests/JunimoServer.TestRunner/**`, test `Containers/**` | Drain producer streams explicitly before consumer disposal — `await using` ordering isn't enough |
| `event-catalog-no-inline-enums.md` | `Helpers/InfrastructureEventLog.cs`, `Helpers/SetupEventBus.cs`, `docs/developers/events-schema.md` | Don't list inline-string variants verbatim in the event catalog — reference enums via `cref` or point at the emitting class; verbatim lists silently drift |
| `ffmpeg-pixel-measurement.md` | `ContainerRecorder.cs`, `TestOverlay.cs`, `RenderingTests.cs`, `tools/.playground/recording-validator/**` | Measure ffmpeg-rendered pixels with per-column `crop=1:H` + rgb24, not a full-frame `format=gray` raw scan — the raw stream's stride drifts and reports phantom edges |
| `follow-true-created-state-eof.md` | test `Containers/**`, `Helpers/Docker*.cs` | `GetContainerLogsAsync(Follow=true)` returns immediate EOF for a `Created` (not yet running) container — retry on first-read EOF with no prior reads |
| `host-automation.md` | `AlwaysOnServer/`, `HostAutomation/`, `Lobby/`, `CabinManager/` | Decompiled-first; `hasDedicatedHost = false`, `netReady` formula, festival repro caveat |
| `minimize-exec-count-and-cut-unconsumed-diagnostic-execs.md` | `tests/**/*.cs` | `docker exec` degrades ~24× under parallel load — minimize exec COUNT in hot paths (one in-shell loop, not N C# polls); cut diagnostic execs whose field has no consumer |
| `modern-docker.md` | `docker/modern/` | musl gotchas: pthread_shim, RunSynchronously deadlock, BlockOnUIThread can't be patched |
| `netdictionary-public-surface.md` | `mod/JunimoServer/**` | Mutate `NetDictionary` via public API, not `FieldDict.Remove/Add` |
| `netfield-revert-pattern.md` | `mod/JunimoServer/**` | Don't revert peer-replicated NetField writes inside `fieldChangeEvent` — interpolation makes Set a no-op |
| `not-dispatched-derivation.md` | `tests/JunimoServer.TestRunner/Rendering/Web/**` | `notDispatched = ExpectedTestCount - (Passed + Failed + Canceled + Skipped)` |
| `one-writer-per-artifact.md` | `tests/JunimoServer.TestRunner/**`, `tests/JunimoServer.Tests/Fixtures/**` | Two producers of the same artifact = silent schema drift — merge upstream state, not downstream files |
| `recorder-anchor-first-frame.md` | `tests/**/Helpers/ContainerRecorder.cs`, `Helpers/Recording*.cs` | Seven load-bearing recorder invariants (timing flags, segment format, anchor source, phase-lock, extraction, filenames) — revert any and the bug it fixed returns |
| `recorder-verify-both-encoder-paths.md` | `Helpers/ContainerRecorder.cs`, `recorder-anchor-first-frame.md` | Recorder branches on `_useGpu` in two places (BuildEncoderArgs + BuildExtractCommand) with materially different keyframe/snap semantics — verify any property claim against both libx264 and NVENC paths |
| `renovate-nuget-allowedversions-needs-semver.md` | `renovate.json` | Renovate `allowedVersions` silently no-ops under the default `nuget` scheme for prerelease-interleaved packages — add `versioning: semver`, use `=X.Y.Z`/`<X.0.0` (not a bare floor), and verify with `renovate --platform=local --dry-run=lookup` |
| `runner-side-artifact-writer.md` | `tests/JunimoServer.Tests/Fixtures`, `tests/JunimoServer.TestRunner/Rendering` | `summary.json` / `ctrf-report.json` are written by the runner, not the fixture |
| `runner-ui-pipeline-plumbing.md` | `SetupEventBus.cs`, `ContainerStatsCollector.cs`, `TestRunner/**`, test-ui types/store | Adding a field to a runner→UI event needs end-to-end plumbing — every hop is hand-written |
| `test-broker-invariants.md` | `tests/JunimoServer.Tests/Infrastructure`, `Helpers` | KeepConnected capacity, exclusive deadlock, session liveness, polling budgets, server config keys, snapshot purity |
| `test-overlay-pixel-contract.md` | `TestOverlay.cs`, `RenderingTests.cs` | Overlay geometry is a hidden pixel contract — different TFMs prevent sharing, grep both sides before editing |
| `test-timing.md` | `tests/**/*.cs` | Per-test overhead ≠ wall-clock cost; `queueDurationMs` is xUnit dispatch-wait, not a broker bottleneck |
| `test-ui-build.md` | `tests/test-ui/**` | Use `make build-test-ui` to verify test-ui builds (vue-tsc catches what plain vite build misses) |

## Public documentation

Reference material and runbooks for contributors live in the public VitePress site under [`docs/developers/`](../../docs/developers/) — game engine notes, mod architecture, test harness reference, manual-testing runbooks, and the test-failure debugging procedure. The Alpine/musl image reference lives at [`docs/admins/operations/modern-docker.md`](../../docs/admins/operations/modern-docker.md).
