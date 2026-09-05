---
description: Join the public JunimoServer test server — live status, player count, and invite codes for Steam and GOG.
---

# Public Test Server

The project runs a public test server so you can try JunimoServer before hosting your own. It runs the latest preview build, so expect occasional restarts and the odd rough edge. Anything that looks broken is worth a [bug report](/community/reporting-bugs).

<ServerStatusWidget api-url="https://junimoserver-status.REPLACE-WITH-ACCOUNT-SUBDOMAIN.workers.dev/" title="Public Test Server" />

## How to join

1. Copy the invite code for your platform above. Steam players need the code starting with `S`, GOG players the one starting with `G`.
2. In Stardew Valley, open **Co-op → Join → Enter Invite Code** and paste it.
3. If the lobby asks for a password, get the current one from the `#test-server` channel on [Discord](https://discord.gg/w23GVXdSF7).

The status above refreshes every 30 seconds. "Offline" means the server process is up but no farm is loaded, and "Stale" means the server stopped reporting; both usually resolve within a few minutes.

## Why the lobby is public

The invite code is not access control: the lobby is visible to anyone, and the password is what gates entry. The [FAQ](/community/faq#why-is-my-server-s-lobby-always-public) explains why JunimoServer forces public lobbies, and [Password Protection](/features/password-protection/) covers the gate itself.
