# Boot-phase instrumentation (for the server/client? mod and surrounding container startup)

## Context

A recent full E2E run (`TestResults/runs/2026-05-14T04-22-56Z_f0e43e8`) shows the broker reports `server_created.totalMs ≈ 77.7s` for a cold-start server with this split visible in `infrastructure.jsonl`:

| Phase                                                      | Source event(s)                                 | Duration |
| ---------------------------------------------------------- | ----------------------------------------------- | -------- |
| Docker `create+start` → container reports running          | `container_start_invoked` → `container_started` | ~41.6s   |
| Container running → mod API up + first tick                | `container_started` → `server_started`          | ~31.7s   |
| API up → `/health` returns `tickCount > 0, isFrozen=false` | `server_started` → `server_ready`               | ~2.1s    |

The 41.6s "Docker start" band is opaque — `container_started` actually waits on the in-container `/health` probe seeing a live game tick, so it really measures `docker create + docker start + s6-overlay services + startapp.sh init + SMAPI launch + mod Entry() + first game tick`. We don't know the breakdown.

The 31.7s "API up → world tickable" band has 5 existing `mod_phase` events (`mod_load_started`, `services_started`, `api_listener_ready`, `save_loaded`, `world_ready` — `ModEntry.cs:38–50, 68, 78, 82, 90, 108`), but no events for the sub-phases inside `RegisterServices()`, no event for the title-screen → save-decision gap, and no `Game1` tick milestones.

**Goal**: extend the existing `mod_phase` event family so a single test run produces enough breakdown to (1) answer "where do the 41s and 31s live?" today and (2) double as a metrics backplane that an OpenTelemetry exporter can read tomorrow without re-engineering the wire format.

**Non-goals**: building the OTel exporter itself (separate task), changing event transport, optimizing any of the phases (separate tasks, gated on this data).

## Approach

Three layers of additive instrumentation, each landing in `infrastructure.jsonl` via the existing `SDVD_EVENT` stdout transport and the existing `mod_phase` event name (extended schema). Plus one shell-script-side event from `startapp.sh` so we can attribute the pre-mod 41s band. Nothing about the wire format changes — only the set of phases emitted grows, and the `data` payload gains forward-compatible fields.

### Layer 1 — Mod-side boot phases (the 31.7s band)

Extend `ModEntry.cs` to emit `mod_phase` at the following additional checkpoints, in the order they happen on cold-start. Each event keeps the current `{ phase, phaseMs, bootMs }` schema and adds two forward-compatible fields described in §"Schema extension" below.

**Inside `RegisterServices()` and its helpers (file: `mod/JunimoServer/ModEntry.cs`)**

`Entry()` itself is only `ModEntry.cs:52-83`; it calls `RegisterServices()` at `:77`. The patch/settings inserts below land in `LoadServiceDependencies` (`:124-162`); `services_registered` lands in `RegisterServices` after `LoadServices` (`:118`); `services_built` lands in `StartServices` (`:176`). All run on the game thread synchronously from `Entry()`, so their `phaseMs` running-delta is meaningful.

