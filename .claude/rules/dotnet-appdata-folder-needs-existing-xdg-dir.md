---
paths:
  - "docker/**"
  - "mod/**/*.cs"
  - "tests/test-client/**"
  - "tools/xnb-unpacker/**"
---

# `GetFolderPath(ApplicationData)` returns "" unless the XDG config dir already exists

On Linux, `Environment.GetFolderPath(SpecialFolder.ApplicationData)` returns an empty string when `$XDG_CONFIG_HOME` (or `$HOME/.config`) does not exist. The game and SMAPI join `StardewValley/...` onto that result, so every write lands relative to the process's working directory. Root hides the bug because it can write anywhere; a non-root uid gets `Permission denied` and SMAPI aborts before loading mods. Any container that runs the game must create the XDG config dir (owned by the app user) before the game starts.

**Why:** After the containers moved to uid 1000, every E2E client died with `Access to the path '/etc/services.d/app/StardewValley/ErrorLogs' is denied` — the jlesage app service's cwd. The server image was unaffected only because the saves volume mounts inside `/config/xdg/config/StardewValley`, which creates the dir as a side effect. Two full E2E runs were spent before the mechanism was read from the decompiled `Program.GetAppDataFolder`.

**How to apply:** In a root init hook or entrypoint, `mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}"` before dropping privileges, on every image that runs the game or a SMAPI-hosted process (server, test client, unpacker). When a game-side write ends up at a path relative to the service directory, suspect an empty `GetFolderPath` result before suspecting the caller.
