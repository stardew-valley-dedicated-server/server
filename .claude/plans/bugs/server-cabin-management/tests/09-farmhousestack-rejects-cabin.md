# Test 09 — FarmhouseStack: `/cabin` rejected with existing message — no regression

## Verifies

The early-return in `CabinCommand.cs:18-22` (`options.IsFarmHouseStack` rejection) still fires after the rewrite. No `PlayerCabinPositions` write happens. No `cabin.Relocate` happens. The user gets the rejection chat reply.

This is a **regression-only** test. It does not exercise any new behaviour from this fix; it guards against the rewrite of Step 3 accidentally moving the early-return below the dictionary-write or below the `Relocate` call.

## Strategy

CabinStack is the default. To run with FarmhouseStack, the test owns its own server and configures explicitly.

## Test class & method

`tests/JunimoServer.Tests/CabinStrategyFarmhouseStackTests.cs` (new) →
`FarmhouseStack_CabinCommand_Rejected`

`[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true)]` — same shape as `CabinStrategyNoneTests`. The class can host other FarmhouseStack-specific tests later.

## Preconditions

- Server with `CabinStrategy=FarmhouseStack`. Created via `CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack")` in the test body.
- One client.
- The farmer must be **on the Farm** for the test to be meaningful — without that, the test could be trivially passing because of the off-Farm rejection (which fires *after* the FarmhouseStack rejection in the code, but a future re-order could swap them and we'd accept the wrong rejection).

  Wait: re-reading `CabinCommand.cs:18-30`, the FarmhouseStack check IS first. So even without warping, the FarmhouseStack reply fires. But to *prove* the FarmhouseStack rejection is what fires, we need to be on the Farm — otherwise the test passes for the wrong reason. Same warp dependency as Test 02.

## Steps

1. `await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");`.
2. `var client = await Farmers.ConnectNewAsync(ct: TestCt);`.
3. Warp onto Farm (harness extension). If unavailable, run the test anyway and accept the weaker invariant ("either rejection fires") — the `await Chat.AssertResponseAsync("/cabin", "...")` keyword can be the FarmhouseStack message, and a regression that only the off-Farm rejection fires would still fail because it expects different chat keywords.
4. `await Chat.AssertResponseAsync("/cabin", "Can't move cabin", "host has chosen to keep all cabins in the farmhouse");` — the AssertResponseAsync helper polls until both keyword fragments appear in chat history.
5. After the rejection, read `/cabins` and verify NO cabin has been mutated:
   - `var after = await ServerApi.GetCabins(TestCt);`
   - For our farmer's cabin: `(TileX, TileY) == (-20, -20)` and `IsHidden == true`.

## Assertions

- Chat reply contains the FarmhouseStack rejection text.
- Master cabin tile for our UID is unchanged at `(-20, -20)`.
- Master cabin `IsHidden == true`.

## Harness limits

- **Warp limitation.** Without it, the test still fires the FarmhouseStack rejection (because that check is first), but a future re-order would silently produce a different rejection and this test would not catch it. Mitigation: assert the *exact* keyword phrase (`"farmhouse"`) so the wrong rejection's reply ("Must be on Farm") fails the assertion.
- **`PlayerCabinPositions` not exposed.** We assert the *absence* of a write indirectly via "master cabin tile unchanged." That's the strongest currently-observable proxy.

## Sibling test (consider co-locating)

The off-Farm rejection regression guard from Test 02's "Harness limits" naturally lives next to this test, since both are about `CabinCommand`'s early-return paths and neither needs the warp primitive. Author them together:

```
[Fact] FarmhouseStack_CabinCommand_Rejected            ← Test 09 proper
[Fact] CabinStack_CabinCommand_OffFarm_Rejected        ← off-Farm rejection guard
```

The off-Farm test runs the default CabinStack server, joins a fresh farmer (lands in Cabin interior, NOT on Farm), sends `/cabin`, asserts chat reply contains `"Must be on Farm"`, and asserts master cabin tile is unchanged at `(-20, -20)`. ~20 LOC.

## Why this test exists

Catches:
- Step 3 rewrite moves the dictionary write or `cabin.Relocate` ABOVE the FarmhouseStack early-return — both side effects run before the rejection, leaving stale state.
- Step 3 rewrite changes the rejection branch's chat message (the existing message text is referenced in the issue, the GitHub forum, and FAQ; changing it without coordination breaks operator expectations).
- A future "consolidate strategies" refactor that removes the FarmhouseStack rejection accidentally — `/cabin` would silently succeed for a strategy where it shouldn't.
