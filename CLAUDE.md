# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

JunimoServer is a Stardew Valley dedicated server mod enabling 24/7 multiplayer hosting via Docker. The mod runs inside the game via SMAPI, exposing an HTTP API, WebSocket, and chat commands for external control.

**Stack**: C# mod (net6.0/SMAPI) + Docker containers + xUnit v3 E2E tests (net10.0) + Vue/TypeScript test UI + VNC for visual debugging. Test infrastructure includes client pooling, server pre-start, and WebSocket-based real-time updates.

## Core Principles

Always-on behavioral rules live in `.claude/rules/universal/` (loaded every session). Code-area rules live in `.claude/rules/*.md` (gated by `paths:` frontmatter). Read `.claude/rules/README.md` for the layer model and full index.

## Implementation Discipline

- When implementing a multi-item plan, check off EVERY item before declaring completion. After finishing, re-read the plan and verify each item was addressed. Pay attention to file naming conventions already established in the project (e.g., .env.test not .test.env).

## Critical Paths

- `mod/JunimoServer/`: main SMAPI mod (C#, net6.0)
- `mod/JunimoServer/ModEntry.cs`: entry point, service registration
- `mod/JunimoServer/Services/`: all mod services
- `mod/JunimoServer.Shared/`: code shared between the server mod and the test-client mod
- `tests/JunimoServer.Tests/`: E2E tests (xUnit v3, net10.0)
- `tests/JunimoServer.Tests/Infrastructure/`: test resource broker, server/client pooling
- `tests/JunimoServer.TestRunner/`: custom test-runner host process (Exe; not the test assembly the stdout prohibition applies to)
- `tests/test-client/`: SMAPI mod for E2E client automation
- `tests/test-ui/`: Vue.js test monitoring UI
- `docker/Dockerfile`: multi-stage server image build
- `Directory.Build.props`: centralizes `GAME_PATH` for all .csproj files
- `decompiled/sdv-1.6.15-24356/`: decompiled Stardew Valley sources (reference only)

## Prohibitions

- Do NOT write to stdout in test assemblies. It corrupts xUnit v3's IPC and breaks test discovery. Use `ITestOutputHelper` or `IMessageSink` instead.
- Do NOT create Docker networks via CLI then wrap with `NetworkBuilder`. Testcontainers will conflict. Use `NetworkBuilder` directly.
- Do NOT hardcode `GamePath` in .csproj files. It comes from `.env` via `Directory.Build.props`.
- Do NOT use `git add .` or `git add -A`. Stage files explicitly by path (see `.claude/rules/universal/git-workflow.md`).

## Build & Run Commands

Run `make help` or read the `Makefile` for all available targets (build, test, deploy, docs, etc.). Key patterns:

- `make test FILTER=ClassName` to run specific E2E tests
- `make test-llm` for structured JSONL output optimized for AI debugging
- `dotnet build mod/JunimoServer/JunimoServer.csproj` to build the mod only (requires `GAME_PATH` in `.env`)

## Test Failure Debugging Workflow

When tests fail, follow the runbook at `docs/developers/testing/test-failure-runbook.md` (6 steps; do not skip or guess).

## Conventions

- **Commits**: Conventional commits enforced by commitlint (`feat:`, `fix:`, `perf:`, `docs:`, `test:`, `chore:`, `refactor:`, `ci:`, `build:`)
- **Decompiled sources**: Reference at `decompiled/sdv-1.6.15-24356/` for tracing game mechanics
- **Helpers are integration-tested, not unit-tested**: `tests/JunimoServer.Tests/` is E2E only — there is no unit-test layer. Verification of new helper code (e.g., wait-tracing primitives) is done by inspecting the JSONL output of a real run, not by isolated unit tests.
