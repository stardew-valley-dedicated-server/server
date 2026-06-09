# Discord Bot MCP Module (follow-up)

## Status

**Deferred follow-up — not scheduled.** This is the "build our own" alternative to the off-the-shelf
Discord MCP that the triage-skills package adopts in the near term
([`discord-github-triage-skills.md`](./discord-github-triage-skills.md)). Captured so the idea and its
trade-offs aren't lost. **Do not start this until** the triage skills are in use and
[SaseQ/discord-mcp](https://github.com/SaseQ/discord-mcp) has either proven insufficient or the
"first-class bot feature for others" value is explicitly wanted. The skills package does **not**
depend on this.

## Context

`tools/discord-bot/` is today a single-file (`src/index.ts`, ~485 lines) `discord.js@14` gateway bot
(`"type": "module"`, run via Bun). It already:
- Reads the server's HTTP API (`API_URL`, `Authorization: Bearer ${API_KEY}`) and WebSocket
  (`WS_URL`) for live status (`ServerStatus`, `WebSocketMessage` — `src/index.ts:35-53`).
- Relays chat **game ↔ Discord channel** (`DISCORD_CHAT_CHANNEL_ID`, gated `GuildMessages` +
  `MessageContent` intents — `src/index.ts:55+`).
- Sets a dynamic nickname / presence (rate-limit-aware, `UPDATE_INTERVAL_MS`).
- Holds a bot token (`DISCORD_BOT_TOKEN`) with Read Message History.

The planned Discord-auth work (`server-discord-auth.md`) already intends to grow this bot with an
OAuth registration flow and a `GET /auth/verify` HTTP API, refactoring the single file into
`src/bot/`, `src/api/`, `src/commands/` modules. **This MCP module should land on top of that
restructure, not fight it** — reuse the same module layout and token.

## Goal

Expose the bot's Discord capabilities (and, naturally, its existing server-status/chat surface) as a
**first-class MCP server**, so any MCP client (Claude Code, others) can read threads/messages/forum
posts, send messages, and run triage/moderation against the JunimoServer community — tightly
integrated with the bot that already holds the connection and token.

Tools to expose (superset of what the triage skills need):
- `read_messages(channel|thread, limit)`, `read_message(messageId|deep-link)`,
  `list_forum_posts(forumChannel)`, `read_forum_post(threadId)`.
- `send_message(channel|thread, content)`, `reply(messageId, content)`.
- (Optional, later) moderation: `add_reaction`, thread `lock/archive`, lookups.
- (Natural extension) `server_status()` / `recent_chat()` bridging the bot's existing API+WS data.

## Decision this plan must make first: off-the-shelf vs. own

Before building, write a short decision section comparing against the **SaseQ/discord-mcp baseline**
(the thing to beat):
- **Adopt SaseQ as-is** — zero maintenance, but a third-party process holds the bot token, runs as a
  separate server from our bot, and we can't bridge our server-status/chat data into it.
- **Build our own MCP in the bot** — one process owns the token + the live API/WS connection, can
  expose JunimoServer-specific tools (status, invite codes, lobby), and becomes a shippable feature
  others can run. Cost: a new MCP transport subsystem + ongoing maintenance.

The "own" path only wins if the **integration** (bot status/chat + Discord in one MCP) or the
**ship-to-others** value is real. If all we ever need is reading triage threads, SaseQ is enough —
say so and close this plan.

## Approach (if "own" is chosen)

- **Add an MCP transport to the bot**, separate from the gateway event loop. An MCP server is
  request/response (stdio or streamable-HTTP via `@modelcontextprotocol/sdk`); the gateway bot is a
  long-lived event consumer. They coexist in one process: the gateway client provides the Discord
  connection; the MCP layer exposes tools that call into it (`channels.fetch`, `messages.fetch`,
  thread/forum APIs already available on the `discord.js` `Client`).
- **Module layout:** land under the auth-plan's restructure — e.g. `src/mcp/` (server + tool
  registrations) alongside `src/bot/` (gateway), sharing the single `Client` instance and token.
- **Transport choice:** stdio for local Claude use; optional streamable-HTTP behind the bot's
  network if remote clients need it. Pick one for v1, document the other as future.
- **Permissions/intents:** reuse the existing token; confirm Read Message History + Message Content
  cover thread/forum reads (the relay already has `MessageContent`).
- **Config:** wire via `.mcp.json` (stdio command) the same way the triage skills would wire SaseQ —
  so swapping our MCP in for SaseQ is a one-line config change for consumers.

## Files (sketch — finalize against the auth-plan restructure)

| Path | What |
|---|---|
| `tools/discord-bot/src/mcp/server.ts` | MCP server bootstrap (`@modelcontextprotocol/sdk`), transport selection. |
| `tools/discord-bot/src/mcp/tools/*.ts` | Tool registrations (read/send/forum), calling the shared `Client`. |
| `tools/discord-bot/package.json` | Add `@modelcontextprotocol/sdk` dep + an `mcp` run script. |
| `.mcp.json` | Point at our MCP instead of SaseQ once it's at parity. |
| `docs/...` | Operator setup; note it supersedes the off-the-shelf MCP. |

## Open questions (resolve before building)

- Does the off-the-shelf SaseQ MCP actually fall short for our needs, or is "own" scope creep? (The
  gating decision above.)
- One process or two — does running an MCP transport inside the gateway bot risk the relay's
  reliability (a slow MCP call blocking the event loop)? If so, separate process sharing the token.
- Token scope: a single bot token for relay + auth + MCP, or separate apps? (Security blast radius.)
- Does the planned auth-bot restructure land first? This module should follow it to avoid two
  competing refactors of the same single file.

## Verification (when built)

- MCP tools resolve via ToolSearch as `mcp__discord__*` and return this guild's threads/messages.
- The triage skills work unchanged after swapping `.mcp.json` from SaseQ to our MCP (drop-in parity).
- The chat relay and presence/nickname features keep working with the MCP layer running (no event-
  loop regression).
