# Test-client HTTP API: one source of truth for routes + OpenAPI contract

Status: open — proposed followup.

## Context

The test-client (`tests/test-client/`) exposes an HTTP API for E2E automation. Its **routes** are
registered as lambdas in `ModEntry.cs` (`_server.Get("actions/…", handler)` /`_server.Post(…)`,
stored in `TestApiServer` as a plain `path → delegate` dictionary), while its **OpenAPI contract**
is hand-declared as empty stub methods carrying `[ApiEndpoint]`/`[ApiResponse]` attributes in
`HttpServer/ApiDefinitions.cs`, reflected over by `HttpServer/OpenApiGenerator.cs`
(`Generate(typeof(ApiDefinitions), …)`). Nothing links the two, so they drift silently — the
compiler cannot catch a missing or renamed entry.

A drift-fix pass already reconciled the path-level gaps (added `/actions/farm_buildings`,
`/actions/location_warps`, `/connect/lan`, `/character`; renamed the never-served
`/wait/character-customization` stub to the served `/wait/character`). Two lower-stakes issues were
deliberately deferred to this followup because they are entangled with the duplication this plan
removes:

- **Request-DTO duplication.** `ApiDefinitions.cs` defines its own `CoopTabRequest`,
  `InviteCodeRequest`, `JoinLanRequest`, `SelectFarmhandRequest`, while `ModEntry.cs` defines a
  parallel set (`CoopTabRequest`, `JoinInviteRequest`, `JoinLanRequest`, …) that the handlers
  actually deserialize via `TestApiServer.ReadBody<T>`. `/coop/invite-code/submit` documents
  `InviteCodeRequest` but the handler reads `JoinInviteRequest` (identical shape, so the generated
  schema matches today — a latent, not active, drift).
- **`/connect/lan` response type.** The handler returns `JoinResult` (documented as such), but
  `GameTestClient` deserializes it as `NavigationResult` — a client-side tolerance to revisit.

## Goal

Make the routing table and the OpenAPI contract read from a single declaration, so a route cannot
exist without contributing to the spec (and vice-versa), and request/response DTOs are owned once.

## Options

### Option A — mirror the server: co-locate attributes on named handlers

The server side already does this: `[ApiEndpoint]`/`[ApiResponse(typeof(X))]` sit on the real
handler methods (`ApiService.HandleGetCabins` etc.), and its generator reflects over the
handler-owning type. Port that shape to the test-client:

- Convert the `ModEntry.cs` route lambdas into named handler methods (attributes cannot attach to
  anonymous lambdas), decorate each, and point `OpenApiGenerator.Generate` at the handler-owning
  type instead of `ApiDefinitions`. Delete `ApiDefinitions.cs` and its duplicate DTOs.
- Response DTO becomes the method's real return type, reflectable from the signature. The request
  DTO is the `ReadBody<T>` type argument, which lives inside the method body and is NOT reflectable
  from the signature — so Option A must declare it explicitly (e.g. an `[ApiRequest(typeof(X))]`
  attribute the generator reads) rather than deriving it "by construction". With that attribute the
  duplicate `ApiDefinitions` request DTOs still go away.
- Scope: ~50 lambdas → named methods across `ModEntry.cs` and the `GameControl/*Controller` classes.
  Large but mechanical. Note the server is still a manual `switch(path)` dispatcher — Option A fixes
  *co-location*, not dispatch, so a small dispatch table would remain.

### Option B — shared registration list feeding both

Extend `TestApiServer.Get/Post/Delete` to accept optional metadata (summary/description/tag/response
type) and store it alongside the delegate; have `OpenApiGenerator` enumerate the **live route table**
instead of a stub class. The router and the spec then read the exact same registration — a route
literally cannot exist without a spec entry, and the request body type comes from the handler's own
`ReadBody<T>`. This is the truer single source (also kills the reverse-drift and both deferred issues
above) but is more invasive to `TestApiServer`.

## Recommendation

Option B is the cleaner end state (one registration feeds both, no stub class, DTOs owned once).
Option A is the smaller conceptual leap and matches an existing in-repo precedent. Both remove the
DTO-duplication drift, but only Option B closes route/spec drift: under Option A route registration
stays a separate declaration, so a handler can still be omitted or served under a path that differs
from its attribute. Pick B to eliminate the drift outright; pick A as an incremental step that still
leaves the dispatch-table drift for later.

## Key files

- `tests/test-client/ModEntry.cs` — route registration (lambdas) + duplicate request DTOs.
- `tests/test-client/HttpServer/TestApiServer.cs` — route storage/dispatch (`Get`/`Post`/`Delete`).
- `tests/test-client/HttpServer/ApiDefinitions.cs` — the stub contract + duplicate DTOs to eliminate.
- `tests/test-client/HttpServer/OpenApiGenerator.cs` — generator (currently reflects `ApiDefinitions`).
- `tests/test-client/HttpServer/ApiAttributes.cs` — `ApiEndpoint`/`ApiResponse`/`ApiRequestBody`/`ApiQueryParam`.
- `tests/test-client/GameControl/*Controller.cs` — the real handlers + their response DTOs.
- Precedent to mirror: `mod/JunimoServer/Services/Api/ApiService.cs` (attributes on real handlers)
  and `mod/JunimoServer/Services/Api/OpenApiGenerator.cs` (server generator).
