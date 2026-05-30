# Test 01 — CabinStack: new player joins → own cabin at shared `StackLocation`, no dummy

## Verifies

A freshly-joined player whose UID has no entry in `PlayerCabinPositions` gets their cabin rendered at the shared `StackLocation` (`StackLocation.Create(_cabinManagerData)` — Step 4 else-branch falls through to no-relocate-needed when the player has no saved position; the existing pre-fix behaviour remains intact).

## Strategy

CabinStack (default). No `/cabin` invocation in this test.

## Test class & method

`tests/JunimoServer.Tests/CabinStrategyTests.cs` →
`CabinStack_NewPlayer_OwnCabinAtSharedStackLocation`

## Preconditions

- `[TestServer]` defaults: CabinStack strategy, `ExistingCabinBehavior=KeepExisting` (default), one client.
- The shared `CabinStrategyTests` server already has hidden cabins from `EnsureAtLeastXCabins` boot.
- No prior `/cabin` invocation has touched this farmer's UID. (Class is `IsolationMode.SharedAssembly`; test must not assume other tests didn't run, but it owns its own newly-generated farmer name and UID.)

## Steps

1. `var client = await Farmers.ConnectNewAsync(ct: TestCt);` — joins a new farmer; the harness already gates on slot+world-ready.
2. `var ourCabin = await WaitForCabinAssignedAsync(client.JoinResult.UniqueMultiplayerId, TestCt);` — polls `/cabins` until the master snapshot reports a cabin assigned to this UID (helper already exists in `TestBase.cs:610`).
3. Read the **shared stack expectation** server-side. `StackLocation.Create(data)` resolution order is `data.DefaultCabinLocation` → `FarmCabinPositions.GetDefaultStackPosition(...)` → `(50, 14)`. The default-farm map's first Paths-layer cabin position is the operative value; the test does not hard-code coordinates and instead asserts the indirect property:
   - `ourCabin.IsAssigned == true`
   - `ourCabin.OwnerId == client.JoinResult.UniqueMultiplayerId`
   - `ourCabin.IsHidden == true` — under the existing snapshot logic (`ApiService.cs:1248`), `IsHidden` is `true` when `building.IsInHiddenStack()`, i.e. tile `(-20, -20)`.

   The MASTER cabin tile remains `(-20, -20)` because Step 4 only mutates an outgoing per-peer message; master state is untouched. So `/cabins` reports `IsHidden=true` even though the *peer-rendered* cabin is at `StackLocation`.

## Assertions

- `ourCabin != null` — cabin assigned to our UID is present.
- `ourCabin.IsAssigned == true`.
- `ourCabin.IsHidden == true` — master tile is the hidden stack.
- `ourCabin.Type == "CabinStack"`.

## Harness limits

The peer-rendered position is **not directly observable** (see README "Harness limits #1"). This test therefore asserts the master invariant ("cabin stays in the hidden stack at master level when no `/cabin` has run") rather than the peer-rendered invariant ("client sees their cabin at `StackLocation`"). The peer-rendered invariant is exercised but only verified indirectly:
- If Step 4 throws on this code path, the join would fail (the interceptor would fault while building the rewritten message), and the harness's `ConnectNewAsync` already gates on a successful world-ready handshake.
- A regression that placed master state at `(-20, -20)` BUT made `/cabins` report a different tile would still pass — that failure mode is judged unlikely because `SnapshotCabins` reads `building.tileX/tileY` directly.

## Why this test exists

Catches:
- Pre-fix regression: `OnLocationIntroductionMessage` accidentally relocating the *master* cabin (the existing code does `cabin.Relocate` on `netRootFarm`'s deserialised copy, not the master farm — easy to swap by mistake during the Step 4 rewrite).
- Step 4 rewrite throwing on the no-saved-position path. The else-branch sets `needsDummy = playerCabin != null`, then runs the dummy lookup; an exception there would break the join handshake.
- Step 1 dictionary-init regression (`PlayerCabinPositions = null` after `Read()`) producing a NullReferenceException inside `StackLocation.Create(data, peerId)`.

## Coverage overlap (consider deleting)

The existing `CabinStrategyTests.CabinStack_AllCabinsAreHidden` (line 78 of the existing file) already asserts `IsHidden == true` for all CabinStack cabins on a fresh server. The marginal coverage Test 01 adds is "for our newly-joined farmer's specific cabin." That's a UID-scoped check the existing test doesn't do. But the Step 4 rewrite throwing on the no-saved-position path would also fail every other test in `CabinStrategyTests` by breaking the join handshake — so the diagnostic value of Test 01 over the existing tests is small.

**Recommendation**: keep Test 01 only if it costs less than 30 lines of test code (it should — most plumbing is already in the existing class). Otherwise, fold its assertion into the existing `CabinStack_PlayerJoin_CabinAssignedToPlayer` test (`CabinStrategyTests.cs:176`) by adding `Assert.True(ourCabin.IsHidden)` and `Assert.Equal((-20, -20), (ourCabin.TileX, ourCabin.TileY))` to its existing assertion block.
