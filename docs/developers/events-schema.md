# Structured Events Schema

Wire contract for structured events emitted across the process graph. This document is the authoritative definition — source code references it, not the other way around. When this spec changes, every emitter is updated in the same PR; the spec change **is** the API change.

Authoring note: when a field's values are inline string literals at the emit site, don't enumerate the variants verbatim here (or in the `InfrastructureEventLog` catalog) — reference the enum or the emitting class instead ("see `ContainerRecorder` for the definitive list"). Verbatim value lists go silently stale when a variant is added at the emit site; a small, stable set (3-4 values) may be listed, and a large one is a signal the strings should become a typed enum.

## Core envelope

Every structured event is a single JSON line (JSONL) with exactly these fields:

```json
{
  "ts":        "<DateTime.UtcNow ISO-8601>",   // always
  "runMs":     <long>,                          // optional
  "requestId": "<string>",                      // optional
  "testId":    "<string>",                      // optional
  "service":   "<string>",                      // always
  "test":      { "class": "...",
                 "method": "...",
                 "displayName": "..." },        // optional
  "phase":     "<string>",                      // optional
  "tickMs":    <int>,                           // optional
  "event":     "<string>",                      // always
  "data":      <object|null>                    // always
}
```

- `ts` — ISO-8601 UTC timestamp captured at emit.
- `runMs` — milliseconds since `RunMetadata.RunClock` start. Populated by `InfrastructureEventLog`.
- `requestId` — ambient correlation identifier. Set when a correlation scope is active (HTTP request handler, logical operation scope from `CorrelationContext.BeginWithId`, outbound `TracingHandler` request). Null when no scope is active — do not fabricate.
- `testId` — the originating test's display name, carried on the `X-Test-Id` request header and bound as `ModRequestContext.TestId` for the request duration. Both this header and the harness-side `test.displayName` are normalized to printable ASCII by the same `TestIdentityContext` normalizer, so the two stay byte-identical for the join below. This is the **server/sidecar-side** counterpart to `test.*`: `test.*` is populated only on `test-harness` events, but a forwarded mod event can't hold the nested `test` object, so `testId` gives those events per-test attribution. Attached at every tracing level (unlike `requestId`), so even reads are attributable. Null only when no test context reached the emitting operation: production, and game-network activity such as a join or chat message from a test's client, which arrives outside any request even though a test caused it. Request-dispatched work that outlives the handler still carries it — `GameThreadDispatcher` and the `/wait/*` continuations capture and rebind it across the `AsyncLocal` pump boundary. Join it to `test.displayName`.
- `service` — which service emitted this event. Always one of the four canonical values below.
- `test` — nested object identifying the currently-executing test: `{ class, method, displayName }`. Populated by `InfrastructureEventLog` when a test is active. Absent on forwarded mod/sidecar events (shared containers serve many tests — those carry `testId` instead) and on test-harness emits outside any test (pre-warming, broker lifecycle).
- `phase` — lifecycle phase of the currently-executing test (`setup`, `connect`, `artifacts`, `cleanup`, or a checkpoint label). Set by `TestIdentityContext.PushPhase` scopes in `TestBase`; absent when no scope is active. Used by the failure runbook to locate which phase emitted a given event.
- `tickMs` — game-tick counter at emit. Populated by mod emitters only (`ModEventLog`); caller-supplied because reading `Game1.ticks` off-thread is unsafe.
- `event` — short snake-case event name. See each emitter for its catalog.
- `data` — free-form payload. May be `null`.

Serialization:
- `System.Text.Json` with `PropertyNamingPolicy = CamelCase` and `DefaultIgnoreCondition = WhenWritingNull`.
- The C# field `@event` serializes to `event` on the wire.
- Null optional fields are omitted from output.

## Canonical `service` values

| service | emitter | file |
|---|---|---|
| `server` | `ModEventLog` | `mod/JunimoServer/Services/Diagnostics/ModEventLog.cs` |
| `test-client` | `ClientEventLog` | `tests/test-client/Diagnostics/ClientEventLog.cs` |
| `test-harness` | `InfrastructureEventLog` | `tests/JunimoServer.Tests/Helpers/InfrastructureEventLog.cs` |
| `steam-auth` | `Logger.LogEvent` | `tools/steam-service/Logger.cs` |

## Run identity

A **run** is identified by its filesystem path (`TestResults/runs/{timestamp}_{sha}/`). Every artifact inside the folder belongs to that run — no per-event `runId` field is emitted.

The one exception is `flakiness.jsonl` at the repo root, which aggregates across runs and uses `runId` as its grouping key.

In-memory `RunMetadata.RunId` serves three purposes: naming Docker containers (`sdvd-steam-auth-shared-{runId}`), labeling Docker resources for orphan reaping (`sdvd.run-id={runId}`), and keying flakiness aggregation. It does not appear in the event envelope.

## Transport: `SDVD_EVENT` stdout prefix

Mod and sidecar containers emit structured events to **stdout** with a fixed prefix:

```
SDVD_EVENT {"ts":"...","service":"server",...}
```

The host-side `SimpleContainerLogStreamer.TryForwardSdvdEvent` parses the prefix, decorates the payload with `forwardedVia`, and appends the line to `{runDir}/diagnostics/infrastructure.jsonl`. This is the sole transport.

