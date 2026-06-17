# 04 — `!cabin reset` command

Let a player undo their `!cabin` placement: clear the saved-position intent and return the cabin to the hidden stack. Today the only thing that clears an intent entry is farmhand deletion (`ApiService.ExecuteFarmhandDeletion`, `mod/JunimoServer/Services/Api/ApiService.cs:4376`); a player who regrets a placement is stuck with it, and an operator's only recourse is editing save mod-data by hand.

## Design

Extend the existing `cabin` chat command (`mod/JunimoServer/Services/Commands/CabinCommand.cs`) with a `reset` subcommand rather than adding an HTTP endpoint — the affected user is the player, the command surface already exists, and no new API/OpenAPI/test-client plumbing is needed (`simplest-solution.md`).

Behavior of `!cabin reset` (by strategy). **Key the action on the cabin's visibility, not on whether an intent entry exists** — that way a half-applied reset (intent cleared, cabin still visible) is recoverable by re-running the command, with no dependence on a later sweep (which only runs under `ExistingCabinBehavior=MoveToStack`, not the default):

- **CabinStack**: if the player's cabin is not in the hidden stack — clear `PlayerCabinPositions[msg.SourceFarmer]` best-effort (`TryRemove`, ignore a miss; the dict is a `ConcurrentDictionary`), `Data.Write()`, then `cabin.SetPosition(HiddenCabinLocation)` — the same call the bulk movers use, so warps/door behavior follow the established path (`OnLocationDeltaMessage` re-points cabin doors). The player sees their cabin back at the shared `StackLocation` on the next location introduction. Reply confirming the reset. If the cabin is already hidden: friendly "nothing to reset" reply (still `TryRemove` a stale intent entry if present).
- **FarmhouseStack**: `!cabin` is already rejected before any subcommand parsing matters today; keep `reset` behind the same gate (nothing to reset — placements can't exist under this strategy).
- **None**: intent entries can exist (the write at `CabinCommand.cs:84` is unconditional) but nothing reads them under None. Clear the entry, do **not** move the cabin (there is no hidden stack under None), reply accordingly.
- **No cabin**: friendly reply, no state change.

Clear the dict entry *before* `SetPosition`: a throw mid-way then leaves "no intent + still visible", which a re-run of `reset` fixes (visibility-keyed); the reverse order could leave "intent + hidden", pinning a hidden cabin out of every future sweep with no user-visible way to clear it.

Update the command's help text registered in `RegisterCommand` to mention the subcommand.

## Tests

Add to `CabinPositionPersistenceTests`:

1. Place via `MoveCabinViaCommandAsync`, assert `SavedPositionPlayerIds` contains the id (existing pattern from `Deletion_ClearsSavedPositionIntent`).
2. Send `!cabin reset`; poll `/cabins` until our cabin `IsHidden == true` and `SavedPositionPlayerIds` no longer contains the id.
3. `SleepToSaveAsync`, `Farmers.DisconnectAndWaitForPersistenceAsync` (`/reload` 409s while connected), `ReloadServerAsync`; assert the cabin is still hidden after the reload (the reset survives the sweep instead of resurrecting).

## Notes

- Keep all logging at Warn/Trace or below (`debugging.md` — Error-level lines cancel tests).
- The dummy-cabin cosmetic (plan 05) interacts with this: after a reset the player's cabin is hidden again, so the dummy branch must not double-place. If 05 lands first, re-check its branch condition here.
