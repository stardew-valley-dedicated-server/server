# Infrastructure-log Shared Typed Schema

## Context

`tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs` writes `infrastructure.jsonl`, the post-mortem record for every test run. Per `docs/developers/events-schema.md` (the authoritative wire contract) it accepts events from four producers:

| `service`      | emitter                  | file                                                         | TFM     | role                                                                                                                                     |
| -------------- | ------------------------ | ------------------------------------------------------------ | ------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| `test-harness` | `InfrastructureEventLog` | `tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs` | net10.0 | direct writer (also linked into `JunimoServer.TestRunner` Exe; runner writes to `parent-infrastructure.jsonl` to avoid racing the child) |
| `server`       | `ModEventLog`            | `mod/JunimoServer/Services/Diagnostics/ModEventLog.cs`       | net6.0  | `SDVD_EVENT` over stdout → forwarded                                                                                                     |
| `test-client`  | `ClientEventLog`         | `tests/test-client/Diagnostics/ClientEventLog.cs`            | net6.0  | `SDVD_EVENT` over stdout → forwarded                                                                                                     |
| `steam-auth`   | `Logger.LogEvent`        | `tools/steam-service/Logger.cs`                              | net10.0 | `SDVD_EVENT` over stdout → forwarded                                                                                                     |

`Containers.SimpleContainerLogStreamer.TryForwardSdvdEvent` strips the `SDVD_EVENT ` prefix and hands the payload to `InfrastructureEventLog.ForwardRaw`, which parses it as a `JsonObject`, sets `forwardedVia`, and re-serializes the line. Today no producer types its payload — every emit site uses an anonymous-typed object literal serialized via `System.Text.Json` (`DiagnosticEmitJson.Serialize` on the test side; producer-local `JsonSerializerOptions` instances on the three sidecars). Verified call counts (2026-05-09):

- **159** `InfrastructureEventLog.{Emit,EmitWait,ForwardRaw}` callsites in `tests/JunimoServer.Tests/`
- **30** `InfrastructureEventLog.{Emit,EmitWait,ForwardRaw}` callsites in `tests/JunimoServer.TestRunner/` (same class, different process — writes to the parent log)
- **65** `ModEventLog.Emit` callsites in `mod/JunimoServer/`
- **9** `ClientEventLog.Emit` callsites in `tests/test-client/`
- **14** `Logger.LogEvent` callsites in `tools/steam-service/`
- **≥150** distinct event names across all four producers (literal-string first arg; the inline catalog in `InfrastructureEventLog.cs:18-260` is the human-readable list)

Field shape lives at the call site. Adding, renaming, or restructuring a payload field is invisible to the compiler — the only enforcement is the inline catalog and `events-schema.md`.

The IPC pipe event family (`SetupEventBus` → named pipe → `EventDispatcher` → renderer) already has typed records under `tests/JunimoServer.Tests/Schema/Events/`. Those events use a flat envelope `{event, ...payload}`; infrastructure events use a wrapped envelope `{ts, runMs, requestId, service, test, phase, tickMs, event, data}` (full spec: `docs/developers/events-schema.md`). The two pipelines stay wire-incompatible by design — the IPC pipe is flat for `EventDispatcher` parser simplicity, the wrapped envelope carries cross-process correlation fields. The `Schema/` directory, `IRendererEvent` marker, `EventNames` constants, and `Schema/Json/DiagnosticEmitJson.cs` serializer already exist and are reused below.

This plan adds typed records for the infrastructure-log schema under `tests/JunimoServer.Tests/Schema/InfrastructureEvents/`, alongside the existing `Schema/Events/` IPC records.

## Why now

- **Post-mortem readability.** The diagnostic log is what we open when production tests fail (`docs/developers/testing/test-failure-runbook.md`). A schema lets jq queries and the failure runbook consume the log without grep-and-pray.
- **Drift prevention.** ~260 anonymous-typed callsites across four producers are a continuous source of silent schema drift (e.g. one site renames `host_id` → `hostId`, downstream filters miss both for a quarter).
- **Multi-producer asymmetry.** Three of the four services emit via stdout forwarding; the receiver-side test project is the only place that can compile-check the wire shape across all of them.

