# Task 2.3: Convert the s6 run-scripts from bash to execline

## Goal

Rewrite the bodies of the s6-overlay service scripts so they no longer call bash. They already use
execline as their interpreter; they just take a shortcut into `/bin/bash -c` for the actual logic.
Removing that shortcut removes bash from the boot path.

## What they look like today

Each service under `docker/modern/rootfs/etc/s6-overlay/s6-rc.d/*/run` starts with the execline
shebang (`#!/command/execlineb -P`) and then immediately runs `/bin/bash -c "..."`. The bash bodies
are simple:

- `xvfb/run` — exec Xvfb with the configured resolution.
- `openbox/run` — poll `xdpyinfo` until the X server answers, then exec openbox.
- `streaming/run` — poll `xdpyinfo`, then exec go2rtc.
- `game/run` — poll `xdpyinfo`, then exec `start-game.sh`.
- `pipewire/run` — already nearly execline (exports one variable, execs pipewire).
- `init-runtime/up` — already execline.

## What to do

Rewrite the bash bodies in pure execline, which ships with s6-overlay and needs no shell:

- The plain "exec this binary with these args" cases (xvfb, pipewire) are direct execline.
- The "wait for X, then exec" cases (openbox, streaming, game) become either an execline retry loop
  around the readiness check, or — better — an s6 readiness dependency so the dependent service only
  starts once X is up, removing the poll entirely. Prefer the dependency approach where it fits the
  s6-rc model; fall back to an execline loop where a poll is genuinely needed.

The `game/run` script's target (`start-game.sh`) is itself shell today — task 2.2 removes its SMAPI
install, and task 2.4 removes the rest of its boot glue, after which `game/run` execs the game (or a
tiny init) directly rather than a bash script.

## Note on readiness checks

`xdpyinfo` is the readiness probe today. If the s6 dependency approach replaces the polling, confirm
the X server genuinely signals readiness at the right point; otherwise keep a minimal execline poll.
The goal is no bash, not necessarily no polling.

## Done when

- No `run` or `up` script under `docker/modern/rootfs/etc/s6-overlay/s6-rc.d/` calls `/bin/bash`.
- Service startup ordering (X before the WM, streaming, and game) still holds.
