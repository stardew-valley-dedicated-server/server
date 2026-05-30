# Feature Ideas Backlog

> **Related GitHub Issues:**
> - [#47 - Delay Unnamed Farmhand notification until name is available](https://github.com/stardew-valley-dedicated-server/server/issues/47)
> - [#46 - Third-party mods can block server sleep indefinitely](https://github.com/stardew-valley-dedicated-server/server/issues/46)
> - [#43 - Discord integration](https://github.com/stardew-valley-dedicated-server/server/issues/43)
> - [#42 - Anticheat](https://github.com/stardew-valley-dedicated-server/server/issues/42)
> - [#41 - Display cabin owner](https://github.com/stardew-valley-dedicated-server/server/issues/41)
> - [#40 - Server tweaks](https://github.com/stardew-valley-dedicated-server/server/issues/40)
> - [#39 - Update checks](https://github.com/stardew-valley-dedicated-server/server/issues/39)
> - [#36 - Admin indicator](https://github.com/stardew-valley-dedicated-server/server/issues/36)
> - [#35 - Backups](https://github.com/stardew-valley-dedicated-server/server/issues/35)
> - [#33 - Inconsistent behavior for host movement and pause conditions](https://github.com/stardew-valley-dedicated-server/server/issues/33)
> - [#21 - Prevent sending connected IPs to clients](https://github.com/stardew-valley-dedicated-server/server/issues/21)
> - [#20 - Leverage DedicatedServer feature from SDV 1.6.15](https://github.com/stardew-valley-dedicated-server/server/issues/20)

A collection of potential features for future consideration. These are ideas that would differentiate this project as a true **dedicated server** platform rather than just a modded game.

---

## Server Administration

### Scheduled Automation
- **Auto-sleep**: Progress day at configurable time even with AFK players
- **Scheduled restarts**: With countdown warnings to players
- **Auto-backup**: Configurable intervals with rotation (keep last N)
- **MOTD/Announcements**: Scheduled or on-join messages

### Player Management
- **Whitelist/Blacklist**: By Steam ID, Discord ID, or invite code
- **Activity tracking**: Last seen, total playtime, session count
- **AFK handling**: Auto-kick or teleport to lobby after timeout
- **Tiered roles**: Admin → Moderator → Trusted → Default → Restricted
- **Temp bans**: Auto-expiry after configured duration

---

## Persistence & World Management

### Time Control
- **Pause when empty**: Freeze time when no players online
- **Day length multiplier**: 2x, 3x longer days for casual servers
- **Eternal season**: Lock season for building/events

### Save Snapshots
- **Manual snapshots**: `junimo snapshot create "before-experiment"`
- **Auto-snapshot**: Before risky operations or on schedule
- **Rollback**: Restore any snapshot via console
- **Diff viewer**: What changed between two snapshots

### Seasonal Resets
- **Soft reset**: Wipe crops/forage, keep buildings and relationships
- **Year reset**: Optional fresh start each in-game year
- **Hardcore mode**: Permadeath for unharvested crops

---

## Economy & Balance

### Starter Kits
- Configurable items/money for new players on first join
- Role-based kits (trusted players get better start)
- One-time or per-season grants

### Economy Controls
- **Global price modifier**: Server-wide sell price multiplier
- **Per-item overrides**: Config file for custom prices
- **Money cap**: Per-player maximum (anti-hoarding)
- **Tax system**: % of sales goes to community fund

---

## Communication & Integration

### Discord Integration
- **Webhooks**: Player join/leave, achievements, festivals, deaths
- **Chat bridge**: Two-way Discord ↔ in-game chat
- **Status bot**: `!status` command shows online players, season, time
- **Role sync**: Discord role grants in-game permissions

### Admin Tools
- **Server mail**: Send in-game mail to all or specific players
- **Teleport**: Move players to any location
- **Item spawn**: For events/rewards/testing
- **Spectator mode**: Invisible observation

---

## Gameplay Enhancements

### Shared Infrastructure
- **Community chest**: Shared storage with configurable permissions
- **Shared greenhouse**: Persists across farm stacking
- **Community board**: Player-to-player trade/request system

### Warp Network
- **Unlockable waypoints**: Earn fast-travel points
- **Player-placed warps**: Public teleport destinations
- **Cross-farm warping**: Visit other players' farms (with permissions)

### Competitions & Events
- **Admin-triggered events**: Fishing/farming/mining competitions
- **Leaderboards**: Most gold, items shipped, fish caught, etc.
- **Seasonal awards**: Cosmetic rewards for top performers

---

## API & Extensibility

### Webhook System
- Configurable outgoing webhooks for game events
- Event types: crop harvested, building built, player married, etc.
- Customizable payloads, rate limiting, retry logic

### Live Map Viewer
- Web endpoint serving farm layout as image or JSON
- Player positions (opt-in)
- Building and crop status overlay

### External Economy Bridge
- API for querying/modifying player inventories and money
- Integration hooks for Discord bots, Patreon, etc.
- "Server shop" for out-of-game rewards

---

## Priority Recommendations

| Priority | Feature | Effort | Impact |
|----------|---------|--------|--------|
| 1 | Discord Webhooks | Low | High — players love activity feeds |
| 2 | Auto-Sleep | Medium | High — solves AFK blocking progression |
| 3 | Save Snapshots | Medium | High — admin confidence for rollback |
| 4 | Starter Kits | Low | Medium — improves onboarding |
| 5 | Whitelist/Roles | Medium | High — required for public servers |
| 6 | Time Pause When Empty | Low | Medium — saves resources, logical behavior |
| 7 | Activity Tracking | Low | Medium — useful admin visibility |
| 8 | Community Chest | Medium | Medium — encourages collaboration |

---

## Notes

- Many features overlap with existing task files (e.g., Discord auth in `server-discord-auth.md`)
- Prioritize features that are **impossible or awkward without a dedicated server**
- Consider config-driven enable/disable for all features (don't force complexity)
- API features enable community tools without core changes
