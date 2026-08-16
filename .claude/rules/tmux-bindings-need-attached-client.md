---
paths:
  - "docker/rootfs/opt/base/bin/**"
  - "docker/modern/rootfs/opt/bin/**"
  - "tests/JunimoServer.Tests/AttachCliTests.cs"
  - "tests/JunimoServer.Tests/CommandCatalogTests.cs"
---

# Verify attach-cli key bindings through an attached tmux client — send-keys bypasses them

`tmux send-keys` injects keystrokes directly into a pane's pty and never consults key-table
bindings, so it cannot exercise a `bind-key -n` (root table) binding like attach-cli's
pane-conditional TAB. To test a binding end to end, type through a real client: create the target
session, then a driver session whose pane runs `TMUX= tmux attach -t <target>`, and `send-keys`
to the **driver** — its client forwards keys through the target's key tables.

**Why:** The TAB-completion feature's make-or-break check was "does TAB reach readline in the
input pane, or get eaten by the tmux bind?" — a plain-send-keys probe answers it wrong in both
directions, silently passing with a broken binding and never exercising the pane-conditional
branch. The nested-driver probe verified both branches for real (output pane → `select-pane`,
input pane → passthrough to the shell) and also caught a regression no static read would find:
the forwarded TAB reaching `read_password`'s raw loop as a literal character.

**How to apply:** The whole attach-cli behavior matrix (ghost text, TAB completion, password mode,
pane focus, FIFO forwarding) is testable without building the images — mount the real scripts from
the rootfs tree into a stock alpine/debian container with zsh + tmux (bash too, for the other
scripts), replicate the 2-pane topology (`split-window -l 2`, the loop launched as
`zsh <path>/server-command-loop`), drive scenarios via the attached driver, and read results with
`capture-pane -p -e` (`-e` keeps the escape sequences the ghost highlight rides on). The input
pane's ZLE behavior reproduces faithfully there (same stack as the images — this is also how the
earlier readline fact, "`read -e` never consults `complete -F`", was established on the old bash
stack). Reach for this whenever editing attach-cli, server-command-loop, or server-completion.zsh.
