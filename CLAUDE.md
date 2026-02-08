# Stardew Valley Dedicated Server

A C# SMAPI mod that transforms Stardew Valley into a dedicated game server, running in Docker.

## Quick Reference

- **Build/Run:** `make help` for all targets; `make up` to start server
- **Config:** `.env.example` for environment variables
- **API:** `/mod/JunimoServer/Services/Api/`

## Structure

| Path | Purpose |
|------|---------|
| `/mod` | Main server mod (JunimoServer) |
| `/tests` | E2E tests (xUnit + Testcontainers) |
| `/tools` | `steam-service`, `discord-bot`, `dll-patcher`, `netdebug`, `openapi-generator`, `test-client` |
| `/docker` | Dockerfile |
| `/docs` | VitePress documentation |
| `/decompiled` | Read-only game reference — do not modify |

## Conventions

- **Commits/Branches:** Conventional commits (`feat:`, `fix:`, `chore:`) — same prefixes for branches
- **PRs:** Squash merge only
- **IMPORTANT - Git commits:** NEVER add `Co-Authored-By` lines or `🤖 Generated with Claude Code` footers to commit messages. Keep commits clean with just the message.
- **Git workflow:** See `.claude/git-workflow.md`
- **Fixes:** Root cause only — no workarounds
- **Game research:** Use `/decompiled` for game internals; web search for SMAPI APIs

## Environment

Windows development. Use glob/search tools over shell commands for file lookups.
