# Test 02 — CabinStack: `/cabin` → own cabin at chosen tile

## Verifies

After a player runs `/cabin`, two observable changes happen in the same session:
1. Master cabin is relocated to the chosen tile (`cabin.Relocate(newPosition.ToPoint())` in Step 3).
2. Master cabin's `IsHidden` flips to `false` (master tile is no longer the hidden stack tile).

The dictionary-write side of Step 3 (`PlayerCabinPositions[uid] = newPosition`) is **not** asserted directly here — it's exercised end-to-end by Test 03 (persistence). Adding it here would require exposing `PlayerCabinPositions` via API, which has no other consumer.

## Strategy

CabinStack (default).

## Test class & method

`tests/JunimoServer.Tests/CabinStrategyTests.cs` →
`CabinStack_CabinCommand_RelocatesMasterCabin`

## Preconditions

- `[TestServer]` defaults: CabinStack, one client.
- The farmer must be on the **Farm** location for `/cabin` to succeed (`CabinCommand.cs:26-29` rejects with "Must be on Farm to move your cabin." otherwise).
- A freshly joined farmhand starts in their **Cabin** interior, NOT on the Farm. The test must drive the farmer onto the Farm before invoking `/cabin`.

## Steps (gated on warp primitive)

1. `var client = await Farmers.ConnectNewAsync(ct: TestCt);`.
2. **Warp the farmer to the Farm at a known tile.** No client-side or server-side `/warp` API exists today (see "Harness limits"). Until a primitive lands, this test cannot run as a happy-path test.
3. Capture pre-state via `/cabins` for our farmer's cabin: `(beforeTileX, beforeTileY)` — expected `(-20, -20)`.
4. Read the farmer's tile from `GameClient.GetState()` and compute `expectedTile = (Tile.X + 1, Tile.Y)`.
5. `await GameClient.SendChat("/cabin");`.
6. Poll `/cabins` until our cabin's `(TileX, TileY) == expectedTile` AND `IsHidden == false`. Timeout via `TestTimings.CabinAssignmentTimeout`; `FailureContext` dump on timeout (matches the existing `WaitForCabinAssignedAsync` pattern in TestBase.cs).
7. Read `/cabins` once more for assertion stability.

## Assertions

- Master cabin tile changed from `(-20, -20)` to `expectedTile`.
- `cabin.IsHidden == false`.
- `cabin.OwnerId == uid` (sanity — relocate must not lose ownership).
- `cabin.Type == "CabinStack"`.
- `Lease.Server.Errors` empty (no `LogLevel.Error` log lines from the `/cabin` path — see `debugging.md`).

## Harness limits

- **No client-side or server-side `/warp` API.** The cleanest cleanup paths:
  - **Add `POST /admin/warp?playerId=X&location=Farm&x=64&y=15` to the server mod's `ApiService`.** Smallest harness change. Use `RunOnGameThreadAsync(() => Game1.warpFarmer(...))`. ~30 LOC + auth gate. Has independent value for any future test that needs to position a farmer.
  - **Add a test-client `/warp` endpoint** that calls `Game1.warpFarmer` from inside the client process. Closer to real-player UX but parallel work and the test-client mod doesn't currently expose a "drive my own farmer" surface.
  - Recommendation: server-side `POST /admin/warp` first. Test 02, Test 06, Test 07, Test 09 (sanity-check), Test 10 all need it.
- **Off-Farm rejection regression NOT covered here.** A regression that breaks `currentLocation.Name != "Farm"` would silently let `/cabin` succeed from anywhere. That's a real but **separate** failure mode; covering it would require asserting "from the Cabin location, `/cabin` is rejected with the on-Farm message" — which is reachable today (no warp needed). If Test 02 is split into "happy path (gated)" and "rejection guard (runnable today)", the rejection guard belongs in either Test 09's class or as a sibling smoke test. Recommend authoring it alongside Test 09 since both tests are about rejection paths.
- **`PlayerCabinPositions` not exposed via API.** Test 03 verifies the dict-write through persistence; here we assert outcome (master moved + chat reply) only.

## Why this test exists

Catches:
- Step 3 omitted (TODO `(a)` left intact, `cabin.Relocate` runs without dictionary write) — Test 03 catches the persistence loss; Test 02 catches the live-tile regression earlier in the development loop.
- `cabin.Relocate` argument-type mismatch (`Vector2.ToPoint()` vs `Point` overloads) silently selecting the wrong overload.
- Step 4's interceptor-side rewrite accidentally mutating master state instead of `netRootFarm` (would surface here as "master cabin moved by the *interceptor* during the join handshake, not by `/cabin`" — failure mode would be a wrong tile or `(0, 0)`).
