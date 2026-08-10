# Task 2.2: Replace the SMAPI download/install shell with a C# tool

## Goal

Replace the curl + unzip + shell that downloads and installs SMAPI at container startup with a
small self-contained C# tool. Keep the download happening at runtime so SMAPI can be updated by
changing the configured version and restarting — no image rebuild — but do it without a shell or
external download utilities.

## How it works today

`init_smapi` in `docker/modern/rootfs/opt/bin/start-game.sh` uses `curl` to download the SMAPI
installer zip from GitHub, `unzip` to extract it, and pipes `"2\n\n"` into the installer executable
to answer its prompts. This is why the runtime image needs curl, unzip, and a shell — and it runs
at startup specifically so SMAPI can update independently of the image.

## Why a C# tool fits

The repo already ships small self-contained .NET tools built this way — `tools/steam-service/`,
`tools/dll-patcher/`, `tools/netdebug/`, `tools/diagnostics/` — published as trimmed single-file
binaries. A SMAPI bootstrapper is the same pattern, and the runtime already has .NET, so it adds no
new base dependency and slots into a shell-free image cleanly (s6/execline just execs the binary).

The .NET base class library covers everything the shell did:

- **Download** with `HttpClient` — it follows the GitHub release → storage redirect automatically.
- **Extract** with `System.IO.Compression.ZipFile.ExtractToDirectory` — no `unzip`.
- **Install** by starting the SMAPI installer executable (the `SMAPI.Installer` apphost the current
  shell runs) with `Process.Start` and `RedirectStandardInput`, writing the same `"2\n\n"` the shell
  pipes in.

## What to build

1. A new tool (for example `tools/smapi-bootstrap/`) that takes the target SMAPI version (from the
   `SMAPI_VERSION` env, matching today's behaviour) and a game path, and performs download →
   extract → install.
2. Make it **idempotent**: skip if SMAPI is already installed at the expected version, the same way
   `init_smapi` checks for the installed SMAPI executable today.
3. Run it as a startup one-shot — an s6 oneshot service ordered before the game service, or the
   mod's own pre-launch — so runtime updating is preserved.
4. Remove `curl` and `unzip` from the runtime image once this and the other tasks no longer need
   them. (Check other consumers first: the game-download and health paths.)

## Gotcha to handle

`System.IO.Compression.ZipFile` does not restore the Unix executable bit on extraction by default.
The SMAPI and game executables need to be executable, so the tool must set the mode explicitly —
`File.SetUnixFileMode` (available on modern .NET) on the relevant files, or apply the zip entry's
`ExternalAttributes`. This is a small, known step, not a blocker. A C# tool can also do things the
shell did loosely — verify a checksum, handle GitHub rate limits and redirects robustly.

## Build-time vs runtime

The same tool works either way. The production image bakes SMAPI at build; the modern image wants it
at runtime for independent updates. Keep runtime here — the tool just runs at startup. If a future
build-time bake is wanted, the identical binary can run in a build stage.

## Done when

- SMAPI download, extract, and install run through the C# tool with no shell, curl, or unzip.
- Changing the configured SMAPI version and restarting updates SMAPI, no image rebuild.
- Installed files have the correct executable permissions.
