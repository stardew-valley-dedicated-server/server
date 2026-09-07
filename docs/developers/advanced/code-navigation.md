# Code Navigation (Serena)

[Serena](https://github.com/oraios/serena) is an [MCP](https://modelcontextprotocol.io/) server that gives an AI coding agent **LSP-based semantic navigation** — exact go-to-definition, find-references, find-implementations, and symbol-level edits — instead of text search. It runs entirely locally and is free.

This page covers only how it is wired **for this repository**. For how the tools work, contexts, and client setup, see the [Serena documentation](https://oraios.github.io/serena/).

## Setup

Two steps, once:

1. Install Serena: `uv tool install -p 3.13 serena-agent` (needs [uv](https://docs.astral.sh/uv/)).
2. Open the repo in Claude Code and **approve** the Serena server when prompted.

That's it — the projects below are already configured. To also navigate the **decompiled game code**, run the [decompilation step](/developers/advanced/decompiling) (those sources aren't in git).

## How it's wired

A project-scoped [`.mcp.json`](https://github.com/stardew-valley-dedicated-server/server/blob/master/.mcp.json) at the repo root registers Serena for Claude Code. Claude Code **prompts you to approve** the server on first launch in the repo — nothing runs unapproved. The committed `.serena/project.yml` files define the project scoping below, so everyone shares the same setup.

## Prerequisites

The config runs `serena` on your `PATH`; it is **not** auto-installed. Once:

```bash
uv tool install -p 3.13 serena-agent
```

(Requires [uv](https://docs.astral.sh/uv/). Prefer no install? Point `.mcp.json` at `uvx --from serena-agent serena start-mcp-server` instead.)

The language servers download on first use and need runtimes you already have for this repo: the **.NET SDK** (C# projects) and **Node** (the Vue `test-ui` project).

## Projects

Ask the agent to work in a project (e.g. *"search the mod project for X"*) — it activates the project from its committed `.serena/project.yml`. Serena's project registry is per-user and local, so the short **names** below register on first activation on your machine; the committed config is what makes each project available on any checkout.

| Project | Root | Covers |
|---|---|---|
| `junimo-mod` | `mod/` | The mod: `JunimoServer` + `JunimoServer.Shared` |
| `junimo-tests` | `tests/` | E2E test suite; resolves into `mod/` and `steam-service` |
| `junimo-steam-service` | `tools/steam-service` | The Steam auth sidecar |
| `junimo-test-ui` | `tests/test-ui` | The Vue/TS monitoring UI |
| `stardew-decompiled` | `decompiled/sdv-<version>/` | Decompiled game code (see below) |

## Decompiled game code

`stardew-decompiled` needs the decompiled sources present locally — run the [decompilation step](/developers/advanced/decompiling) first. Its `.serena/project.yml` is committed (the sources themselves stay gitignored), so once you decompile into the folder, the project works with no extra setup.
