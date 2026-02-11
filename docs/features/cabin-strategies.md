# Cabin Strategies

Control how player cabins are placed and managed on your farm.

## Strategies

| Strategy | Description |
|----------|-------------|
| `CabinStack` | Cabins hidden off-map. Each player sees only their own cabin at a shared position. |
| `FarmhouseStack` | Cabins hidden off-map. All players warp to a shared farmhouse interior. |
| `None` | Vanilla behavior. Cabins placed at real farm positions. |

## CabinStack (Default)

Cabins exist but are moved off the visible map. When a player warps to "their cabin," they go to the hidden location. From the player's perspective, they have a cabin — it's just not cluttering the farm.

Benefits:
- Clean farm layout regardless of player count
- No cabin placement conflicts
- Each player still has their own private space

## FarmhouseStack

Similar to CabinStack, but all players share the farmhouse interior instead of having separate cabins. Players still have individual inventories and beds.

Use this for a more communal feel.

## None (Vanilla)

Standard Stardew Valley behavior. Cabins are placed on the farm at specific locations. Use this if you want the traditional multiplayer experience or need cabins at specific positions.

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

Players can reposition their cabin using the `!cabin` chat command on the farm. The cabin moves to the player's right side.
