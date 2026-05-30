# Test 10 — None: `/cabin` works as before; `PlayerCabinPositions` written but harmlessly unconsumed

## Verifies

Under the `None` (vanilla) strategy:
- `/cabin` is **NOT** rejected (the FarmhouseStack guard doesn't fire, the on-Farm guard fires only if the player is off-Farm).
- The master cabin is relocated by `cabin.Relocate(newPosition.ToPoint())` to the chosen tile — same as pre-fix.
- `PlayerCabinPositions[uid]` IS written (Step 3 has no strategy-aware gate). This is intentionally unconditional: under None, the dictionary entry is written but never *read* (no `OnLocationIntroductionMessage` interception runs under None — see `CabinManagerService.cs:79-94`, the message interceptor is only added when `!options.IsNone`).
- No mod errors are logged.

This is the **None-strategy regression test**. The fix is intended to be a no-op for None operators; this test gates that intent.

## Strategy

None.

## Test class & method

`tests/JunimoServer.Tests/CabinStrategyNoneTests.cs` (existing class) →
`NoneStrategy_CabinCommand_RelocatesMasterCabin`

The existing class already runs with `[TestServer(Exclusive = true)]` and uses `CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", ...)` — drop the new test in alongside the existing two.

## Preconditions

- Server with `CabinStrategy=None`, `startingCabins=2` (so we have >= one cabin to move and the test doesn't race `EnsureAtLeastXCabins`).
- One client.
- Same Farm-warp dependency as Test 02.

## Steps

1. `_needsServerReset = true; await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 2);`.
2. `var client = await Farmers.ConnectNewAsync(ct: TestCt);`.
3. Capture pre-tile from `/cabins` for our farmer's cabin. Under None, this is at a Paths-layer position, NOT `(-20, -20)`.
4. Warp onto Farm at a known, distinct tile (e.g. `(64, 15)`). The pre-tile and target tile must not coincide.
5. `await GameClient.SendChat("/cabin");`.
6. Poll `/cabins` until our cabin's `(TileX, TileY) == (64+1, 15)`.
7. Assert `Lease.Server.Errors` is empty.

## Assertions

- Master cabin tile updated to `(farmer.Tile.X + 1, farmer.Tile.Y)`.
- `IsHidden == false` (was already false under None; unchanged).
- `Type == "Normal"` (None strategy reports "Normal", per `ApiService.cs:1247`).
- `Lease.Server.Errors` empty.
- Chat history does not contain rejection keywords (`"Can't move cabin"`, `"Must be on Farm"`).

## Harness limits

- **Same Farm-warp dependency as Tests 02 / 06 / 09.**
- **Cannot directly assert that `PlayerCabinPositions[uid]` was written** (no exposed endpoint). That observation is reserved for Test 03 (which uses save+reload to verify the write through persistence). For Test 10, we assert the **outcome** (master cabin moved, no errors) rather than the write itself — sufficient because the dictionary write is on the same code path as the relocate (Step 3), so a regression that omits the write would also have to omit the relocate to pass this test.

## Why this test exists

Test 10 covers the **behavioural** regression surface for None operators — what users would notice. It does NOT cover the internal-state regression of "dict write under None silently disabled by a future strategy gate"; that's a different failure mode and not observable from this test.

Catches (behavioural):
- A regression that broke the `cabin.Relocate(newPosition.ToPoint())` call structure for any strategy — the master tile would be wrong or unchanged.
- Mod-side error log emitted from the None path of Step 3 (e.g. logging at `LogLevel.Error` on dictionary write failure) — see `debugging.md`. The empty `Lease.Server.Errors` assertion catches it.
- An accidental strategy-gate that REJECTS `/cabin` under None (e.g. a refactor adding `if (options.IsCabinStack)` around the relocate). The "tile didn't change" assertion fails; the chat reply would also be missing.

Does NOT catch (internal-state):
- A future "harden the fix" refactor that gates the dict write to CabinStack only (`if (options.IsCabinStack) Data.PlayerCabinPositions[...] = ...`). That's invisible from Test 10 because nothing under None reads the dict; the master cabin still moves and the test passes. Such a regression would only surface if the operator later switched to CabinStack — at which point Test 03 / Test 05 would catch it on the next restart, but only for a save where `/cabin` was issued post-switch.

Companion to existing `NoneStrategy_DefaultStartingCabins` / `NoneStrategy_SixStartingCabins` (`CabinStrategyNoneTests.cs:33,53`), which verify cabin **creation** under None; this test verifies cabin **movement** under None.
