# Structured Events Schema

Wire contract for structured events emitted across the process graph. This document is the authoritative definition — source code references it, not the other way around. When this spec changes, every emitter is updated in the same PR; the spec change **is** the API change.

## Core envelope

Every structured event is a single JSON line (JSONL) with exactly these fields:

```json
{
  "ts":        "<DateTime.UtcNow ISO-8601>",   // always
  "runMs":     <long>,                          // optional
  "requestId": "<string>",                      // optional
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
- `service` — which service emitted this event. Always one of the four canonical values below.
- `test` — nested object identifying the currently-executing test: `{ class, method, displayName }`. Populated by `InfrastructureEventLog` when a test is active. Absent on forwarded mod/sidecar events (shared containers serve many tests) and on test-harness emits outside any test (pre-warming, broker lifecycle).
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

- **`requestId`** — per-HTTP-call join key. One end-to-end flow across harness → mod → sidecar shares one id. Answers "what did this API call do?"
- **`test.*`** — which test caused the event. Populated only on test-harness-emitted events (`service = "test-harness"`); absent on forwarded mod/sidecar events because shared containers serve many tests. Answers "what did this test do?"

Typical queries:

```bash
# Everything the test-harness emitted for one test
jq 'select(.test.displayName == "NavigationTests.JoinServer")' infrastructure.jsonl

# A specific API call's full fan-out across services
jq 'select(.requestId == "abc123")' infrastructure.jsonl

# A specific call within a specific test
jq 'select(.test.displayName == "X" and .data.path == "/newgame")' infrastructure.jsonl
```

A single `requestId` stitches a logical operation across every structured stream:

- Test code (or fixture) calls `ServerApiClient.CreateNewGameAsync(...)`, which enters a `CorrelationContext` scope via `TracingHandler`.
- `TracingHandler` adds `X-Request-Id` to the outbound request and emits `http_request` (service `test-harness`) into `infrastructure.jsonl` with that id.
- The server mod's `ApiService.HandleRequestAsync` reads the header and binds `ModRequestContext.RequestId` (AsyncLocal) for the request duration. `ModEventLog.Emit` reads this when it writes `http_served` and any other server-side events.
- If the mod makes an outbound HTTP call to the sidecar during this request, `SteamAuthCorrelationHandler` forwards the same `X-Request-Id` header. The sidecar's Kestrel middleware reads it and binds `SidecarRequestContext.Current` (AsyncLocal) for the request duration.
- The test-client mod's `TestApiServer` reads inbound `X-Request-Id` and binds `ClientRequestContext` for the handler duration. `ClientEventLog.Emit` reads this.

Events emitted outside any scope carry `requestId = null`. SteamKit callbacks, game-engine reactions, and background watchdogs are the common cases. Correlate those via `service`, `ts`, and `forwardedVia`.

## How to add a new event type

1. Pick a short `snake_case` event name.
2. Decide the emitter based on where the event source lives (server mod, sidecar, test-client, or test infrastructure).
3. Add the `event` name + payload shape to the emitter's catalog (see `InfrastructureEventLog.cs` docstring for the full list).
4. Call `Emit(eventName, new { ... })`. Event-specific fields go in `data`; do not extend the envelope.