## Goal

A single source of truth for every event name and field shape that lands in `infrastructure.jsonl`. Producer-side serialization goes through typed records (with byte-for-byte wire compatibility). The grep-and-pray phase ends. `events-schema.md` continues to be the human-readable wire contract; the C# records are the machine-checkable mirror.

## Design decisions (locked)

- **Two envelopes, not one.** IPC events keep their flat `{event, ...payload}` shape; infrastructure events keep their wrapped `{ts, runMs, requestId, service, test, phase, tickMs, event, data}` shape. Records describe the _payload_ only; the wrapping is the writer's job. Zero wire-format change. The two pipelines serve different consumers (live UI vs post-mortem) and unifying them would force a breaking change on either the renderer + TS UI or every existing `infrastructure.jsonl` jq filter and the failure runbook.
- **Shared schema project for the four producers.** Records live in a multi-targeted (`net6.0;net10.0`) schema project so the test receiver, the two SMAPI mods, and the steam-service sidecar all reference the _same_ records. No parity tests, no per-producer mirroring, no silent drift on field additions. Schema project name: `JunimoServer.Schema` (new project, not folded into `JunimoServer.Shared` — the latter is mod-adjacent and already pulls in `Pathoschild.Stardew.ModBuildConfig` + the Stardew assembly reference, which the test and steam-service projects must not inherit).
- **`ForwardRaw` stays opaque on the wire path.** It continues to parse the line as a `JsonObject`, set `forwardedVia`, and re-serialize. No deserialize-into-record at runtime. An integration test deserializes archived `infrastructure.jsonl` lines via the records as a CI drift check; runtime correctness stays decoupled from schema validation.
- **Big-bang migration in one PR.** Land the records, the typed `Emit<T>` overloads, every callsite conversion, and the deletion of the old `Emit(string, object?)` API in a single change. No half-typed intermediate state. The reviewer experience is one large diff; the runtime experience is one cutover.
- **Per-stage records for events with discriminated payloads.** Several event names have heterogeneous payloads keyed off a `stage` / `reason` / `mode` field today — `recording_clip_failed` has 7+ shapes, `recording_per_test_clip_skipped` ~8, `recording_start_failed`, `screenshot_failed`, `tunnel_forward_failed`, `auth_steam_lobby_create_failed`, `auth_login_attempted` similarly. Each stage becomes its own record (`RecordingClipFailedSegmentDiscoveryEvent`, `RecordingClipFailedNoSegmentsEvent`, …) sharing the wire `event` name. The lookup goes `(eventName, discriminator) → record`, not just `eventName → record`. Polymorphic `[JsonPolymorphic]` is rejected — it would write a `$type` discriminator and break wire compat. Union records with all-nullable fields are rejected — they put the codebase back to grep-and-pray inside the record. The cost is more record types; the benefit is the schema actually constrains what each stage may carry.
- **`extras` and other open dictionaries become explicit, never `IDictionary<string, object?>`.** `FailureContext.DumpAsync` accepts an arbitrary `extras` dict that flows into `failure_context.data.extras`. Each call site passes its own field set (uid, farmer name, attempt). The fix is to type each call site: `FailureContextPlayerVisibilityExtras { Uid, Attempt, … }`, `FailureContextJoinExtras { ... }`, etc. 11 call sites in `tests/` today. If a call site genuinely needs free-form K/V, use a typed `Dictionary<string, string>` with documented semantics; never `object?`. Same rule applies to any other emit site that smuggles through a dict today (audit during the catalog walk).
- **Event-name discriminator is an instance property, not a `static abstract`.** Every `IInfrastructureEvent` declares `string EventName { get; }` as an instance property; records implement it as `public string EventName => InfrastructureEventNames.X;`. Static abstract members on interfaces require net7.0+ and the schema project multi-targets net6.0. The instance-property choice is final, not a fallback — it changes the `Emit<T>` writer signature (`evt.EventName` vs `T.EventName`), and that decision must be made before the writer is sketched.
- **Producer-thread serialization preserves `AsyncLocal` capture.** `Emit<T>(T evt)` serializes the full envelope (composed around the typed payload) into a `string` on the **caller's thread**, then enqueues the string into the `AsyncJsonlWriter` channel. Envelope-field reads (`CorrelationContext.Current`, `TestIdentityContext.Current`, `RunMetadata.GetRunMs()`) happen during that serialize step, so they capture the emit-time value before any thread hop. Anything else regresses the bug `asynclocal-pitfalls.md` was written about — `requestId` going null on every event of an emit site that crosses a queue boundary. The pre-init buffer (`_preInitBuffer` at `InfrastructureEventLog.cs:287`) keeps stashing already-serialized strings; the typed path matches that contract.

