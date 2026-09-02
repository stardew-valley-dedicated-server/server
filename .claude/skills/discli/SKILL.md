---
name: discli
description: Manages the project's Discord servers from the command line via the `discli` CLI — inspects and adjusts the server itself (channels, FAQ/onboarding content, webhooks, and other settings). Use when the user wants to review or edit Discord content or configuration, e.g. reading FAQ/onboarding pages to improve them, or checking/tweaking webhooks and channel settings. discli is wrapped in a Docker container under `tools/discli-docker/`; invoke it only through `docker compose exec`.
argument-hint: [what to review or adjust on Discord]
allowed-tools: Bash(docker compose *), WebFetch
---

# Using discli

`discli` ([DevRohit06/discli](https://github.com/DevRohit06/discli)) is a Discord CLI we run wrapped in a Docker container, driving our servers' Discord via a bot token.

Primary use is **managing the server itself** — not user interaction or moderation chatter. Typical work:

- Read the **FAQ and onboarding pages** so we can review and improve them together, then apply edits.
- Inspect and make small adjustments to **server settings** — webhooks, channels, and similar.

Other command families (members, roles, threads, reactions, events, polls) exist and are available when needed, but aren't the focus.

## Command reference

Read the command model before running anything non-trivial — how identifiers resolve (`#channel`, `@user`, raw IDs), the global flags, and each command family:

- https://github.com/DevRohit06/discli/blob/main/docs/guides/cli-usage.mdx

Fetch it with WebFetch (raw form: `https://raw.githubusercontent.com/DevRohit06/discli/main/docs/guides/cli-usage.mdx`) when you need an exact command shape.

## How to invoke — always via Docker

The tool lives in `tools/discli-docker/` (`Dockerfile`, `docker-compose.yml`, `.env.example`). It runs as a long-lived idle container; never start a one-off container or install discli locally.

All `docker compose` calls must run from `tools/discli-docker/`. The Bash tool's working directory persists between calls, so a second `cd tools/discli-docker` fails ("No such file or directory") once you're already inside it. Use the absolute path each time to stay call-order-independent: `cd "${CLAUDE_PROJECT_DIR}/tools/discli-docker" && docker compose ...`.

1. Check whether the container is already up — it normally is, since it stays running:

   ```bash
   docker compose ps
   ```

   If it's up (`STATUS` shows `Up ...`), skip straight to step 3.

2. Only if it's not running, start it (first-time setup: copy `.env.example` to `.env` and fill in the token beforehand):

   ```bash
   docker compose up -d
   ```

3. Run every command by exec'ing into the running container:

   ```bash
   docker compose exec discli discli <command...>
   ```

   Examples (from `tools/discli-docker/`):

   ```bash
   docker compose exec discli discli --json message list "#faq" --limit 100
   docker compose exec discli discli message edit "#faq" <message-id> "updated text"
   docker compose exec discli discli --json webhook list "#faq"
   ```

   Confirm exact subcommands and flags against the reference above.

## Navigating to a channel (verified command shapes)

The command families are not the obvious Discord terms — check `discli --help` before guessing. Notably: it's **`server`**, not `guild`; and `channel list` takes the server as a **`--server` flag**, not a positional argument.

Typical FAQ/onboarding lookup, top to bottom:

```bash
docker compose exec discli discli --json server list                       # -> server id + name
docker compose exec discli discli --json channel list --server <id-or-name>  # find the channel + its id
docker compose exec discli discli --json message list "#faq" --limit 100     # read the page(s)
```

`#channel` name references work in `message`/`webhook` commands; use raw ids (or the server name) where a `--server` is needed.

## Conventions

- Pass **`--json`** for anything you need to parse — it emits machine-readable output and meaningful exit codes.
- discli has its own dedicated bot token, separate from the per-server bots. It comes from `tools/discli-docker/.env` (`DISCORD_BOT_TOKEN`) and is already in the container's env, so exec'd commands inherit it. Never pass a token on the command line.
- A bot can only edit its **own** messages — not ones another user or bot authored. To "edit" a human-posted page, repost it through the bot.
