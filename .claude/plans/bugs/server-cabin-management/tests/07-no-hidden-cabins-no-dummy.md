# Test 07 — CabinStack: every cabin moved (no hidden cabins remain) → no dummy, no error

## Verifies

When every cabin on the farm has been relocated out of the hidden stack, the `OnLocationIntroductionMessage` Step 4 dummy lookup `farm.buildings.FirstOrDefault(b => b.isCabin && b != playerCabin && b.IsInHiddenStack())` returns `null`, the null-conditional `dummy?.Relocate(sharedStackPos)` is a no-op, and the join handshake succeeds without exceptions.

This is the **boundary-condition guard** for Step 4. The needsDummy=true branch executes, but the lookup returns null. Without this test, an off-by-one in the lookup (e.g. `First` instead of `FirstOrDefault`, or removing the `?.` later in a "simplification") would throw mid-handshake and break the join silently.

## Strategy

CabinStack (default). Requires at least 2 farmers issuing `/cabin` (see "Preconditions").

## Test class & method

`tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs` →
`CabinStack_AllCabinsMoved_LaterJoinSucceedsWithoutDummy`

(Owns its own server because it consumes all available cabins.)

## Preconditions

- Owns its own server via `[TestServer(Exclusive = true)]` + a fresh `CreateNewGameOnServerAsync` to start with a known cabin pool.
- `StartingCabins=2` so the test only has to move 2 cabins to drain the hidden stack — keeps wall-clock manageable.
- Same warp+`/cabin` harness extension as Test 02.

## Steps (single-client; reconnect exercises the no-dummy branch)

1. `await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack", startingCabins: 1);` — fresh state with exactly 1 hidden cabin (this farmer's cabin).
2. Connect A. `OnServerJoined` fires → `EnsureAtLeastXCabins` runs but the pool is already at the minimum, so no new cabin is built.
3. Warp onto Farm at a known tile.
4. `await GameClient.SendChat("/cabin");` — A's cabin moves to chosen tile. Pool: 0 hidden cabins. Verify via `/cabins`: `Cabins.Count(c => c.IsHidden && c.Type == "CabinStack") == 0`.
5. **Disconnect A** and wait for slot release. `OnServerJoined` does NOT fire on disconnect, so `EnsureAtLeastXCabins` does not replenish.
6. **Reconnect A.** Stardew sends a fresh `locationIntroduction` to A. The mod's interceptor runs Step 4's logic:
   - `playerCabin = A's cabin` (at chosen tile, not hidden).
   - `playerCabin.IsInHiddenStack()` is `false` → else-branch.
   - `needsDummy = (playerCabin != null) = true`.
   - Dummy lookup: `farm.buildings.FirstOrDefault(b => b.isCabin && b != playerCabin && b.IsInHiddenStack())` — returns `null` because no other hidden cabin exists.
   - `dummy?.Relocate(sharedStackPos)` — no-op (the null-conditional). **This is the path under test.**
   - The reconnect's `OnServerJoined` then fires `EnsureAtLeastXCabins` which rebuilds a hidden cabin, but that's after the message went out.

## Assertions

- Reconnect handshake completes without `Lease.Server.Errors` accumulating any new entries.
- A's master cabin tile is still at the `/cabin`-chosen tile after reconnect (the no-dummy path didn't mutate it).
- `Lease.Server.Errors` is empty (mod did not log at `LogLevel.Error` during the handshake — see `debugging.md`).
- (Optional) `/cabins` after reconnect shows ≥ 1 hidden cabin (the post-reconnect `EnsureAtLeastXCabins` replenished). This is incidental, not the test's core invariant.

## Harness limits

- **Same warp limitation as Tests 02, 06.**
- **Multi-client orchestration** (README "Harness limits #7"). Same blocker as Tests 05, 06.
- **`EnsureAtLeastXCabins` keeps replenishing hidden cabins.** It runs on `OnServerJoined` (Harmony postfix on `GameServer.sendServerIntroduction` — `CabinManagerService.cs:480`) and auto-creates a hidden cabin whenever the available count drops below `minEmptyCabins` (1). The test cannot prevent this without patching the service. The "no hidden cabins remain" precondition is therefore **transient** — between `/cabin` invocations and the next `OnServerJoined`. Concretely:
  - Connect A, warp, `/cabin`. Pool: 0 hidden cabins (A's cabin moved out).
  - Connect B → triggers `OnServerJoined` for B → `EnsureAtLeastXCabins` rebuilds a hidden cabin → pool: 1 hidden cabin. **The no-dummy precondition is broken before B's `OnLocationIntroductionMessage` runs.**
  - This makes the test as written **untestable** in the obvious way.
  - **Workaround**: connect A and B FIRST (let replenishment fire for both joins), THEN run `/cabin` from A AND from B back-to-back. The "no hidden cabins remain" window opens AFTER both `/cabin` calls land and BEFORE the next reconnect triggers replenishment. The test must observe Step 4's no-dummy-found path **on a reconnect of A or B during this window**:
    1. Connect A, warp, `/cabin` (pool drops to 0, but no `OnServerJoined` fires after, so it stays at 0 from this point until the next join).
    2. (Skip B-connect, since that would replenish.) Disconnect A.
    3. Wait for slot release.
    4. Reconnect A. `OnLocationIntroductionMessage` fires for A's reconnect; A's cabin is at the chosen tile (not hidden), so `playerCabin.IsInHiddenStack()` is FALSE → `needsDummy = true` → dummy lookup returns null (no hidden cabins) → `dummy?.Relocate` is no-op. **Then** `EnsureAtLeastXCabins` fires from `OnServerJoined`, but it's too late to invalidate the in-flight message.
  - **Single-client coverage IS sufficient** for this test's invariant; the original two-farmer plan was over-specified. Recommend rewriting Step 2 onward with a single farmer.
- **No way to assert "the dummy cabin field of the rewritten message is null"** — see README #1. This test asserts the proxy: "join completes, no errors, master state is intact."

## Why this test exists

Catches:
- A future "simplification" of `dummy?.Relocate(sharedStackPos)` to `dummy.Relocate(sharedStackPos)` — would throw NRE for every join after all cabins are moved, silently breaking Step 4 for late-joining players.
- Replacing `FirstOrDefault` with `First` in the dummy lookup.
- A regression that throws inside the LINQ predicate (e.g. `b.GetIndoors<Cabin>()?.owner` chain re-introduced) when buildings list contains a non-Cabin in some edge state.
- `LogLevel.Error` introduced into the no-dummy diagnostic path — would cancel the test via the `\b(ERROR|FATAL)\b` regex (`debugging.md`).