## What lands

### Layout

```
JunimoServer.Schema/                        (NEW project — multi-targets net6.0;net10.0)
  IInfrastructureEvent.cs                   (marker interface; payload only, no envelope fields)
  InfrastructureEventNames.cs               (const string per event name)
  Polling/
    WaitEvent.cs                            (one record for event="wait"; data carries name/phase/durationMs/snapshot/error)
    PollCompletedEvent.cs
    LongPollCompletedEvent.cs
  Recording/
    RecordingLifecycleEvents.cs             (recording_started, recording_stopped, recording_container_dead, recording_full_*)
    RecordingClipFailedEvents.cs            (one record per stage: WrongState, ZeroDuration, SegmentDiscoveryFailed, NoSegments, NoCoverage, FfmpegFailed, RetrieveFailed, Exception)
    RecordingClipSkippedEvents.cs           (one record per reason: ArtifactsOptedOut, TestTooShortForFps, RetentionPassed, EndTimeMissing, RecorderNeverStarted, RecorderMissing, ZeroDuration, ContainerDead, ExtractionFailed, FinalizeDeferredFailed)
    RecordingClipExtractedEvent.cs          (recording_clip_extracted, recording_per_test_clip)
  Broker/
    CapacityEvents.cs
    ServerLifecycleEvents.cs
    ClientPoolEvents.cs
    ExclusivityEvents.cs
  Http/
    HttpEvents.cs                           (http_request, http_503_retry, http_served)
  Mod/                                      (records emitted by ModEventLog — server-mod side)
    GameStateEvents.cs                      (peer_*, farmhand_*, ready_check_*, otherfarmers_*, cabin_*, snapshot_*)
    LobbyEvents.cs
    AuthEvents.cs                           (auth_login_*, auth_warp_*, auth_steam_*, auth_galaxy_*, auth_server_steamid_*)
    SteamGameServerEvents.cs                (steam_p2p_*, steam_callback_error)
    RoleEvents.cs
    WatchdogEvents.cs                       (game_thread_stall_*, exception_swallowed, console_command_invoked)
  TestClient/                               (records emitted by ClientEventLog — test-client mod side)
    ClientEvents.cs                         (client_chat_sent, client_health_stall_*)
  SteamAuth/                                (records emitted by Logger.LogEvent — sidecar)
    SteamAuthEvents.cs                      (account_logged_in/off/failed, app_ticket_*)
  Distribution/                             (records emitted by JunimoServer.TestRunner)
    ImageDistributionEvents.cs              (image_transfer_*, image_skip_match, image_build_*)
    GameDataDistributionEvents.cs           (game_data_transfer_*, game_data_skip_populated, helper_image_pull_*)
    SshEvents.cs                            (ssh_*, tunnel_forward_*)
  Lifecycle/
    DiscoveryEvents.cs                      (config_discovery_completed, run_aborted, setup_ipc_*, coordinator_address_resolved)
    ContainerEvents.cs                      (container_started/stopped/start_failed/oom_killed, docker_preflight*)
    HealthEvents.cs                         (health.*, server_*, screenshot_failed)
    SteamAccountEvents.cs                   (steam_account_*)
  Json/
    InfrastructureEnvelopeWriter.cs         (composes the wrapped envelope around an IInfrastructureEvent payload)

tests/JunimoServer.Tests/Schema/            (existing — IPC pipe events; unchanged by this plan)
  Events/, EventNames.cs, IRendererEvent.cs, Json/DiagnosticEmitJson.cs, ...

tests/JunimoServer.Tests/Schema/__fixtures__/infrastructure-events/   (NEW — committed JSON snapshots for serialization tests)
```

