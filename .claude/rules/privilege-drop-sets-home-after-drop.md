---
paths:
  - "docker/**"
  - "tools/**/Dockerfile*"
  - "tools/**/entrypoint.sh"
---

# A root-then-drop entrypoint sets HOME after the drop, and proves itself by booting as a non-root uid

`gosu` and `su-exec` replace `HOME` with the target uid's passwd home — `/` when the uid has no `/etc/passwd` entry — so any `export HOME=...` placed before the drop is lost. Set it on the far side (`exec gosu "$uid:$gid" env HOME=/tmp <cmd>`), pre-create every directory the dropped process will write while still root, and verify by running the image with an arbitrary uid such as `1234`, not just the default.

**Why:** Three entrypoints exported `HOME=/tmp` and then dropped via gosu/su-exec; inside the dropped process `HOME` was `/`, which showed up as `//.cache` permission errors and a game that could not create its config folder. The static review had passed all three. Separately, a non-root Xvfb needed `/tmp/.X11-unix` created by root first.

**How to apply:** When writing or reviewing a `USER_ID`-style drop, place `env HOME=...` after the drop command, list what the app writes on first run (config dir, cache, X socket dir) and `mkdir`/`chown` it before dropping, then boot the image once with `-e USER_ID=1234` and read the log. On a Windows host, bind-mounted folders always appear root-owned inside the container, so check file ownership against a named volume instead of a host folder.
