# Security & Configuration

## Why a Lobby?

A black screen could be modded away client-side. Stardew's pause is server-wide, so pausing for unauthenticated players would freeze everyone.

The lobby implements per-player state isolation at the server level. Unauthenticated players receive a filtered world state while authenticated players continue normally.

## Security Features

### Message Blocking

Unauthenticated players are completely sandboxed:

- ❌ Cannot move furniture
- ❌ Cannot pick up items
- ❌ Cannot use tools
- ❌ Cannot warp anywhere
- ❌ Cannot interact with anything
- ✅ Can only type `!login` or `!help`

All game messages from unauthenticated players are blocked at the network level.

### Exit Blocking

The lobby's exit is physically blocked and all warps removed. Even if message blocking failed, players couldn't walk out.

### Time Isolation

Unauthenticated players receive a frozen time state (12:00 PM) regardless of actual server time. This prevents:

- 2:00 AM passout events
- End-of-day transitions affecting lobbied players
- Time-based events triggering in the lobby

Authenticated players continue with real server time unaffected.

### Sleep Exclusion

Unauthenticated players are excluded from sleep ready-checks. The day ends when authenticated players are ready. Lobbied players can't block day transitions.

### Timeout Protection

Players who don't authenticate are automatically kicked. This prevents:

- AFK players consuming server slots
- Bots or scripts holding connections open
- Forgotten connections

## Example Configurations

### Public Server (Strict)

For servers shared with strangers or a wider community. Short timeout and few login attempts to prevent abuse.

```sh
# .env
SERVER_PASSWORD=MySecurePass123
MAX_LOGIN_ATTEMPTS=2
AUTH_TIMEOUT_SECONDS=60
```

```json
// server-settings.json
{
    "Server": {
        "LobbyMode": "Individual",
        "ActiveLobbyLayout": "welcome"
    }
}
```

### Community Server (Relaxed)

For servers shared with a known community. More forgiving settings for players who might be slow to type.

```sh
# .env
SERVER_PASSWORD=CommunityPass
MAX_LOGIN_ATTEMPTS=5
AUTH_TIMEOUT_SECONDS=180
```

```json
// server-settings.json
{
    "Server": {
        "LobbyMode": "Shared",
        "ActiveLobbyLayout": "cozy-hangout"
    }
}
```

## Troubleshooting

### Players Can't See the Lobby

- Verify `SERVER_PASSWORD` is set in `.env`
- Restart the server after changing password settings
- Check logs for `[Lobby] Initialized` message

### Layout Not Applying

- Confirm you ran `!lobby set <name>` to activate it
- Check `!lobby list` to verify the layout exists
- The layout applies to new connections, not already-connected players

### Spawn Point Inside Furniture

Use `!lobby edit <name>`, move to a clear spot, run `!lobby spawn`, then `!lobby save`.

