# Code Navigation (Serena)

[Serena](https://github.com/oraios/serena) is an [MCP](https://modelcontextprotocol.io/) server that gives an AI coding agent LSP-based navigation (go to definition, find references, find implementations, and symbol-level edits) instead of text search. It runs locally and is free.

This page covers how it is set up for this repository. For how the tools work, see the [Serena documentation](https://oraios.github.io/serena/).

## Setup

1. Install Serena once: `uv tool install -p 3.13 serena-agent` (needs [uv](https://docs.astral.sh/uv/)). To skip the install, point `.mcp.json` at `uvx --from serena-agent serena start-mcp-server` instead.
2. Open the repo in Claude Code and approve the Serena server when prompted.

The language servers download on first use and need runtimes this repo already requires: the .NET SDK for C# and Node for TypeScript and Vue.

## Projects

The project-scoped [`.mcp.json`](https://github.com/stardew-valley-dedicated-server/server/blob/master/.mcp.json) starts Serena with `--project-from-cwd`, so the root project of the checkout Claude Code starts in (the main checkout or a worktree) is active from the start. Serena picks the project once, at startup: after a session switches into a worktree, ask the agent to activate the worktree by path.

| Project | Root | Covers |
|---|---|---|
| `junimo` | repo root | Every C# project, plus the TypeScript and Vue code |
| `stardew-decompiled` | `decompiled/sdv-<version>/` | Decompiled game code (see below) |

The first activation in a fresh checkout is slow while the C# language server restores and loads every project.

## Decompiled game code

`stardew-decompiled` needs the decompiled sources, which stay out of git: run the [decompilation step](/developers/advanced/decompiling) first. Its `.serena/project.yml` is committed, so it works as soon as the sources are there. Serena has one active project at a time. The first time, ask the agent to activate the main checkout's `decompiled/sdv-<version>/` folder by path (worktrees have the config but not the sources); after that, the name works. Switch back by path too: every checkout's root project is named `junimo`, so Serena rejects that name once more than one checkout has used it.
