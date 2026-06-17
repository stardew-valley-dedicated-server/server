# 02 — Second-farmer test helper

A reusable helper for connecting a second concurrent farmer over its own client lease. This is the one missing primitive for multi-player E2E tests; today no test in the suite runs two farmers at once.

## Context

The mechanism is already proven in the body of the skipped test `CabinPlacementValidationTests.AnotherPlayerInFootprint_RejectsAndDoesNotMove` (`tests/JunimoServer.Tests/CabinPlacementValidationTests.cs:142`):

```csharp
await using var leaseB = await LeaseClientAsync(ct);
var connB = new ConnectionHelper(leaseB.Client, serverApi: ServerApi);
var joinB = await connB.JoinWorldViaLanAsync(
    Lease!.ServerLanAddress, Lease.ServerLanPort, nameB, cancellationToken: ct);
Farmers.TrackFarmer(nameB, joinB.UniqueMultiplayerId);
```

The test is `[Skip]`ped only because the shared connection helpers bind to the single primary client — the inline mechanism works but deserves a home before being copied.

## Design

Add `tests/JunimoServer.Tests/Helpers/SecondFarmerHelper.cs` (or a method on `FarmerTestHelper` if it fits the fixture's lifecycle — decide at implementation) exposing roughly:

```csharp
await using var second = await SecondFarmer.ConnectAsync(testBase, name: "FarmerB", ct);
// second.Client (GameTestClient), second.Uid, second.FarmerName, second.Lease
```

Requirements:

- **Disposal order.** The wrapper's `DisposeAsync` must disconnect the farmer and dispose the lease *before* the test class's `DisposeAsync` runs its `CreateNewGameOnServerAsync` reset — `/newgame` and `/reload` return 409 while any client is connected. `await using` in the test body gives this ordering for free; document it on the type.
- **Idempotent disposal.** Tests need to disconnect the second farmer mid-test (e.g. before `SleepToSaveAsync` — an idle connected farmer blocks the day-end ready check), then `await using` disposes again at scope exit. `DisposeAsync` must be safe to call twice.
- **LAN-only is fine.** `JoinWorldViaLanAsync` suffices; Steam transport is only needed for tests requiring a client-stamped `userID` (`.claude/rules/abandoned-claim-is-steam-only.md`), which none of the planned consumers do.
- **No `[TestServer(Clients = 2)]` needed.** Declaring 2 clients splits the server config-hash/pool (`.claude/rules/test-broker-invariants.md`). A runtime `LeaseClientAsync` avoids the split, and the cabin pool replenishes on join (`EnsureAtLeastXCabins` runs in the patched `sendAvailableFarmhands` path), so the second farmer always finds a slot.
- **Track the farmer** via `Farmers.TrackFarmer(...)` so existing cleanup sweeps it.

## Steps

1. Extract the skipped test's inline mechanism into the helper.
2. Un-skip `AnotherPlayerInFootprint_RejectsAndDoesNotMove`, port it to the helper, and get it green — it is the helper's first consumer and its validation.
3. Keep the helper minimal: connect, expose handles, dispose cleanly. Per-test orchestration (warps, chat) stays in tests.

## Verification

Un-skipped test green on real infrastructure, plus one full run of `CabinPlacementValidationTests` to confirm the disposal ordering doesn't break the class's `/newgame` reset.
