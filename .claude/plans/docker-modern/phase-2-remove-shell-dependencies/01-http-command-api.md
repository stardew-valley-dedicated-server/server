# Task 2.1: Move the command path from the FIFO to HTTP

## Goal

Stop feeding server commands through the SMAPI stdin FIFO and the shell CLI that writes to it.
Instead, drive commands over the mod's existing HTTP API, and turn the interactive CLI into a
client that runs on demand — including against remote servers.

## How commands work today

The launch in `docker/modern/rootfs/opt/bin/start-game.sh` runs SMAPI with its stdin coming from a
named pipe (`tail -f` on `/tmp/smapi-input` piped into SMAPI, wrapped in `script` to get a PTY for
coloured output). Anything written to that FIFO becomes a SMAPI console command. Two shell pieces
write to it:

- `docker/modern/rootfs/opt/bin/server-command-loop` — the interactive REPL in the tmux bottom pane.
- `docker/modern/rootfs/opt/bin/toggle-rendering.sh` — writes `rendering on/off/toggle`.

The whole interactive UI lives in `docker/modern/rootfs/opt/bin/attach-cli` (tmux split-pane, the
`mem-cpu.sh` status bar, the invite-code display).

## What already exists to replace it

The mod's HTTP API (`mod/JunimoServer/Services/Api/ApiService.cs`) already has the two hard pieces:

- An authenticated **WebSocket at `/ws`** — a real-time channel (today it carries chat relay for
  the Discord bot). This is the stream a CLI uses for live log/output.
- A **`POST /test/console`** endpoint that injects a console command name + args through SMAPI's
  command manager (models in `mod/JunimoServer/Services/Api/ApiService.TestEndpoints.Models.cs`).
  The tricky part — invoking a registered SMAPI console command from outside the console, with the
  right threading — is already solved here (see `.claude/rules/smapi-api-surface.md` for why this
  needs reflection into `SCore` → `CommandManager`, and that the callback runs off the game thread).

## What to build

1. **Promote console injection to a production endpoint.** Add an authenticated `POST /console`
   (or promote `/test/console` out of the test-only gate) that runs an arbitrary SMAPI console
   command. Keep it behind `API_KEY` auth like the rest of the API.
2. **Point the CLI at HTTP.** Rework the interactive CLI to submit commands via `POST /console` and
   stream output over `/ws`. This client no longer needs to live inside the server container — it
   can be a separate sidecar, or a local tool run from a laptop. This lines up with the existing
   `.claude/plans/features/cli-rewrite-v4.md`.
3. **Replace `toggle-rendering.sh`** with a call to the API (a rendering endpoint likely already
   exists in `ApiService.cs`; use it, or add one).
4. **Drop the FIFO wrapper at launch.** Once nothing writes commands to stdin, SMAPI can be exec'd
   directly instead of through `script` + `tail -f FIFO`. This also removes two always-on processes
   plus a PTY.
5. **Retire the shell CLI files** — `attach-cli`, `server-command-loop`, `toggle-rendering.sh`,
   `mem-cpu.sh` — from the image once the client replaces them.

## Benefits this unlocks (beyond removing shell)

- **Runs only when used.** The persistent FIFO reader and PTY that ran for the server's whole life
  go away; the CLI client's processes exist only while someone is using it. The overhead saving is
  small and honest — those were cheap — but the decoupling is real.
- **Runs locally against remote servers.** An HTTP + WebSocket client with an `API_KEY` is
  location-independent: one CLI can manage any number of remote servers, switching by URL, with no
  `docker exec` and no SSH into a container. The API already enforces `API_KEY`, so remote control
  is already secured.

## Caveat to design around

SMAPI console commands write their output to the log/monitor, not as a return value. So
`POST /console` returns "accepted", and the command's textual output arrives over `/ws` (or a log
tail) — not as a tidy per-request HTTP response for arbitrary commands. This is the same input-pane
/ output-pane split the tmux CLI already has, so it maps naturally; just don't design the client to
expect the command output back in the POST response. The mod's own operations that already have
dedicated REST endpoints do return structured responses — this caveat is only about arbitrary
SMAPI-console passthrough.

## Done when

- An authenticated production endpoint injects SMAPI console commands.
- The CLI submits over HTTP and streams over `/ws`, and works against a remote server.
- The FIFO, the `script`/`tail` launch wrapper, and the shell CLI files are gone.
