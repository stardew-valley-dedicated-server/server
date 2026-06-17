# 05 — Dummy cabin at the shared stack location

Cosmetic gap deferred out of issue #64's fix: under CabinStack, a player who moved their cabin via `!cabin` sees an **empty spot** at the shared `StackLocation`, while every other player sees their own cabin there. Maintainer-confirmed design: for such a player, render one hidden-stack cabin at the shared spot as a dummy; if no hidden-stack cabin remains, render nothing.

## Context

`CabinManagerService.OnLocationIntroductionMessage` (`mod/JunimoServer/Services/CabinManager/CabinManagerService.cs:504`) deserializes a fresh farm copy from the outgoing `locationIntroduction` message, mutates **that copy**, and re-serializes — master state is never touched. The CabinStack branch (lines ~538–550) currently does one thing: if the peer's own cabin is in the hidden stack (tile −20,−20), relocate it to `StackLocation.Create(_cabinManagerData)`. A peer whose cabin is *not* hidden (placed via `!cabin`, or imported visible under KeepExisting) gets no mutation — hence the empty shared spot.

## Open design questions — resolve before coding

1. **The dummy is another player's real cabin.** Its door warps into that player's cabin interior, and its mailbox/interactions are live. Options: accept the quirk (the pre-#64 status quo put *some* cabin there for everyone anyway); or strip/redirect the dummy's warps in the message copy (e.g. `SetWarpsToFarmCabinDoor` equivalent pointing nowhere sensible — needs investigation). Decide with the maintainer; this is the reason the feature was split out, so don't hand-wave it.
2. **Does the KeepExisting-imported-visible case get a dummy too?** The design says "whose own cabin has been moved" — an imported visible cabin is morally the same (not at the stack). Recommend: same treatment, one branch condition (`cabin != null && !cabin.IsInHiddenStack()`).

## Implementation sketch

In the CabinStack else-branch, after the existing hidden-cabin relocate:

```csharp
else if (cabin != null && !cabin.IsInHiddenStack())
{
    var dummy = farm.buildings.FirstOrDefault(b =>
        b.isCabin && b != cabin && b.IsInHiddenStack());
    dummy?.Relocate(StackLocation.Create(_cabinManagerData).ToPoint());
}
```

- `IsInHiddenStack()` checks (−20,−20) only, so lobby cabins at (−21,−21) are excluded automatically.
- `FirstOrDefault` + `?.` are load-bearing as defense: the zero-hidden-cabin case appears unreachable via real joins (see below), but a `First`/`.` "simplification" would turn any future reachability into a broken join handshake.
- Interacts with plan 04 (`!cabin reset`): a reset player's cabin is hidden again and takes the existing branch; no double-place, but re-verify after both land.

## Verification

The mutation exists only in the per-peer message copy — `/cabins` reads master state and **cannot see it**. The test-client mod has no endpoint reading `Game1.getFarm().buildings` from the client's view.

**The no-dummy branch cannot be reached by draining the pool and reconnecting.** `FarmhandSenderService` calls `EnsureAtLeastXCabins(reservedIds.Count + 1)` while sending the available-farmhands list (`FarmhandSenderService.cs:259`) — i.e. *before* the player picks a slot, therefore before `locationIntroduction` is sent. A reconnecting client replenishes the hidden pool as a side effect of its own handshake, so the interceptor always finds a dummy candidate on a real join. (`OnServerJoined` ensures again after the intro.) Treat the `?.` as defense, not as a path needing E2E proof.

1. **Guard test (proxy, automatable today)**: fresh game, connect, `!cabin`, disconnect, reconnect. The reconnect's location introduction runs the new dummy-found branch; assert the join completes, the master tile is unchanged (the dummy relocate must mutate only the message copy), and no exceptions/errors. Lives in `CabinPositionPersistenceTests`.
2. **Positive observation**: either (a) add a small test-client endpoint listing client-side farm buildings (name/tile) — the honest, reusable gate, also unlocks asserting the existing per-peer relocate; or (b) a documented manual sign-off with a real client in the PR. Pick (a) if the feature ships at all; per `runtime-post-conditions-are-gates.md` don't merge on the proxy test alone when the entire feature is the unobservable part.