The `JunimoServer.Schema` project carries no dependencies beyond `System.Text.Json` (in-box on net6.0+). It declares records and event-name constants only — no I/O, no DI, no logging.

### Project references

- `JunimoServer.Schema` — new project, no project references.
- `mod/JunimoServer/JunimoServer.csproj` — add `ProjectReference` to `JunimoServer.Schema`.
- `tests/test-client/JunimoTestClient.csproj` — add `ProjectReference` to `JunimoServer.Schema`.
- `tools/steam-service/SteamService.csproj` — add `ProjectReference` to `JunimoServer.Schema` (first in-repo project reference for this sidecar).
- `tests/JunimoServer.Tests/JunimoServer.Tests.csproj` — add `ProjectReference` to `JunimoServer.Schema`.
- `JunimoServer.Shared` is **not** touched. Distinct concern (mod runtime helpers vs wire schema).

### Writer changes

- `InfrastructureEventLog.Emit<T>(T evt) where T : IInfrastructureEvent` — the typed overload becomes the only emit API. The existing `Emit(string, object?)` and `EmitWait(WaitName, WaitPhase, …)` are deleted in the same PR; every callsite migrates.
- `IInfrastructureEvent` declares `string EventName { get; }`. Each record implements `public string EventName => InfrastructureEventNames.X;`. The writer reads `evt.EventName` (one virtual dispatch per emit, irrelevant on a microsecond emit path).
- Envelope composition stays in `InfrastructureEventLog` (it owns the `ts` / `runMs` / `requestId` / `test` / `phase` capture sources from `RunMetadata`, `CorrelationContext`, `TestIdentityContext`). The new `InfrastructureEnvelopeWriter` in the Schema project exposes a pure `Write(Utf8JsonWriter, envelope-fields, T payload)` method so each producer composes its own envelope without duplicating field-order logic.
- **Serialization runs on the producer thread.** `Emit<T>` reads ambient context (`AsyncLocal` sources above), serializes the envelope into a `string`, then enqueues the string into the `AsyncJsonlWriter` channel. The writer thread never touches `AsyncLocal`. Pre-init buffer continues to stash already-serialized strings (`_preInitBuffer` at `InfrastructureEventLog.cs:287`); the typed path matches that contract. Diverging from this regresses the bug `.claude/rules/asynclocal-pitfalls.md` was written about.
- `ModEventLog`, `ClientEventLog`, and `Logger.LogEvent` get the same `Emit<T>` overload, sharing `InfrastructureEnvelopeWriter`. Each producer keeps its own envelope-field sources (`ModRequestContext`, `ClientRequestContext`, `SidecarRequestContext`). All three already serialize on the producer thread today (per their current `Emit` bodies); preserve that.
- `ForwardRaw` unchanged on the wire path. Stays opaque.
- The `WaitName` enum (`tests/JunimoServer.Tests/Helpers/WaitName.cs`) and `WaitPhase` enum (`InfrastructureEventLog.cs:649`) move into the Schema project alongside `WaitEvent`.
- The inline event catalog in `InfrastructureEventLog.cs:18-260` shrinks in the same PR to a one-line pointer at `events-schema.md` + the records — its content is now encoded in the records themselves.

