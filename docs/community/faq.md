# FAQ

Common questions about JunimoServer.

## General

### Is JunimoServer free?

Yes. JunimoServer is open-source and free to use. You need to own a copy of Stardew Valley on Steam (the server downloads game files using your Steam account).

### Can I use my existing single-player save?

Yes. See [Backup & Recovery](/features/backup#importing-existing-saves) for import instructions.

### How many players can join?

Configurable via `MaxPlayers` in server settings. Default is 10. The game creates cabins automatically as players join.

### Does it work with GOG?

Yes, but with limitations. GOG connections have ~50% success rate compared to Steam's ~99%. This is due to NAT traversal differences. Steam players should use Steam invite codes, GOG players use GOG invite codes.

## Gameplay

### What happens to my crops if I can't log in?

JunimoServer includes **Crop Preservation** — crops track their owner and won't die from lack of watering while you're offline. Season-end death is also delayed. Your crops are safe until you return.

### Does time pass when I'm offline?

Time pauses when no players are online. When players are connected, time runs normally. The game saves at the end of each in-game day.

### Can I move my cabin?

Yes. Stand where you want your cabin, then type `!cabin` in chat.

### Is money shared or separate?

Configurable by the server admin. Default is shared wallet (all players share one money pool). Admins can toggle this with `!changewallet`.

## Technical

### Can I play on my own computer while hosting a server?

Yes, but use a **separate Steam account** for the server. Steam doesn't allow the same account to be logged in twice simultaneously.

### What are the server requirements?

- Docker Engine 20+ with Compose V2
- 2 GB RAM minimum (4 GB recommended)
- Dual-core CPU minimum
- 1-2 GB disk space

### Can I run the server on Windows?

Yes. JunimoServer runs on both Linux and Windows via Docker.

### Do players need to install anything special?

No. Players connect using the normal Stardew Valley multiplayer menu with an invite code. Content mods (if any) must match between server and players.

## Mods

### Can I use mods?

Yes. JunimoServer supports SMAPI mods. See [Mod Support](/features/mods).

### Do all players need the same mods?

Depends on the mod type:
- **Server-only mods** — Only on server
- **Content mods** (new items, NPCs, maps) — Server AND all players
- **Client-only mods** (UI tweaks) — Individual players

## Troubleshooting

### Players can't connect

1. Verify server is running: `docker compose ps`
2. Check invite code is correct
3. See [Troubleshooting](/admins/troubleshooting#player-connection-issues)

### Server won't start

Check logs: `docker compose logs -f`

Common causes:
- Docker not running
- Invalid Steam credentials
- Port conflicts

See [Troubleshooting](/admins/troubleshooting) for detailed solutions.

## Still Have Questions?

- [Discord](https://discord.gg/w23GVXdSF7) — Community chat
- [GitHub Issues](https://github.com/stardew-valley-dedicated-server/server/issues) — Bug reports and feature requests
