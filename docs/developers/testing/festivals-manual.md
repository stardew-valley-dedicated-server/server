# Manual Testing: Festivals

Runbook for manually reproducing festival behavior. The sleep-through requirement (do not use `/debug day` to skip to a festival day) is the rule at [.claude/rules/host-automation.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/host-automation.md), item 3.

## Quick Setup

1. Start server with a new or existing save
2. Connect a client
3. Use debug commands to get close to a festival:
   ```
   /debug day 12
   /debug season spring
   ```
4. **Sleep through the night** to reach day 13 (Egg Festival)

## Festival Dates

| Festival | Date | Time Window | Location |
|----------|------|-------------|----------|
| Egg Festival | Spring 13 | 9:00-14:00 | Town |
| Flower Dance | Spring 24 | 9:00-14:00 | Forest |
| Luau | Summer 11 | 9:00-14:00 | Beach |
| Dance of Moonlight Jellies | Summer 28 | 22:00-24:00 | Beach |
| Stardew Valley Fair | Fall 16 | 9:00-15:00 | Town |
| Spirit's Eve | Fall 27 | 22:00-23:50 | Town |
| Festival of Ice | Winter 8 | 9:00-14:00 | Forest |
| Feast of the Winterstar | Winter 25 | 9:00-14:00 | Town |

## Test Scenarios

### 1. Festival Entry
- [ ] Client walks to festival location
- [ ] Client sees "Waiting for players..." dialog
- [ ] Host automatically warps to festival
- [ ] Both players enter festival together

### 2. Main Event (with countdown)
- [ ] Chat announces countdown timer
- [ ] `!event` command skips countdown
- [ ] Main event starts automatically after countdown

### 3. Festival Exit
- [ ] Client clicks "Leave" at festival
- [ ] Host automatically triggers leave dialogue
- [ ] Both players exit festival properly
- [ ] Day continues normally (or ends for evening festivals)

## Common Issues

### Host doesn't warp
- Check that there is at least one connected player (host warps only when others are present)
- Check that the host is not already at the festival
- Check that the host is not already warping

### Player stuck at "Waiting for players..."
- Host must be detected as ready
- Check server logs for `SetLocalReady` call