### Tests

- **Per-record serialization snapshot tests.** One assertion per record asserts the serialized payload matches a committed JSON snapshot under `tests/JunimoServer.Tests/Schema/__fixtures__/infrastructure-events/`. Fixtures change deliberately. The tests are pure-serialization (no broker, no containers, no emit-time `AsyncLocal` reads — construct the record literally, serialize, compare). They depart from CLAUDE.md's "E2E-only" rule for `tests/JunimoServer.Tests/`; this is the only deviation, and it's narrow because the schema project itself has no I/O.
- **Drift-detector test against a frozen fixture.** Commits a representative `infrastructure.jsonl` slice (≤1 MB) under `tests/JunimoServer.Tests/Schema/__fixtures__/drift-detector/` — covering one passing and one failing test, every event name in the catalog, every `stage`/`reason` discriminator. The test deserializes every line via the records with `JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }` (available on net8.0+; the test runs on net10.0). Failures: unknown event name, unknown property name, missing required (non-nullable) constructor parameter. Re-generate the fixture deliberately when the schema changes; the diff in the PR doubles as a wire-shape changelog. **Do not** read from `TestResults/runs/<latest>/` — that's a "whatever the dev last ran" path that flakes on CI ordering and creates a chicken-and-egg with new events introduced by the same PR.
- **Byte-equality smoke test (the real safety net).** Before merging, run `make test FILTER=ServerApiTests` on `master` and capture `infrastructure.jsonl`; run the same on the PR branch and capture again. After normalizing volatile fields (`ts`, `runMs`, `requestId`, `instanceId` UUIDs, host-specific paths, `pid`, free-form `message:`, durations), the two files must be **line-for-line equal in field order, field names, and field presence**. Any drift is a wire-format regression. Script lives in `tools/.playground/jsonl-diff/` (new). Without this, a field-order regression in `InfrastructureEnvelopeWriter` corrupts every line of every producer and the snapshot tests miss it (snapshots cover individual records, not the envelope around them).
- **`docs/developers/events-schema.md` updates** land in the same PR as the record changes; the spec stays authoritative.

## Risks

