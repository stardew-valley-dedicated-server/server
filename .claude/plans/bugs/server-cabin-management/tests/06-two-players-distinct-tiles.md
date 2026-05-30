# Test 06 — CabinStack: two players `/cabin` to different tiles → each sees own cabin at their tile + dummy at `StackLocation`

## Verifies

Two distinct entries in `PlayerCabinPositions` map to two distinct master cabin tiles, both stay through the same session, and `OnLocationIntroductionMessage` (Step 4) feeds each peer their own `targetPos` (their saved tile) plus a dummy at `sharedStackPos`.

This is the **multi-player invariant** for the new code: per-player positions must not stomp each other in either direction (write side: dictionary keyed by UID; read side: `Create(data, peerId)` reads the right key).

## Strategy

CabinStack (default).

## Test class & method

`tests/JunimoServer.Tests/CabinStrategyTests.cs` →
`CabinStack_TwoCabinCommands_BothPositionsRecorded`

`[TestServer(Clients=2)]` on the method to declare 2 clients (config-hash split from the 1-client default — see `test-broker-invariants.md`).

## Preconditions

- Default CabinStack.
- 2 clients available. With `[TestServer(Clients=2)]`, the broker gives this test a server pool sized for it; `StartingCabins` derives from `Clients` so cabin pool has slack.
- Both farmers must reach the Farm location to issue `/cabin` (same warp limitation as Test 02).

## Steps

1. `var clientA = await Farmers.ConnectNewAsync(ct: TestCt);` — primary client.
2. `var leaseB = await LeaseClientAsync(TestCt);` — second client lease.
3. Connect farmer B through `leaseB.Client` using the existing `ConnectionHelper` API for that client (see `LobbyCommandsTestBase.cs` and `PasswordProtectionDisruptiveTests.cs` for two-client patterns). Capture `uidB`.
4. **Warp both onto Farm at distinct tiles**, e.g. A at `(64, 15)`, B at `(72, 15)`. (Same harness extension as Test 02.)
5. Capture pre-tiles per uid via `/cabins`. Both should be `(-20, -20)`.
6. Issue `/cabin` from A, poll until A's master cabin tile != `(-20, -20)`. Capture `aTile`.
7. Issue `/cabin` from B, poll until B's master cabin tile != `(-20, -20)` AND not equal to `aTile`. Capture `bTile`.
8. Read `/cabins` once more.

## Assertions

- `aTile != (-20, -20)` and `aTile == (farmerA.Tile.X + 1, farmerA.Tile.Y)`.
- `bTile != (-20, -20)` and `bTile == (farmerB.Tile.X + 1, farmerB.Tile.Y)`.
- `aTile != bTile` — distinct.
- After both `/cabin`s, `/cabins` shows both cabins at their distinct master tiles, `IsHidden=false`, and at least one OTHER cabin (the pre-warmed `EnsureAtLeastXCabins`-built cabins from boot) remains at `(-20, -20)` — needed to satisfy the "dummy candidate exists" precondition for Step 4's needsDummy branch.

## Harness limits

- **Same warp limitation as Test 02.**
- **Multi-client orchestration cost** (README "Harness limits #7"). No `Clients=N≥2` precedent in the test suite; ~80–120 LOC of test-side helper code per multi-client class.
- **Per-peer dummy is not directly observable** (README "Harness limits #1"). This test verifies that the master state for both A and B holds up — i.e. the dictionary doesn't silently overwrite or read the wrong UID — but cannot directly assert "A's peer rendering shows B's cabin at B's tile and the dummy at sharedStackPos." That assertion would require a client-side `/farm/buildings` GET endpoint.

## Cost/value trade-off (consider deferring)

Test 06's marginal coverage over Tests 02 + 03 + 05 is **only** "concurrent-write to the dict from two distinct UIDs without one stomping the other." But:
- Step 3's dict write is on the game thread (`ChatBox.receiveChatMessage` postfix → game-loop chat-callback). Two `/cabin` invocations from two different players are still serialised by the game loop. The "concurrent" angle is really "two sequential writes from different UIDs."
- The same coverage is reachable in a single-client test by **reconnecting the same farmer with a different UID** between two `/cabin`s — but that's contrived.
- Test 03 (persistence) + Test 05 Variant B (one saved position survives reload) together establish that the dict-key mechanism works for a real UID. The ONE concrete failure mode Test 06 adds is "the dict key is hardcoded to `Game1.player.UniqueMultiplayerID` (host UID) instead of `msg.SourceFarmer`" — a real bug type, but greppable: any test 06 regression would also fail Test 03 (the host doesn't issue `/cabin` in Test 03; the farmhand does, so writing to the host's UID would mean Test 03's lookup at `PlayerCabinPositions[uidA]` fails — sweep wipes A's cabin).

**Recommendation**: defer Test 06 unless the multi-client harness lands cheaply. Tests 03 + 05 cover the real failure modes; Test 06 is "nice to have" for diagnostic clarity. If multi-client lands as part of Test 05's implementation (since Test 05 Variant B also needs 2 farmers), then Test 06 is cheap — author it as a sibling.

## Why this test exists

Catches:
- `PlayerCabinPositions[msg.SourceFarmer]` regression to `[Game1.player.UniqueMultiplayerID]` (host UID) — both writes go to the same key; the second overwrites the first.
- `StackLocation.Create(data, playerId)` reading the WRONG key — A's peer receives B's tile.
- `cabin.Relocate` calling the parameterless overload accidentally (still 0,0 for both).
- The dummy-cabin lookup in Step 4 mutating the wrong farm reference (it operates on `farm` which IS `netRootFarm` here — easy to confuse with `Game1.getFarm()` in a refactor).
