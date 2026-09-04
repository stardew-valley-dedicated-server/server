# Task 2.4: Move the remaining boot glue and env validation out of shell

## Goal

Remove what's left of `docker/modern/rootfs/opt/bin/start-game.sh` after tasks 2.1 (FIFO→HTTP) and
2.2 (SMAPI tool) have taken the command path and the SMAPI install out of it. What remains is
one-time boot filesystem glue and an environment-validation gate — none of which fundamentally needs
a shell.

## What remains in start-game.sh

After the earlier tasks, the shell functions still present are:

- `validate_environment` — refuses to start if the API is enabled without an `API_KEY`, unless
  `ALLOW_INSECURE_SETUP=true`.
- `init_stardew` — symlinks the game files from the shared volume into place, waiting in a loop if
  the steam-auth sidecar hasn't produced them yet.
- `init_steam_sdk` — places `steamclient.so` (the real one now, after phase 1) and writes
  `steam_appid.txt`.
- `init_mods` — copies mods into the mods path.
- `init_permissions` — sets executable bits and ownership on the game files.
- The launch itself — after task 2.1, exec the game/SMAPI directly.

## Where each piece should go

- **`validate_environment` → the mod.** This belongs in `mod/JunimoServer/Env.cs` (or a startup
  check in the mod) as a fail-fast, so the rule lives in one place with the other env parsing rather
  than duplicated in a shell script. Note `.claude/rules/universal/verify-claims.md`:
  test any new fail-fast against the committed compose config, not just the example.
- **`init_stardew`, `init_steam_sdk`, `init_mods`, `init_permissions` → build-time where static,
  a tiny init where dynamic.** Mod copying and permissions can largely be baked at build. The
  genuinely runtime part is linking the game files that the steam-auth sidecar produces at runtime,
  and placing `steamclient.so` — a handful of filesystem operations. Options, in order of preference:
  fold into the SMAPI bootstrap tool from task 2.2 (it already runs as a startup one-shot in .NET),
  express as execline in the s6 init, or a small dedicated init binary. Pick one home; don't split
  across several.

## Guidance

Prefer folding the dynamic filesystem glue into the existing startup one-shot (the SMAPI bootstrap
tool) over creating a new mechanism — it already runs at the right time, in .NET, with filesystem
access. That keeps the boot path to: one .NET one-shot for setup, then the game service. Once this
task lands, `start-game.sh` is gone entirely.

## Done when

- `start-game.sh` no longer exists.
- Env validation runs in the mod against the committed config.
- Game-file linking and steamclient placement happen with no shell.
- Time sync is either dropped or a shell-free one-shot.
