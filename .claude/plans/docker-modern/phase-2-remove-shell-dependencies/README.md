# Phase 2: Remove runtime shell dependencies

## Goal

Remove every runtime dependency on a shell from the modern image, one shippable change at a time,
while still on the Debian base where a shell is present to fall back on for debugging. When this
phase is done, nothing in the running container invokes bash, coreutils, tmux, or the SMAPI stdin
FIFO — which is exactly the precondition for going distroless in phase 3.

## Why now, and why on Debian

The shell in the modern image is wide but shallow. It lives almost entirely in two places: the
interactive operator CLI (tmux split-pane, status bar, command REPL) and one-time boot glue in
`start-game.sh`. Neither is in the actual request-serving path — once running, the server is Xvfb →
openbox → SMAPI/.NET → the HTTP API, all binaries.

Doing this work on the Debian base (phase 1's output) means each shell-removal change can be built,
shipped, and validated with `docker exec sh` still available if something breaks. By the end, the
shell is unused and phase 3 can remove it entirely.

## The tasks (each independently shippable)

1. `01-http-command-api.md` — replace the SMAPI stdin FIFO command path with the mod's HTTP API,
   and move the interactive CLI out of the server container into a client that can also target
   remote servers.
2. `02-smapi-bootstrap-tool.md` — replace the curl+unzip+shell SMAPI download/install with a small
   C# tool, preserving runtime download so SMAPI can update without an image rebuild.
3. `03-s6-execline-conversion.md` — convert the s6 run-script bodies from bash to execline.
4. `04-boot-glue-and-env-validation.md` — move the remaining one-time boot glue and the env-validation
   gate out of shell (into the mod, into build-time, or into a tiny init step).
5. `05-feature-trimming.md` — optional, reversible decisions to drop features that aren't needed
   (screen streaming, audio), each of which also removes packages and CVE surface.

## What makes this safe

Two of the hardest-sounding pieces already exist in the mod's HTTP API
(`mod/JunimoServer/Services/Api/ApiService.cs`): an authenticated WebSocket at `/ws` for real-time
output, and a `POST /test/console` endpoint that already injects SMAPI console commands through the
command manager. So the command-path work is mostly promoting and repurposing existing code, not
building a new mechanism.

## Checklist this phase produces (the phase-3 entry gate)

- [ ] No runtime process reads the SMAPI stdin FIFO; command input is over HTTP.
- [ ] SMAPI download/install is a binary, not curl+unzip+shell.
- [ ] All s6 run-scripts are execline, with no `/bin/bash -c` bodies.
- [ ] Boot glue (game-file linking, steamclient placement, permissions) needs no shell.
- [ ] Env validation runs in the mod, not `start-game.sh`.
- [ ] The interactive CLI runs as a client (local or remote), not inside the server container.