- **Breaking `make test-events` and runbook jq filters.** Field-name compatibility is load-bearing — every existing post-mortem tool reads camelCase field names from the on-disk log (`docs/developers/testing/test-failure-runbook.md` step 4 cites `jq` queries by field name). Records must serialize with `[JsonPropertyName]` decoration where the C# property name differs from the wire form, and respect the global camelCase + `WhenWritingNull` policy from `DiagnosticEmitJson`. The byte-equality smoke test is the catch — per-record snapshots cover individual payloads but not the envelope-around-them.
- **One writer touches every line.** Big-bang means a single bug in `InfrastructureEnvelopeWriter` (field order, missing field, wrong serializer options) silently corrupts every event of every producer. The byte-equality smoke test exists for exactly this; treat its run as a merge gate, not a nice-to-have.
- **`System.Text.Json` version on net6.0.** `JunimoServer.Schema` multi-targets `net6.0;net10.0`. The in-box `System.Text.Json` on net6.0 lacks features the test side uses (`UnmappedMemberHandling.Disallow` is .NET 8+, `JsonPolymorphic` is .NET 7+). The schema project itself uses neither — it only declares records and constants. Producer-side serialization on net6.0 (mod, test-client) stays on the in-box version. Do not `PackageReference` a newer `System.Text.Json` into the mod project just for the schema — SMAPI assembly-load behaviour around BCL replacement isn't worth the risk. The strict-mode opt-in is test-side only and runs on net10.0 anyway.
- **SMAPI ModBuildConfig DLL deployment for the Schema project.** `mod/JunimoServer/JunimoServer.csproj` uses `Pathoschild.Stardew.ModBuildConfig`, which deploys mod outputs into the SMAPI mods folder. Adding a `ProjectReference` to `JunimoServer.Schema` produces `JunimoServer.Schema.dll` that ModBuildConfig must copy alongside `JunimoServer.dll`. If it doesn't, SMAPI fails to load the mod the first time it touches `IInfrastructureEvent`. **Verify**: build the mod once after wiring the reference; confirm `JunimoServer.Schema.dll` lands in the SMAPI deploy folder.
- **Three Dockerfiles selectively COPY only the producer's own subtree — the new `JunimoServer.Schema/` is invisible to all of them.** The build contexts `dotnet restore` against today copy only the producer's own files: `docker/Dockerfile` (server image) does `COPY ./mod /src/mod`; `docker/Dockerfile.test-client` does `COPY ./tests/test-client /src/tests/test-client` + `COPY ./mod/JunimoServer.Shared /src/mod/JunimoServer.Shared`; `docker/Dockerfile` stage 1 (`steam-service-builder`) and standalone `tools/steam-service/Dockerfile` both copy only `./tools/steam-service/`. Adding a `ProjectReference` to `JunimoServer.Schema` makes `dotnet restore` fail at image-build time with `Project not found` before publish runs. Fix: each of the three Dockerfiles needs an extra `COPY JunimoServer.Schema/ /src/JunimoServer.Schema/` (and the corresponding `.csproj` in the restore-only stage). The standalone `tools/steam-service/Dockerfile` additionally needs its build context widened to repo root and its `Makefile` invocation updated. Smoke-test each image build after wiring the reference; failure surfaces at `docker build`, not at runtime.
- **Schema-policy drift between sidecars.** All three forwarded producers use their own `JsonSerializerOptions` instance (camelCase + `WhenWritingNull`). Today the shape is aligned with `DiagnosticEmitJson`'s policy by convention, not by sharing the options object. Records carry their own `[JsonPropertyName]` attributes so they serialize correctly under either options object — producer-side options stay producer-local.
- **Forwarded-producer `Console.Out` is not the CLAUDE.md prohibition.** `ModEventLog`, `ClientEventLog`, and `Logger.LogEvent` all write `SDVD_EVENT` lines via `Console.Out.WriteLine`. CLAUDE.md's "do NOT write to stdout in test assemblies" applies to xUnit v3 test assemblies (`tests/JunimoServer.Tests`), not SMAPI mods or sidecar binaries. `tests/test-client` is a SMAPI mod despite living under `tests/` — the typed `ClientEventLog.Emit<T>` overload is allowed.
- **Big-bang PR review burden.** ~260 callsite edits in one diff. Mitigation: lay it out so reviewers can scan record-by-record (each record + its callers in a contiguous block). Per the user, "you don't have to care about git" — don't pre-emptively split for cherry-pickability.

## Out of scope

- Changing the wire envelope of `infrastructure.jsonl` or the IPC pipe.
- Changing the `SDVD_EVENT` forwarding protocol.
- Source-generated `JsonTypeInfo` (premature optimization for an emit path that already runs in microseconds).
- Re-numbering or re-organising the existing `Schema/Events/` IPC records.
- Folding the Schema project into `JunimoServer.Shared`.

## Open follow-ups

- Should `InfrastructureEventNames` collapse with `Schema/EventNames` (IPC)? Default no — different envelopes, different consumers, separate constants reduce confusion. Revisit if any event name needs to appear on both pipelines (none today).
- Should the records be `record class` or `record struct`? Default `record class` — the JSON writer's reflection path is unchanged. `record struct` is a microopt with no user-visible benefit.
- Per-process `InfrastructureEventLog` linkage: the same class is linked into both `JunimoServer.Tests` (writes `infrastructure.jsonl`) and `JunimoServer.TestRunner` (writes `parent-infrastructure.jsonl`, merged at end-of-run by the broker shutdown path). Both processes pick up the new typed `Emit<T>` overload simultaneously since `TestRunner` references `Tests`. Confirm with one runner-side emit during the spike rather than assuming — failure mode would be a missing-method `TypeLoadException` on runner startup.
