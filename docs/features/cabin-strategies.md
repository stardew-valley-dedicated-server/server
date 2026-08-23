# Cabin Strategies

Control how player cabins are placed and managed on your farm.

## Strategies

| Strategy | Description |
|----------|-------------|
| `CabinStack` | Cabins hidden off-map. Each player sees only their own cabin at a shared position. |
| `FarmhouseStack` | Cabins hidden off-map. All players exit at the main farmhouse's front door (shared entry point). |
| `None` | Vanilla behavior. Cabins placed at real farm positions. |

## CabinStack (Default)

Cabins exist but are moved off the visible map. When a player warps to "their cabin," they go to the hidden location. From the player's perspective, they have a cabin; it's just not cluttering the farm.

Benefits:
- Clean farm layout regardless of player count
- No cabin placement conflicts
- Each player still has their own private space

Admins can move the shared spot every player sees their cabin at: `cabins stackspot` (server console) shows the current spot, `cabins stackspot <x> <y>` sets it, and the in-game `!stackspot` / `!stackspot place` admin chat command does the same visually. Connected players see the new spot after they reconnect.

## FarmhouseStack

Similar to CabinStack — every player still has their own hidden cabin interior, inventory, and bed — but each cabin's exit is redirected to the main farmhouse's front door on the farm, so everyone steps out at the same spot. The main farmhouse interior itself stays reserved for the server host; a player who walks into it is sent back to their own cabin.

Use this for a more communal feel where everyone congregates at one front door.

## None (Vanilla)

Standard Stardew Valley behavior. Cabins are placed on the farm at the map's designated cabin spots. Use this if you want the traditional multiplayer experience or need cabins at specific positions.

::: warning Player ceiling
Each farm map has a fixed number of designated cabin spots (7 on the Standard farm), so under `None` the effective maximum number of players is `min(designated spots, MaxPlayers)`. All cabins are placed up front when the game is created (`StartingCabins` is ignored), and the count is frozen — raising `MaxPlayers` later does not add cabins, because placing a cabin on a developed farm would bulldoze whatever is on that spot. For larger or growing rosters, use `CabinStack` or `FarmhouseStack`.

For the same reason, switching an existing stacked save to `None` by editing the settings file is rejected at load and the strategy reverts. Use a fresh game, or the staged `cabins migrate` flow below.

These guarantees are strongest for farms **created** under `None`. A farm imported via `saves import` computes a fresh cap on its first load, and any cabins it is still missing are placed on demand at the designated spots — clearing whatever stands there — so keep the designated spots free on imported farms.
:::

## Switching Strategies

Whether a switch is a plain settings-file edit or needs the staged migration depends on the direction:

| Direction | Path |
|-----------|------|
| anything → `FarmhouseStack` | Settings file + reload (cabins are only hidden — nothing appears on the farm) |
| `None` → `CabinStack` | Settings file + reload (the sweep vacates the shared spot the stack renders at) |
| `FarmhouseStack` → `CabinStack` | `cabins migrate` (the shared stack cabin becomes visible on a spot players may have developed) |
| `CabinStack`/`FarmhouseStack` → `None` | `cabins migrate` (real cabins appear on the farm) |

A rejected settings-file switch reverts only the server's active strategy — the file keeps your edited value, so the rejection warning repeats on every reload until you edit `server-settings.json` back (or complete the switch via `cabins migrate`).

### Staged migration (`cabins migrate`)

Directions that materialize a cabin on a developed farm go through a staged, admin-driven migration. Nothing is ever destroyed: every placement is validated against the live farm (occupied or blocked spots are skipped, never cleared), the current strategy stays fully active while you stage, and the switch happens only at an explicit commit — so aborting is always safe.

Prefer committing while the server is empty: players connected during the migration keep seeing the pre-migration cabin layout until they reconnect.

1. `cabins migrate start <strategy>` (server console) — validates the direction, auto-places what fits on the map's designated spots, and reports how many placements remain.
2. Place the remainder anywhere valid: stand in-game where a cabin should go and run `!migrate place` (admin chat command, places to your right like `!cabin`), or use `cabins migrate place <x> <y>` from the console. `cabins migrate status` shows progress.
3. `cabins migrate commit` — refuses while placements remain; otherwise flips the strategy, updates `server-settings.json`, and re-points cabin doors. `cabins migrate abort` undoes the staging instead.

A restart or reload during staging is harmless: the record persists with the old strategy still active, and settings-file strategy edits are refused until you commit or abort.

::: tip Capacity under None
When migrating to `None`, capacity IS the cabin count — the cap is frozen at commit. Place spare cabins during staging if new players should still be able to join.
:::

## Configuration

In `server-settings.json`:

```json
{
  "Server": {
    "CabinStrategy": "CabinStack"
  }
}
```

## Existing Cabin Behavior

When switching to a stacked strategy on a farm that already has visible cabins:

| Setting | Behavior |
|---------|----------|
| `KeepExisting` | Leave existing cabins where they are. Only new cabins use the stack. |
| `MoveToStack` | Relocate all visible cabins to the hidden stack on startup. |

```json
{
  "Server": {
    "CabinStrategy": "CabinStack",
    "ExistingCabinBehavior": "MoveToStack"
  }
}
```

## Moving Cabins

Players can reposition their cabin using the `!cabin` chat command on the farm. The cabin moves to the player's right side. `!cabin reset` sends it back (under a stacked strategy, into the hidden stack).

This works under every strategy. Under `CabinStack` and `FarmhouseStack`, a moved-out cabin becomes a real, visible building that everyone can see and enter through its own door — under `FarmhouseStack` this is the way to let players meet inside each other's homes while non-movers keep the tidy shared farmhouse entrance.

To forbid cabin moves entirely, set `AllowCabinRelocation` to `false` in `server-settings.json`:

```json
{
  "Server": {
    "AllowCabinRelocation": false
  }
}
```
