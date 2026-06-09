# Discord → GitHub Triage Skills Package

## Context

Triaging community bug reports follows a recurring loop that was run by hand this session: read a
Discord report, investigate the code, root-cause it, draft a GitHub issue to the repo's bug
template, and file it (this produced [#361](https://github.com/stardew-valley-dedicated-server/server/issues/361)).
The loop carries non-obvious project knowledge worth encoding into reusable skills:

- The repo has **two** issue formats — the reporter **bug-report template**
  (`.github/ISSUE_TEMPLATE/bug_report copy.yml`: *Describe the bug → Steps to reproduce → Expected
  behavior → Additional context → Final checks*) vs. the maintainer freeform `## Symptom / ## Cause
  / ## Fix` style (e.g. #346). An **initial report** uses the template; a maintainer write-up uses
  the freeform style.
- Labels are `bug` + `priority-1..4` (verified via `gh label list`). The template auto-applies
  `bug` + `needs-verification`; `needs-verification` is the triage-pending default until a priority
  is assigned.
- Findings must be **verified, not asserted** (`subagent-findings-are-claims`, `verify-claims`):
  open cited `file:line` before building an issue on a sub-agent's bottom-line.
- `gh` CLI is authenticated and is the issue path.

**Goal:** a project-scoped skill package (`.claude/skills/`) that turns a Discord report link into a
triaged GitHub outcome — find duplicates first, then create a template-correct issue, with a human
gate before any write, and cross-link the Discord thread.

**Discord access decision (resolved):** skills depend on a real Discord MCP. Rather than build one,
**adopt the off-the-shelf [SaseQ/discord-mcp](https://github.com/SaseQ/discord-mcp)** (bot-token,
actively maintained, exposes `read_messages` + `list_forum_posts` + forum-thread tools — matches our
reports being *forum posts*). Rejected: `v-3/discordmcp` (abandoned), selfbot/user-token servers
(Discord account-ban risk). Expanding our own bot into an MCP is a **deferred follow-up** — see
[`discord-bot-mcp-module.md`](./discord-bot-mcp-module.md); this package does not depend on it.

## Prerequisite (not a skill): configure SaseQ/discord-mcp

Add a `.mcp.json` entry wiring SaseQ/discord-mcp with the existing bot token (`DISCORD_TOKEN`) and
`DISCORD_GUILD_ID=947923329057185842`, plus a short setup note. The bot needs **Read Message
History** + the **Message Content** intent (the relay bot already declares `MessageContent` —
`tools/discord-bot/src/index.ts`).

**Setup-time verification (verify-claims):** confirm the *exact* tool name/params the running server
exposes for "fetch one message by deep-link ID" vs. "read N recent from a channel/thread" before the
skills reference them. Do not hardcode an unverified tool signature — the skills must name tools that
actually resolve via ToolSearch as `mcp__discord__*`.

## Skills

Four skills under `.claude/skills/`, each a `SKILL.md` matching the existing format
(`extract-session-rules/SKILL.md` frontmatter: `name`, `description`, `argument-hint`, `tools`).
Three leaf skills + one orchestrator. All accept a **Discord message/thread link** argument; the read
path is the configured Discord MCP.

### 1. `triage-discord-report` — orchestrator, primary entry point

`/triage-discord-report <discord-link>`. Pipeline:
1. Fetch the report (Discord MCP) → normalize to `{reporter, text, link, attachments}`.
2. Run `find-related-issue` logic → candidate matches with confidence + reasoning.
3. **STOP & report** (chosen default): surface matches + proposed action, do **not** write. User
   decides: comment on an existing issue, file new, or drop. No `gh` write without confirmation.
4. On go-ahead → `create-issue-from-report` (file new) **or** post a comment on the matched issue.
5. Offer `sync-discord-issue` to cross-link.

### 2. `create-issue-from-report`

`/create-issue-from-report <discord-link>`. Procedure:
- Fetch report. Optionally investigate the codebase to root-cause (Explore / general-purpose
  sub-agents), **verifying every load-bearing claim at `file:line`** before it enters the issue.
- Draft to the **bug-report template** sections (reporter POV): root-cause finding folded into
  *Additional context* as "Initial finding (investigation)", never presented as fix scope.
- Labels `bug` + a proposed `priority-N` (with impact reasoning); include the Discord link and an
  attachment placeholder (CLI/MCP can't upload local images — note the drag-drop step).
- **Present the full draft for approval; `gh issue create` only after explicit go-ahead.** Output
  the issue number/URL.

### 3. `find-related-issue`

`/find-related-issue <discord-link-or-text>`. Procedure:
- Fetch/accept the report text. Extract symptom keywords, subsystem, error strings.
- Search **open and closed** issues (`gh issue list --search`, `gh search issues`) across multiple
  angles (symptom phrase, subsystem term, component names) — multi-modal, not one query.
- Return ranked candidates with confidence + matching evidence, and a dedupe recommendation
  (duplicate / related / none). **Read-only — never writes.** Per `subagent-findings-are-claims`,
  open a candidate issue before calling it a duplicate.

### 4. `sync-discord-issue`

`/sync-discord-issue <issue-number> <discord-link>`. Procedure:
- Post the issue link back to the Discord thread (Discord MCP send tool) and/or add a comment on the
  GitHub issue with the Discord source link, so the two are mutually referenced.
- Optionally note status changes (closed / PR-merged) back to the thread.
- **Write action on both sides → requires confirmation before posting.**

## Files to create

| Path | What |
|---|---|
| `.claude/skills/triage-discord-report/SKILL.md` | Orchestrator; stop-and-ask-on-duplicate default. |
| `.claude/skills/create-issue-from-report/SKILL.md` | Report → template-correct issue, human-gated `gh` write. |
| `.claude/skills/find-related-issue/SKILL.md` | Read-only multi-angle dup/related search. |
| `.claude/skills/sync-discord-issue/SKILL.md` | Cross-link GitHub ↔ Discord, write-gated. |
| `.mcp.json` (+ short setup note) | Wire SaseQ/discord-mcp with existing token/guild. |

Reuse: existing skill format (`extract-session-rules/SKILL.md`), bug template
(`.github/ISSUE_TEMPLATE/bug_report copy.yml`), label set (`gh label list`), current `gh` auth, and
the relay bot's existing token/intents (`tools/discord-bot/src/index.ts`).

## Guardrails (baked into the skills)

- **No silent writes.** Every `gh issue create` / comment / Discord post is presented and confirmed
  first (orchestrator default = *Stop & report, ask first*).
- **Verify before asserting.** Root-cause claims in an issue are opened at `file:line` first
  (`subagent-findings-are-claims`, `verify-claims`).
- **Right format for the audience.** Reporter issues → bug template; never paste the maintainer
  `Symptom / Cause / Fix` style into a reporter report.
- **No empty scaffolding** (`holistic-or-explicit-todo`): skills don't reference Discord-MCP tools
  not confirmed to exist; the bot-MCP idea is an explicit deferred plan, not a stubbed dependency.

## Verification

- **Skill format:** each `SKILL.md` parses with valid frontmatter and appears in the `/`-invocable
  list; compare shape against `extract-session-rules/SKILL.md`.
- **MCP wiring:** after adding `.mcp.json`, confirm `mcp__discord__*` tools resolve via ToolSearch
  and the read tool returns this session's report
  (`https://discord.com/channels/947923329057185842/947923329581449238/1512251723052224574`).
- **End-to-end dry run (read-only):** run `/find-related-issue` against the #361 report text → expect
  it to surface #361 (proves dedupe on a known case). Run `/triage-discord-report` on a *fresh* link
  → expect it to stop at the report-and-confirm gate with **no** writes. Only after the gate behaves,
  exercise a real create/sync on a genuine new report.
- **Template fidelity:** the issue produced by `create-issue-from-report` has the four template
  sections + Final checks, `bug` + a `priority-N` label, and the finding under *Additional context*
  — matching #361.
