# JunimoServer

JunimoServer is a Stardew Valley dedicated-server mod providing 24/7 Docker hosting, an HTTP API, WebSocket control, and chat commands.

## Architecture

- The server runs as a SMAPI mod inside Stardew Valley.
- **Clients are unmodded vanilla clients.** Server-side Harmony patches and asset edits affect only the server process. Client-visible behavior must use server-authoritative state or vanilla network messages.
- `mod/JunimoServer.Shared/` is shared by the server mod and E2E test-client mod.
- E2E infrastructure uses xUnit v3, Testcontainers, pooled game clients/servers, and a Vue/TypeScript monitoring UI.

## Rules

Detailed always-on and path-specific rules live in `.claude/rules/`. Read `.claude/rules/README.md` for the rule hierarchy and index.

- Never write to stdout from test assemblies; it corrupts xUnit v3 IPC. Use `ITestOutputHelper` or `IMessageSink`.
- Create Testcontainers networks with `NetworkBuilder`; never create them via Docker CLI first.
- Never hardcode `GamePath` in `.csproj`; it comes from `.env` via `Directory.Build.props`.

## Important Paths

- `mod/JunimoServer/` — main SMAPI mod
- `mod/JunimoServer.Shared/` — shared server/test-client code
- `tests/JunimoServer.Tests/` — E2E tests and infrastructure
- `tests/JunimoServer.TestRunner/` — test-runner host process
- `tests/test-client/` — SMAPI E2E client mod
- `tests/test-ui/` — test monitoring UI
- `decompiled/sdv-1.6.15-24356/` — gitignored Stardew Valley sources for reference; unavailable from worktrees unless present there

## Commands

- `make help` — available Make targets
- `make test FILTER=ClassName` — run specific E2E tests
- `make test-llm` — structured JSONL test output for AI debugging
- `make build-test-ui` — type-check/build the test UI
- `dotnet build mod/JunimoServer/JunimoServer.csproj` — build the mod (`GAME_PATH` required)

## Debugging Tests

When an E2E test fails, follow `docs/developers/testing/test-failure-runbook.md` exactly; do not skip steps or guess.