The prefix is `SDVD_EVENT␠` (uppercase + single space, 11 bytes) and is byte-stable. The remainder of the line must be a valid JSON object; malformed payloads are dropped with a one-per-type stderr warning.

## Forwarded-line decoration: `forwardedVia`

Events forwarded via stdout acquire a single top-level decoration when the streamer writes them to `infrastructure.jsonl`:

```json
{ "ts": "...", "service": "server", "event": "http_served", ..., "forwardedVia": "server-0" }
```

`forwardedVia` names the **container of origin** (`server-0`, `client-2`, `steam-auth-shared`). It is distinct from `service`: `service` identifies the emitter type, `forwardedVia` identifies the container instance.

**Contract**:
- `forwardedVia` is the **only** field any post-emit transport may add.
- Its **absence** means the event was written directly by its emitter (native emit — `InfrastructureEventLog`).
- Its **presence** means the event was forwarded from stdout; the core envelope fields are preserved byte-for-byte from the origin emitter.

## Per-stream top-level decorations

The core envelope is identical across every stream. Individual streams may add stream-specific top-level fields:

| Stream | Extra top-level fields | Rationale |
|---|---|---|
| `{runDir}/diagnostics/infrastructure.jsonl` (forwarded lines only) | `forwardedVia` | Container-of-origin identifier. |

Consumers should treat any top-level field outside the core envelope as an optional stream-specific decoration.

## Schema version

`run-metadata.json` and `summary.json` both carry `"schemaVersion": 1`. One version per run covers every structured artifact — no per-artifact or per-event versioning.

Breaking changes to the envelope bump this number in lockstep in both manifest files. Additive changes (new optional field, new event name) do not bump.

## Correlation across the process graph

Two orthogonal filtering axes:

- **`requestId`** — per-HTTP-call join key. One end-to-end flow across harness → mod → sidecar shares one id. Answers "what did this API call do?" Present only when tracing mints one (`Basic`+ for mutations, `Full` for every verb).
- **`test.*`** — which test caused the event, as a nested object. Populated only on test-harness-emitted events (`service = "test-harness"`). Answers "what did this test do?"
- **`testId`** — the server/sidecar-side flavor of the same axis: the originating test's display name, present on forwarded mod/sidecar events that can't carry the nested `test` object. Attached on every request regardless of tracing level, so a forwarded `http_served` (even for a read) is attributable to its test. Answers "which test issued this server event?" — join `.testId` to a test-harness event's `.test.displayName`.

Typical queries:

```bash
# Everything the test-harness emitted for one test
jq 'select(.test.displayName == "NavigationTests.JoinServer")' infrastructure.jsonl

# A specific API call's full fan-out across services
jq 'select(.requestId == "abc123")' infrastructure.jsonl

# A specific call within a specific test
jq 'select(.test.displayName == "X" and .data.path == "/newgame")' infrastructure.jsonl

# Every server event a given test caused (forwarded mod events carry testId, not test.*)
jq 'select(.testId == "NavigationTests.JoinServer")' infrastructure.jsonl

# Which instance a test ran on, for any isolation mode
jq 'select(.event == "test_instance_bound" and .test.displayName == "X") | .data.serverInstanceId' infrastructure.jsonl
```

A single `requestId` stitches a logical operation across every structured stream:

- Test code (or fixture) calls `ServerApiClient.CreateNewGameAsync(...)`, which enters a `CorrelationContext` scope via `TracingHandler`.
- `TracingHandler` adds `X-Request-Id` to the outbound request and emits `http_request` (service `test-harness`) into `infrastructure.jsonl` with that id.
- The server mod's `ApiService.HandleRequestAsync` reads the header and binds `ModRequestContext.RequestId` (AsyncLocal) for the request duration — alongside `ModRequestContext.TestId` from the always-present `X-Test-Id` header. `ModEventLog.Emit` reads both when it writes `http_served` and any other server-side events. Both are captured and rebound across the game-thread queue (`GameThreadDispatcher`) and the `/wait/*` await continuations, which `AsyncLocal` doesn't cross on its own.
- If the mod makes an outbound HTTP call to the sidecar during this request, `SteamAuthCorrelationHandler` forwards the same `X-Request-Id` and `X-Test-Id` headers. The sidecar's Kestrel middleware reads both and binds `SidecarRequestContext` (AsyncLocal) for the request duration, so `Logger.LogEvent` stamps `requestId` and `testId` on every sidecar event the call produces.
- The test-client mod's `TestApiServer` reads inbound `X-Request-Id` and `X-Test-Id` and binds `ClientRequestContext` for the handler duration (captured and rebound across its game-thread queue, like the server). `ClientEventLog.Emit` reads both.

Events emitted outside any scope carry `requestId = null`. SteamKit callbacks, game-engine reactions, and background watchdogs are the common cases. Correlate those via `service`, `ts`, and `forwardedVia`.

## How to add a new event type

1. Pick a short `snake_case` event name.
2. Decide the emitter based on where the event source lives (server mod, sidecar, test-client, or test infrastructure).
3. Add the `event` name + payload shape to the emitter's catalog (see `InfrastructureEventLog.cs` docstring for the full list).
4. Call `Emit(eventName, new { ... })`. Event-specific fields go in `data`; do not extend the envelope.