| New phase                       | Insert after                                                                                                                  | Captures                                                                                                                                                       |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `harmony_baseline_patched`      | `HttpListenerFix.Apply` + `UpdateWhenCurrentLocation` patch in `LoadServiceDependencies` (line 142)                           | cost of the two always-on patches                                                                                                                              |
| `test_overlay_patched`          | `TestOverlay.Apply` / `ButtonTutorialSuppressor.Apply` block in `LoadServiceDependencies` (line 149), guarded by `Env.IsTest` | cost of test-only patches (will be missing on non-test runs — that's correct)                                                                                  |
| `settings_loaded`               | `ServerSettingsLoader` ctor + `SmapiLogConfig.SetVerboseLogging` in `LoadServiceDependencies` (line 158)                      | settings I/O                                                                                                                                                   |
| `services_registered`           | `LoadServices(services)` in `RegisterServices` (line 118) — _before_ `StartServices`                                          | assembly reflection scan only                                                                                                                                  |
| `services_built`                | `services.BuildServiceProvider()` in `StartServices` (line 176) — _before_ the per-service loop                               | DI graph construction                                                                                                                                          |
| (existing) `services_started`   | After per-service loop completes (current line 78)                                                                            | retained; now measures only the per-service `Entry()` loop because the build cost was split out                                                                |
| (existing) `api_listener_ready` | Current location (line 82)                                                                                                    | retained — name is slightly misleading because the HTTP listener actually starts in `ApiService.OnGameLaunched` (`ApiService.cs:1622–1635`), see Layer 2 below |

**Inside the per-service start loop (`StartServices`, file: `mod/JunimoServer/ModEntry.cs:184–215`)**

The loop already iterates services alphabetically by type name. Wrap the per-service `GetRequiredService` + `Entry()` call in a sub-stopwatch and emit one new event per service with the type name and elapsed ms. This is the highest-leverage addition: today we don't know whether 5 services take 1s each or one service takes 5s. New event name: keep `mod_phase` with `phase = "service_started"` and `data = { phase, phaseMs, bootMs, service = serviceName }`.

Rationale for not inventing a new `service_started` event name: per the events-schema doc, `phase` is the natural axis for sub-typing within a family. Adding a new top-level event name fragments the catalog; adding a phase value reuses the existing parser path. The forwarded line still has `event = "mod_phase"`; consumers filter on `data.phase` and `data.service`.

### Layer 2 — Mod-side post-`Entry` phases (the gap between `api_listener_ready` and `world_ready`)

The current `api_listener_ready` fires from `Entry()` but the actual HTTP listener doesn't open until `ApiService.OnGameLaunched` calls `StartServer()` (`ApiService.cs:1572–1635`). The band between mod-loaded → title-rendered → save-decision → save-loaded → first-tick → world-ready has no sub-phase signal today. Note: `GameManagerService` polls `ConditionallyStartGame()` once per second from `OneSecondUpdateTicked` (`mod/JunimoServer/Services/GameManager/GameManagerService.cs:77-86`), gated on a `_titleLaunched` latch flipped by the first `TitleMenu` render (`:88-94`). So "save decision" is a separate event from "title rendered" — they can be 0-N seconds apart depending on title-render latency.

All Layer-2 phases are emitted with `phaseMs = null` (absolute `bootMs` only). They fire from independent handlers (`ApiService.OnGameLaunched`, `ApiService.StartServer`, `GameManagerService.OnRenderedActiveMenu`, `GameManagerService.ConditionallyStartGame`, `GameLoaderService.LoadSave`, `GameCreatorService.CreateNewGame`, and the one-shot `UpdateTicked` handler) seconds apart, so a running-delta `phaseMs` would measure wall-clock idle between handlers, not work, and would skew every later delta. Consumers compute per-phase durations from the difference of consecutive `bootMs` values when they want them. These emitters must not write the shared `_previousPhaseMs` (game-thread-only; see §"Files modified") — they use the `phaseMs = null` overload. Add seven events:

| New phase               | Emitter location                                                                                                                                                                                                                      | Captures                                                                                                                                                                                                                |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `game_launched`         | `ApiService.OnGameLaunched` (`ApiService.cs:1572`), _before_ `StartServer()` runs                                                                                                                                                     | gap from `api_listener_ready` to SMAPI's `GameLaunched` — engine init cost                                                                                                                                              |
| `http_listener_bound`   | `ApiService.StartServer` after `_listener.Start()` (`ApiService.cs:1624`)                                                                                                                                                             | HTTP listener bind cost (sub-second expected, but instrumented for OTel)                                                                                                                                                |
| `title_menu_rendered`   | `GameManagerService.OnRenderedActiveMenu` (`mod/JunimoServer/Services/GameManager/GameManagerService.cs:88-94`), latched on the first frame where `_titleLaunched` flips true                                                         | Stardew title-screen reached — distinct from "save decision" because `OneSecondUpdateTicked` polls every 1s after this latch                                                                                            |
| `save_decision_made`    | `GameManagerService.ConditionallyStartGame` (`mod/JunimoServer/Services/GameManager/GameManagerService.cs:96-150`), at the load-vs-create branch (`:146-149`)                                                                         | which path was taken; `data.decision = "load" \| "create"`                                                                                                                                                              |
| `save_load_started`     | `GameLoaderService.LoadSave` before `SaveGame.Load(saveName)` (`mod/JunimoServer/Services/GameLoader/GameLoaderService.cs:40-70`)                                                                                                     | load-path duration as a first-class measurement                                                                                                                                                                         |
| `new_game_started`      | `GameCreatorService.CreateNewGame` (`mod/JunimoServer/Services/GameCreator/GameCreatorService.cs:56`) only — _not_ also in `CreateNewGameFromConfig` (`:34`), which delegates to `CreateNewGame`; emitting in both would double-count | create-path duration, symmetric to `save_load_started`. Required because most prestart cold-boots take the create path (no existing save), so without this the `save_decision_made → world_ready` band has no breakdown |
| `first_tick_after_save` | `Helper.Events.GameLoop.UpdateTicked` handler installed during `OnSaveLoaded`, latched on first invocation post-`SaveLoaded`                                                                                                          | first game-thread tick after save was loaded — closes the `save_loaded → world_ready` band that today is a black box                                                                                                    |

For the existing `save_loaded` and `world_ready` emits, no change.

`data.decision = "load" | "create"` rides on `save_decision_made`. The decision is binary in practice — `ConditionallyStartGame` only emits when the branch actually runs (after the `_titleLaunched && !_gameStarted` and `_pendingNewGameConfig == null` short-circuits clear), so there is no `"skip"` value.

Note: `save_decision_made` only fires on the default `HasLoadableSave()` load-vs-create branch (`GameManagerService.cs:146-149`). The API-driven `_pendingNewGameConfig` path (`:119-137`) and the `ForceNewDebugGame` path (`:139-144`) bypass this branch entirely and emit no `save_decision_made` — on those runs the phase is simply absent (its absence indicates a non-default create path was taken).

Note: `title_menu_rendered` is latched in `OnRenderedActiveMenu` (`GameManagerService.cs:88-94`), which may never fire on `SuppressDraw` runs (no menu render). To keep the phase observable there, also latch the emit at the `ConditionallyStartGame` fallback (`GameManagerService.cs:106-109`) where the title-launched state is otherwise detected; if that fallback is not used, document the phase's absence on `SuppressDraw` runs the same way `game_files_wait_started`'s absence is documented in Layer 3.

Note: `first_tick_after_save` assumes an `UpdateTicked` fires strictly between `SaveLoaded` and the first `DayStarted`. This SMAPI event ordering is not independently verified here — confirm it against one real `infrastructure.jsonl` (the phase should appear once, between `save_loaded` and `world_ready`) during the smoke run.

### Layer 3 — Pre-mod container init (the 41.6s band)

The s6-overlay supervisor + `startapp.sh` runs before the mod's `Entry()` is called. The script already structures init as discrete `init_*` functions (`docker/rootfs/startapp.sh:251–260`). Emit one `SDVD_EVENT` line per init function on completion, from the shell, using the same JSON envelope. The harness's `SimpleContainerLogStreamer` doesn't care what process wrote the line — only that the prefix matches.

Add a tiny helper in `startapp.sh` near the top:

```sh
emit_phase() {
  # $1 = phase name, $2 = elapsedMs (since script start)
  printf 'SDVD_EVENT {"ts":"%s","service":"server","event":"mod_phase","data":{"phase":"%s","bootMs":%s}}\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)" "$1" "$2"
}
```

Track a script-start epoch in millis (one `date +%s%3N` at top), emit after each `init_*` step:

| New phase                 | Emitter location in `startapp.sh`                                                                                                                                                                                                                  |
| ------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `container_init_started`  | top of script, after epoch capture                                                                                                                                                                                                                 |
| `time_synced`             | after `init_time_sync` (line 251)                                                                                                                                                                                                                  |
| `gui_ready`               | after `init_gui` (line 252)                                                                                                                                                                                                                        |
| `display_ready`           | after `init_xauthority`+`init_display_settings` (lines 253–254)                                                                                                                                                                                    |
| `game_files_wait_started` | inside `init_stardew` (`docker/rootfs/startapp.sh:141-144`), _only_ when the poll loop is entered (game files not yet populated by `steam-auth`). Absent on warm-volume runs where the symlink succeeds immediately — its absence is itself signal |
| `game_files_ready`        | after `init_stardew` completes (line 255)                                                                                                                                                                                                          |
| `steam_sdk_ready`         | after `init_steam_sdk` (line 256)                                                                                                                                                                                                                  |
| `smapi_installed`         | after `init_smapi` (line 257) — named `smapi_installed` (not `smapi_ready`) to mean "SMAPI installed on disk," not "SMAPI started running"                                                                                                         |
| `mods_ready`              | after `init_mods` (line 259)                                                                                                                                                                                                                       |
| `permissions_set`         | after `init_permissions` (line 260) — this step runs `chmod -R`/`chown -R` over the whole game tree, expected to be multi-second, so it is worth its own phase                                                                                     |
| `smapi_invoked`           | immediately before the `script -q -f --return -c …` invocation (line 278) — last shell event before SMAPI's runtime takes over                                                                                                                     |

`init_patch_dll` (line 258) is commented out in the script, so no emit is added for it; if it is ever re-enabled, add a `dll_patched` phase after it.

These shell-emitted events are intentionally distinct from the C# `mod_phase` events but use the same wire format so they merge into one timeline in `infrastructure.jsonl`. The first C# event (`mod_load_started`) starts the C# stopwatch; comparing its `ts` to `smapi_invoked.ts` gives you the SMAPI-cold-start band (CLR JIT + SMAPI mod-scan + SMAPI's own startup work) — a phase that is otherwise unmeasurable from inside C# because the SMAPI source isn't in this repo and we can't emit from inside its scanner.

### Schema extension: forward-compatible payload fields

The current `mod_phase` payload is `{ phase, phaseMs, bootMs }`. Add two optional fields, both `null`/absent by default, populated only where they apply:

```json
{
    "phase": "service_started",
    "phaseMs": 412,
    "bootMs": 1733,
    "service": "ApiService", // only on service_started phases
    "decision": "load" // only on save_decision_made
}
```

Per the schema-versioning policy in `docs/developers/events-schema.md:94-98`, **additive optional fields do not bump schemaVersion**. No version bump needed.

### Why this shape works for OTel later

OpenTelemetry's natural model is one `Span` per phase with `bootMs` = span start (relative to a root span anchored at `container_init_started`) and `phaseMs` = span duration. The bash-emitted root + C# child phases form a single trace. The forward-compatible `data` payload becomes span attributes. The `service` field maps to OTel `service.name` resource attribute. A future exporter is a one-way transform from `infrastructure.jsonl` rows where `event == "mod_phase"` into OTLP spans; no changes to the emit sites needed.

What this design intentionally avoids: shipping `System.Diagnostics.Metrics` / `ActivitySource` in the mod _now_. The mod runs inside SMAPI's pinned net6.0 assembly load context; adding OTel SDK packages risks dependency drift with SMAPI's own deps. Keeping the wire as JSONL + post-run transform is the lower-risk version of "OTel readiness."

## Files modified

- `mod/JunimoServer/ModEntry.cs` — add 7 new `EmitModPhase` call sites (`harmony_baseline_patched`, `test_overlay_patched`, `settings_loaded`, `services_registered`, `services_built`, the per-service `service_started` loop emit, and `first_tick_after_save`); refactor `EmitModPhase` to accept an optional `data` object so the `service`-name and `decision` fields can ride along without inventing new event names. The refactor also gives `EmitModPhase` an overload that emits with `phaseMs = null` (absolute `bootMs` only) for callers that do not run sequentially on the game thread — the running-delta `phaseMs = bootMs - _previousPhaseMs` and the shared `_previousPhaseMs` write are only valid for game-thread-synchronous Layer-1 callers; an off-thread or out-of-order emit that wrote `_previousPhaseMs` would corrupt every subsequent delta. Document this game-thread-only invariant at the `_previousPhaseMs` field.
- `mod/JunimoServer/Services/Diagnostics/ModEventLog.cs` — no change to `Emit`; possibly extract a helper for the phase-payload shape so the C# and shell emit-shapes stay in lockstep.
- `mod/JunimoServer/Services/Api/ApiService.cs` — 2 new `ModEventLog.Emit("mod_phase", …)` calls in `OnGameLaunched` and `StartServer`.
- `mod/JunimoServer/Services/GameManager/GameManagerService.cs` — 2 emits: `title_menu_rendered` in `OnRenderedActiveMenu` (latched), and `save_decision_made` in `ConditionallyStartGame` with `data.decision`.
- `mod/JunimoServer/Services/GameLoader/GameLoaderService.cs` — 1 emit in `LoadSave` (`save_load_started`).
- `mod/JunimoServer/Services/GameCreator/GameCreatorService.cs` — 1 emit at the entry of `CreateNewGame`/`CreateNewGameFromConfig` (`new_game_started`).
- `mod/JunimoServer/ModEntry.cs` — `OnSaveLoaded` installs a one-shot `GameLoop.UpdateTicked` handler that emits `first_tick_after_save` on first invocation, then unsubscribes.
- `docker/rootfs/startapp.sh` — add `emit_phase` helper + 11 emit calls (`container_init_started`, `time_synced`, `gui_ready`, `display_ready`, `game_files_ready`, `steam_sdk_ready`, `smapi_installed`, `mods_ready`, `permissions_set`, `smapi_invoked`, plus the conditional `game_files_wait_started` inside the poll loop). `init_patch_dll` is commented out, so it gets no emit.
- `docs/developers/events-schema.md` (and/or the emitter's own catalog) — there is no literal `| mod_phase |` table row in `docs/`; `events-schema.md:32` points to each emitter's own catalog as the source of truth for its `data.phase` values. Confirm where the `mod_phase` catalog actually lives before editing, then add the new phase names and the optional `service` / `decision` fields there. The schema-versioning conclusion still holds: additive optional fields do not bump `schemaVersion` (`events-schema.md:94-98`), so no version bump.

## Files NOT modified (out of scope)

- `tests/JunimoServer.Tests/Containers/SimpleContainerLogStreamer.cs` — already forwards any `SDVD_EVENT` line; no parser change needed (already verified: `TryAnnotateModPhase` only special-cases the `mod_phase` event name for an annotation side-effect, but the forwarding path is generic).
- `tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs` — already accepts forwarded lines verbatim via `ForwardRaw`.
- `tests/JunimoServer.Tests/Infrastructure/TestResourceBroker.EmitModPhaseAnnotation` — still resolves the annotation by `data.phase`; the new phases automatically appear as annotations on running tests.

## Verification

Per `.claude/rules/universal/runtime-post-conditions-are-gates.md`, post-conditions are runtime checks, not just "it compiles." Each post-condition must be exercised on a real run before declaring done.

1. **Build clean**: `dotnet build mod/JunimoServer/JunimoServer.csproj` succeeds.
2. **Image rebuild lands the new startapp.sh**: `make build-server`, then `docker create sdvd/server:local && docker cp <id>:/startapp.sh -` and confirm the new `emit_phase` helper is in the artifact (per `verify-edit-landed-in-artifact.md` — startapp.sh is a `COPY docker/rootfs/` consumer, but a sanity check is cheap).
3. **Single-test smoke run**: run any one fast test (`make test FILTER=ServerApiTests.GetOpenApiSpec_ReturnsValidJson`) and confirm `infrastructure.jsonl` contains a `mod_phase` event for each new phase listed above. Use:
    ```bash
    # Sort by `ts`, not `bootMs`: the C# `_bootStopwatch` starts at `Entry()`
    # (`ModEntry.cs:54`) while the shell stopwatch starts at script top — two
    # different zeros. Sorting the merged timeline by `bootMs` would interleave
    # the two clocks wrong. `ts` is the single shared wall-clock axis (matches
    # the "stitch via ts" note in step 5).
    jq -r 'select(.event=="mod_phase") | "\(.ts)\t\(.data.bootMs)\t\(.data.phase)\t\(.data.service // "")"' \
      TestResults/runs/<latest>/diagnostics/infrastructure.jsonl | sort
    ```
    Expect to see, in order: `container_init_started`, `time_synced`, `gui_ready`, `display_ready`, (`game_files_wait_started` if cold-volume), `game_files_ready`, `steam_sdk_ready`, `smapi_installed`, `mods_ready`, `permissions_set`, `smapi_invoked`, `mod_load_started`, `harmony_baseline_patched`, `test_overlay_patched` (test-mode only), `settings_loaded`, `services_registered`, `services_built`, (N × `service_started` rows with `data.service`), `services_started`, `api_listener_ready`, `game_launched`, `http_listener_bound`, `title_menu_rendered` (absent on `SuppressDraw` runs unless latched at the `ConditionallyStartGame` fallback), `save_decision_made` (with `data.decision`; absent on API/debug create paths), (`save_load_started` _or_ `new_game_started`), `save_loaded`, `first_tick_after_save`, `world_ready`.
4. **No event drops, no malformed lines**: confirm `infrastructure.jsonl` parses end-to-end (`jq -c . infrastructure.jsonl > /dev/null` exits zero) and that no `[ModEventLog] emit failed` lines appear on the container's stderr (`TestResults/runs/<latest>/containers/server-*/container.log`).
5. **`bootMs` monotonicity**: the post-`smapi_invoked` C# phases should have C# stopwatch `bootMs` < shell-side `bootMs` (C# stopwatch starts at `Entry()`, shell stopwatch starts at script top). The two timelines stitch via `ts`. Confirm with:
    ```bash
    jq -c 'select(.event=="mod_phase")' …/infrastructure.jsonl
    ```
    manually scan: phases should be monotonic by `ts`.
6. **Full-suite delta check**: re-run the most recent comparable full suite (`make test`) and confirm `server_created.totalMs` is within ±5% of the baseline (596s ± 30s). The new emits are <10/second and each is one `printf` or `Console.WriteLine` — overhead should be in the noise.
7. **No `LogLevel.Error` triggered by new code paths** (`debugging.md`): the emits use `ModEventLog.Emit`, not `Monitor.Log` at Error level. Verify no test cancellations due to error-regex matches by searching the run output for `error_block`.

## Risks & mitigations

- **Shell-side stdout interleaving**: `startapp.sh` runs before the `script -q -f` PTY wrapper that captures SMAPI's stdout. Lines emitted by `emit_phase` _before_ `wait $SMAPI_PID` go to the container's stdout directly, which Docker captures and the harness streams the same way as SMAPI output. Verified: the `forwardedVia` decoration is set by `SimpleContainerLogStreamer` based on container label, not parser context — both bash-era and mod-era lines land correctly tagged. No mitigation needed.
- **Game-tick reading off-thread**: `tickMs` is intentionally omitted from boot-phase events. `Game1.ticks` is unsafe to read from `Entry()` (game thread not yet running) — per `ModEventLog.cs:43`, `tickMs` is caller-supplied for exactly this reason. Don't add it.
- **`PasswordProtectionService` ctor heavy work**: per the exploration, `PasswordProtectionService` applies 5 Harmony patches in its constructor (`PasswordProtectionService.cs:84-120`). Layer 1's `service_started` phase per-service-name captures this without special-casing — if `PasswordProtectionService` dominates the `services_started` band, the per-service breakdown surfaces it.
- **Schema rot from drift between C# and shell emit shapes**: both sides emit the same JSON envelope by hand. To prevent drift, the events-schema doc gets the new phases listed in the same change; the shell helper is short enough that a code review catches divergence.
