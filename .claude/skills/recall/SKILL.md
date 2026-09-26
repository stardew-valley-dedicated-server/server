---
name: recall
description: Searches this repository's past Claude Code session transcripts (main checkout and worktrees) and prints the matching messages with date, session id, and title. Use when the user asks how, when, or whether something was done or discussed in an earlier session.
argument-hint: <words to find> [--session <id>] [--all] [--limit N] [--wide]
---

# Search past sessions

```bash
node "${CLAUDE_SKILL_DIR}/scripts/recall.mjs" $ARGUMENTS
```

- A message matches when it contains every word, ignoring case. It searches the user's prompts, Claude's replies, and tool calls (commands, file paths), but not tool output or earlier recall searches.
- Results are grouped by session, newest first. The printed id works with `claude --resume <id>`.
- Search for distinctive words: identifiers, error text, PR numbers, a phrase from the message. Add words when there are too many hits. Quote a phrase or a Windows path so it reaches the script as one word: `'repos\server\README.md'`.
- `--session <id prefix>` lists every match in one session, `--wide` prints longer excerpts, `--all` searches every project, and `--limit N` caps the number of matches (default 30, no cap with `--session`).
- Transcripts are deleted after `cleanupPeriodDays` (30 by default), so older sessions won't show up.
