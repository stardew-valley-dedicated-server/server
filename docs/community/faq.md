# FAQ

## General

### Is JunimoServer free?

Yes. JunimoServer is open-source and free to use. You need to own a copy of Stardew Valley on Steam (the server downloads game files using your Steam account).

### Can I use my existing save?

Yes. Copy it onto the server and run `saves import` (see [Importing Saves](/admins/operations/importing-saves)). To keep the original owner a player instead of letting the server take over their farmer, add `--swap-host-to`.

### How many players can join?

Configurable via `MaxPlayers` in server settings. Default is 10. The game creates cabins automatically as players join.

### Does it work with GOG?

Yes, but with limitations. GOG connections have ~50% success rate compared to Steam's ~99%. This is due to NAT traversal differences. Steam players should use Steam invite codes, GOG players use GOG invite codes.

## Gameplay

### What happens to my crops if I can't log in?

Crops track their owner and won't die from lack of watering while you're offline. Season-end death is also delayed.

### Does time pass when I'm offline?

Time pauses when no players are online. When players are connected, time runs normally. The game saves at the end of each in-game day.

### Can I move my cabin?

Yes. Stand where you want your cabin, then type `!cabin` in chat.

### Is money shared or separate?

Configurable by the server admin. Default is shared wallet (all players share one money pool). Admins can switch it with `!changewallet shared` / `!changewallet separate`; the change applies overnight.

### Can another player take over my farmer?

No. Your farmer is locked to the connection identity that created it — for Steam/GOG players that means your account, and other players don't even see it in their farmer list. (Steam note: joining with the G-prefixed GOG invite code counts as a different identity than joining via the friends list or the S-prefixed code, so stick to one method.) The exception is farmers created over a direct IP connection: direct IP carries no account identity, so those farmers are shared among all direct-IP players. Server admins can transfer or unlock a farmer with the [`farmhand` command](/admins/operations/commands#farmhand).

## Technical

### Can I play on my own computer while hosting a server?

Yes, but use a **separate Steam account** for the server. Steam doesn't allow the same account to be logged in twice simultaneously.

### What are the server requirements?

- Docker Engine 20+ with Compose V2
- 2 GB RAM minimum (4 GB recommended)
- Dual-core CPU minimum
- 1-2 GB disk space

These are ballpark estimates. Actual requirements vary based on mods and player count.

### Can I run the server on Windows?

Yes. JunimoServer runs on both Linux and Windows via Docker.

### Do players need to install anything special?

No. Players connect using the normal Stardew Valley multiplayer menu with an invite code. Content mods (if any) must match between server and players.

### Why is my server's lobby always public?

By design. In normal Stardew, the host's lobby defaults to "Friends Only," which lets only the host's Steam/GOG friends join. A dedicated server runs on a separate account that your players aren't friends with, so "Friends Only" would block everyone — the invite code alone wouldn't be enough. To keep invite codes working for anyone you share them with, JunimoServer forces the lobby Public. Access control comes from keeping the invite code private (and optionally [password protection](/features/password-protection/)), not from lobby visibility.

## Mods

### Can I use mods?

Yes. JunimoServer supports SMAPI mods. See [Mod Support](/features/mods).

### Do all players need the same mods?

Depends on the mod type:
- **Server-only mods**: Only on server
- **Content mods** (new items, NPCs, maps): Server AND all players
- **Client-only mods** (UI tweaks): Individual players

## Troubleshooting

### VNC shows a black screen. Is the server broken?

No. The server is working correctly. By default, `SERVER_FPS=0` which means the server doesn't draw graphics to its own display (saving CPU). Players always see the game normally on their own screens. Connect with your game client to verify.

You don't need VNC to play or manage the server. Use the CLI (`docker compose exec server attach-cli`) for server commands.

See [VNC](/admins/operations/vnc#why-is-vnc-showing-a-black-screen) if you specifically need to enable the display for debugging.

### Players can't connect

1. Verify server is running: `docker compose ps`
2. Check invite code is correct
3. See [Troubleshooting](/admins/troubleshooting#player-connection-issues)

### I connected, but my farmer isn't in the list

You most likely joined a different way than when you created the farmer. Farmers are tied to how you connect: one created via a Steam or GOG invite code belongs to that account and never shows up over direct IP (and the other way around — a farmer created over direct IP is only offered to direct-IP players). On Steam, the G-prefixed (GOG) invite code counts as a different join method than the friends list or the S-prefixed code. Reconnect the same way you originally joined. If you genuinely need to move a farmer to another account or connection method, ask the server admin to use the [`farmhand` command](/admins/operations/commands#farmhand).

### Server won't start

Check logs: `docker compose logs -f`

Common causes:
- Docker not running
- Invalid Steam credentials
- Port conflicts

See [Troubleshooting](/admins/troubleshooting) for detailed solutions.

### "Asset does not appear to be a valid XNB file"

Log errors like `Failed to spawn NPC '...'` or `Couldn't create the '...' location` with `ContentLoadException: Asset does not appear to be a valid XNB file` mean a game content file on disk is corrupted (for example from an interrupted download). The server doesn't repair game files on its own, so the error returns every restart until you re-run the downloader. It validates all game files and re-downloads the broken pieces:

```bash
docker compose run --rm steam-auth download
docker compose restart server
```

If it fails with a login error, run `docker compose run --rm -it steam-auth setup` first.

