# Server Mechanics

JunimoServer runs your farm 24/7 without needing a player to host. The server includes automatic behaviors that handle game progression and edge cases silently in the background.

## Host Player

The server runs an invisible "host" player that handles game progression:

| Behavior | What It Does |
|----------|--------------|
| **Auto-sleep** | Host goes to bed when all players are sleeping |
| **Auto-pause** | Game pauses when no players are online |
| **Event skipping** | Cutscenes are automatically skipped (festivals excluded) |
| **Menu handling** | Dialogs, level-ups, and shipping menus are dismissed |
| **Mailbox checking** | Mail is automatically checked each morning |

### Pause Behavior

- **Players online**: Game runs normally
- **No players**: Game pauses between 6:10 AM and 1:00 AM
- **After 1:00 AM**: Game unpauses to allow the forced pass-out at 2:00 AM

### Progression Unlocks

The host automatically unlocks certain progression items:

- Pet naming
- Cave choice (mushrooms or bats)
- Community Center door
- Sewers key (at 60+ museum artifacts)
- Fishing rod (Day 2)

## Crop Preservation

Crops track their owner (the player who planted them). When owners go offline:

- Unwatered crops get extra days instead of dying
- Season-end death is delayed while the owner is offline
- Normal rules apply once the owner returns

This prevents crops dying because you couldn't log in.

## Desync Protection

Network desyncs can freeze the game. The server automatically kicks stuck players:

| Situation | Timeout | Action |
|-----------|---------|--------|
| Stuck at day transition | 20 seconds | Kicked |
| Not ready during save | 60 seconds | Kicked |

Kicked players can reconnect immediately — progress is not lost.

## Chest Protection

To prevent griefing, the server locks player storage:

- Players cannot access other players' cabin chests
- Fridges and inventories are protected
- Only the cabin owner can access their storage

## Festival Handling

- Host automatically joins festivals
- Players participate normally
- If stuck, use the `!event` command to force-start

## Related

- [Backup & Recovery](/features/backup) — Protect your farm
- [Troubleshooting](/admins/troubleshooting) — Common issues
