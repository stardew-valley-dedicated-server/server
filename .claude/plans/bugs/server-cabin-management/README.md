# Fix: Cabin position resets to default on restart

> **GitHub Issue:** [#64 - After moving the co-op cabin, it still reverts to the default location](https://github.com/stardew-valley-dedicated-server/server/issues/64)

## Bug

After a player moves their cabin via `/cabin`, the position is lost on the next save-load if `ExistingCabinBehavior=MoveToStack` (or on a strategy switch from None → CabinStack/FarmhouseStack). There is also no visible cabin at the shared `StackLocation` for players who moved their own cabin away — the spot looks empty.

Scope: CabinStack strategy. FarmhouseStack and None are unaffected.

## Architecture

- `CabinPositions.PlayerStack = (-20, -20)` is the hidden stack. Lobby cabins live at `(-21, -21)`.
- `OnLocationIntroductionMessage` (server-side) deserialises a fresh farm copy from the outgoing `locationIntroduction` message via `NetRoot<GameLocation>.Connect(context.Reader)`, mutates that copy, and re-serialises via `NetworkHelper.CreateMessageLocationIntroduction(...)`. Real server state is not touched.
- `StackLocation.Create(data)` returns one shared position from `data.DefaultCabinLocation` → map Paths layer → fallback `(50, 14)`.

## Root cause

The player's chosen position has no mod-side representation. The save file is the only record, and any code that bulk-moves cabins to the hidden stack has no way to distinguish "intentional position" from "incidental position." Two such bulk movers exist today (`SyncExistingCabins` MoveToStack branch, `MigrateCabins` None→Stacked branch); both wipe `/cabin`-placed positions.

## Design (confirmed with maintainer)

- A moved cabin is visible to **everyone** at its new position.
- For a player whose own cabin has been moved, render one hidden-stack cabin at the shared `StackLocation` as a dummy. If no hidden-stack cabin remains, render nothing.

---

## Step 1 — Persist per-player positions

**File**: `mod/JunimoServer/Services/CabinManager/CabinManagerData.cs`

Add field. **Use `ConcurrentDictionary`**, not `Dictionary` — the chat command writes from the game thread, the location-introduction interceptor reads from the network thread (see Compatibility § "Concurrency").

```csharp
public ConcurrentDictionary<long, Vector2> PlayerCabinPositions = new ConcurrentDictionary<long, Vector2>();
```

In `Read()`, after the existing copy lines:

```csharp
PlayerCabinPositions = Data.PlayerCabinPositions ?? new ConcurrentDictionary<long, Vector2>();
```

`Write()` is unchanged.

**Save-format note.** SMAPI's serialiser uses Json.NET; `ConcurrentDictionary<TKey,TValue>` round-trips through JSON identically to `Dictionary<TKey,TValue>` (it's just a key-value map at the wire level), so this is still backwards-compatible with older saves.

## Step 2 — Per-player `StackLocation` overload

**File**: `mod/JunimoServer/Services/CabinManager/StackLocation.cs`

Add:

```csharp
public static StackLocation Create(CabinManagerData cabinManagerData, long playerId)
{
    if (cabinManagerData.PlayerCabinPositions.TryGetValue(playerId, out var position))
    {
        return new StackLocation(position, null);
    }
    return Create(cabinManagerData);
}
```

The parameterless-player `Create(CabinManagerData)` is unchanged and remains the source for the dummy cabin's position.

## Step 3 — Persist position in `/cabin`

**File**: `mod/JunimoServer/Services/Commands/CabinCommand.cs`

`Register()` already receives `CabinManagerService cabinService`. Replace `cabin.Relocate(farmer.Tile.X + 1, farmer.Tile.Y);` with:

```csharp
var newPosition = new Vector2(farmer.Tile.X + 1, farmer.Tile.Y);

// Order: dictionary update first, then relocate. If Relocate throws, the
// dict update sticks; the next OnLocationIntroductionMessage will use the
// saved tile to reposition the cabin in the rewritten message. Note that
// neither side persists to disk here — Helper.Data.WriteSaveData is
// in-memory until the next game save event.
cabinService.Data.PlayerCabinPositions[msg.SourceFarmer] = newPosition;
cabinService.Data.Write();

cabin.Relocate(newPosition.ToPoint());
```

Delete the stale TODO `(a)` at lines 41-42 (this step makes it obsolete). Items `(b)` and `(c)` stay.

Also use `ConcurrentDictionary<long, Vector2>` for `PlayerCabinPositions` (see Compatibility § "Concurrency"); the field declaration and `Read()` initialiser change accordingly.

## Step 4 — `OnLocationIntroductionMessage`: restore + dummy

**File**: `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs`
**Method**: `OnLocationIntroductionMessage`, CabinStack else-branch (lines ~384-396)

Replace with:

```csharp
farm = netRootFarm;
var sharedStackPos = StackLocation.Create(_cabinManagerData).ToPoint();
var playerCabin = farm.GetCabin(context.PeerId);

bool needsDummy;
if (playerCabin != null && playerCabin.IsInHiddenStack())
{
    var targetPos = StackLocation.Create(_cabinManagerData, context.PeerId).ToPoint();
    playerCabin.Relocate(targetPos);
    needsDummy = targetPos != sharedStackPos;
}
else
{
    needsDummy = playerCabin != null;
}

if (needsDummy)
{
    var dummy = farm.buildings.FirstOrDefault(b =>
        b.isCabin && b != playerCabin && b.IsInHiddenStack());
    dummy?.Relocate(sharedStackPos);
}
```

`IsInHiddenStack()` checks tile `(-20, -20)`; lobby cabins at `(-21, -21)` are excluded automatically.

## Step 5 — Bulk movers respect saved positions

**File**: `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs`

Add helper to the class:

```csharp
// A cabin that the player has explicitly placed via /cabin must not be
// pulled back into the hidden stack by bulk migrations. Reads
// PlayerCabinPositions via the public ContainsKey API (same shape on
// ConcurrentDictionary as Dictionary; the read is an O(1) lookup, no lock
// required against concurrent writes from the chat thread).
private bool HasSavedPosition(Building cabin)
{
    var ownerId = cabin.GetIndoors<Cabin>()?.owner?.UniqueMultiplayerID ?? 0;
    return ownerId != 0 && Data.PlayerCabinPositions.ContainsKey(ownerId);
}
```

In `SyncExistingCabins()` `MoveToStack` branch (lines ~335-346), add `&& !HasSavedPosition(b)` to the `visibleCabins` filter:

```csharp
var visibleCabins = allCabins
    .Where(b => !b.IsInHiddenStack() && !b.IsLobbyOrEditing() && !HasSavedPosition(b))
    .ToList();
```

In `MigrateCabins()` None→Stacked branch (lines ~268-281), same filter addition:

```csharp
var visibleCabins = farm.buildings
    .Where(b => b.isCabin && !b.IsInHiddenStack() && !b.IsLobbyOrEditing() && !HasSavedPosition(b))
    .ToList();
```

---

## Files changed

| File | Change |
|------|--------|
| `mod/JunimoServer/Services/CabinManager/CabinManagerData.cs` | Add `PlayerCabinPositions`; copy in `Read()`. |
| `mod/JunimoServer/Services/CabinManager/StackLocation.cs` | Add `Create(data, playerId)` overload. |
| `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs` | Add `HasSavedPosition`. Rewrite CabinStack branch of `OnLocationIntroductionMessage`. Filter `SyncExistingCabins` MoveToStack branch and `MigrateCabins` None→Stacked branch. |
| `mod/JunimoServer/Services/Commands/CabinCommand.cs` | Persist position before relocate; remove stale TODO `(a)`. |

## Compatibility

- **Save format**: new dict field is backwards-compatible (older saves deserialise as `null`, `Read()` initialises empty).
- **Concurrency**: `Data.Write()` from `/cabin` runs on the game thread (chat callback fires from a `ChatBox.receiveChatMessage` postfix). `Data.Read()` runs on `OnSaveLoaded`, also game thread. The interceptor reads the dict from the **network thread**; `Dictionary<TKey, TValue>` is *not* thread-safe for concurrent read+write. Even though the read is `TryGetValue` (logically read-only), a concurrent `Add` from a `/cabin` invocation can mid-resize the bucket array and produce NRE / wrong-value reads. **Use `ConcurrentDictionary<long, Vector2>` from the start of this fix.** Adopting it later means tracking down a heisenbug that only manifests when `/cabin` lands during a peer's location intro — vanishingly rare in dev, common in busy public servers.
- **Persistence semantics**: `IModHelper.Data.WriteSaveData(...)` writes to in-memory SMAPI mod data; the actual disk write happens during the next **game save event** (sleep, day-end, `/save` command). Calling `Data.Write()` from `/cabin` only updates the in-memory copy — if the server crashes / restarts before the next save, both the dict entry AND the master cabin position are lost together. The "intent before move" ordering still has a narrow win (recovers if the relocate itself throws while the dict update succeeded), but it does NOT recover from a process crash before save. Tests that exercise the cross-restart path (Tests 03, 04, 05, 08) MUST trigger a save in between (e.g. sleep through a day, run a force-save command) — otherwise they pass for the wrong reason: nothing was on disk to be wiped or preserved.
- **Failure ordering**: persisting before relocating means a relocate-throw leaves intent-but-no-move (next intro restores it). Reverse order would leave move-but-no-intent (next `MoveToStack` wipes it).

---

## Tests

Each test below has a dedicated plan file. Plans cover what is observable through the existing test harness; tests requiring harness extensions (server restart while preserving the save, multi-peer packet inspection) are flagged explicitly in their plan files rather than silently dropped.

All tests live in `tests/JunimoServer.Tests/` and run against real Docker server containers. Server master state is observed via `GET /cabins`; chat responses are observed via `GameClient.GetChatHistory(...)`. There is **no** client-side API to inspect what each peer's rewritten `locationIntroduction` packet contained — every plan calls out the consequences of that limit.

| # | Test | Plan | Status |
|---|------|------|--------|
| 1 | CabinStack: new player joins → cabin at hidden stack, no dummy needed. | [tests/01-new-player-no-dummy.md](tests/01-new-player-no-dummy.md) | Optional — overlaps existing class smoke tests; consider folding into existing test. |
| 2 | CabinStack: `/cabin` → master cabin at chosen tile, IsHidden=false. | [tests/02-cabin-command-moves-and-records.md](tests/02-cabin-command-moves-and-records.md) | Gated on warp primitive. |
| 3 | CabinStack: `/cabin` + save + restart → master cabin still at chosen tile. | [tests/03-position-survives-restart.md](tests/03-position-survives-restart.md) | **Required to merge.** Gated on save+reload primitive. |
| 4 | (Consolidated into Test 05 Variant A.) | [tests/04-movetostack-baseline.md](tests/04-movetostack-baseline.md) | See Test 05. |
| 5 | `HasSavedPosition` filter — Theory: 0 saved positions (sweep moves all) and 1 saved position (sweep skips that one). | [tests/05-movetostack-respects-saved.md](tests/05-movetostack-respects-saved.md) | **Required to merge.** Gated on save+reload + 2-client primitives. |
| 6 | Two players `/cabin` to distinct tiles → distinct master tiles, no dict-key collision. | [tests/06-two-players-distinct-tiles.md](tests/06-two-players-distinct-tiles.md) | Defer unless multi-client primitive lands cheaply (Test 05 already needs it). |
| 7 | All cabins moved (no hidden cabins remain) → no dummy, no NRE on reconnect. | [tests/07-no-hidden-cabins-no-dummy.md](tests/07-no-hidden-cabins-no-dummy.md) | Single-client; gated on warp primitive only. |
| 8 | Strategy switch None → CabinStack: `MigrateCabins` respects `HasSavedPosition`. | [tests/08-strategy-switch-respects-saved.md](tests/08-strategy-switch-respects-saved.md) | **Required to merge** (different code path from Test 05). Gated on save+reload + strategy-switch primitive. |
| 9 | FarmhouseStack: `/cabin` rejected; master state untouched. (+ off-Farm rejection sibling.) | [tests/09-farmhousestack-rejects-cabin.md](tests/09-farmhousestack-rejects-cabin.md) | Runnable today (private-message reply path verified by existing `LobbyCommandsEditingTests`). |
| 10 | None: `/cabin` works as before; no errors. | [tests/10-none-strategy-no-regression.md](tests/10-none-strategy-no-regression.md) | Gated on warp primitive (same as Test 02). |

**Merge gate (per `runtime-post-conditions-are-gates.md`)**: Tests 03, 05, 08 all exercise the on-disk persistence path that the bug actually lives on. The fix MUST NOT merge to master without at least Test 03 passing on real infrastructure (CI green or a documented manual sign-off in the PR). Static review of the diff is insufficient — the bug surfaced in production specifically because the persistence claim was never end-to-end verified.

## Test class layout

| Test | Class | File |
|------|-------|------|
| 01, 02, 06 | `CabinStrategyTests` (existing) — default CabinStack, `IsolationMode.SharedAssembly` | `tests/JunimoServer.Tests/CabinStrategyTests.cs` |
| 03, 04, 05, 07, 08 | `CabinPositionPersistenceTests` (new) — `Exclusive=true` + `CreateNewGameOnServerAsync` per test, follows the `CabinStrategyNoneTests` shape | `tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs` |
| 09 | `CabinStrategyFarmhouseStackTests` (new) — `Exclusive=true`, FarmhouseStack | `tests/JunimoServer.Tests/CabinStrategyFarmhouseStackTests.cs` |
| 10 | `CabinStrategyNoneTests` (existing) — None strategy, `Exclusive=true` | `tests/JunimoServer.Tests/CabinStrategyNoneTests.cs` |

Caveat: Test 06 sets `[TestServer(Clients = 2)]`, which forks the config-hash from the rest of `CabinStrategyTests` (per `test-broker-invariants.md`: "Different `clientsNeeded` test demand produces different config hashes"). So Test 06 lives in `CabinStrategyTests` for **organisational locality** but does not benefit from the shared-server reuse the class otherwise provides — it gets its own pool. If 2-client tests proliferate, consider `CabinStrategyTwoClientTests` as a separate class.

Per-test plan files use the section headers below so reviewers can scan them uniformly:

- **Verifies** — the bug-line / invariant the test guards (one sentence).
- **Strategy** — Cabin strategy and any non-default settings the test depends on.
- **Test class & method** — exact target file and method name.
- **Preconditions** — server config (`[TestServer]` attributes), starting state, what the harness has to guarantee before the test body runs.
- **Steps** — the operations the test performs, ordered.
- **Assertions** — concrete values to read from `/cabins`, chat history, or `/farmhands`. Each assertion is anchored to a `CabinsResponse` field, a chat keyword, or a setting.
- **Harness limits** — what cannot be observed directly and how the test approximates the real invariant. If a real invariant is unobservable, the plan says so and proposes either a follow-up harness change or a manual sign-off step.
- **Why this test exists** — the bug-line / regression risk this test catches; a future maintainer needs this to decide if the test still earns its slot.

### How tests should write expected coordinates

Two tile values appear repeatedly: `(-20, -20)` (the hidden stack, set by `CabinPositions.PlayerStack`) and the shared `StackLocation` (resolved by `StackLocation.Create(data)`).

- **`(-20, -20)` is a constant** in mod source — it's safe to hard-code in test assertions. Any change there is a deliberate refactor that should update the tests in the same diff.
- **The shared `StackLocation` is map-derived.** `FarmCabinPositions.GetDefaultStackPosition(...)` reads the `Paths` layer of the active farm map XNB; the value depends on `farmType` and on SDV's map content. Tests **must not** hard-code `(50, 14)` (the fallback) or any specific Paths-layer tile — capture it dynamically by reading `/cabins` for an unmoved cabin's tile, or by computing it from a known `farmer.Tile` reference. A future SDV update or farm-type addition that shifts the map's Paths layer would otherwise silently break every cabin test in this folder.
- **The `/cabin`-chosen tile is `(farmer.Tile.X + 1, farmer.Tile.Y)`** per `CabinCommand.cs:50`. Tests capture `farmer.Tile` from `GameClient.GetState()` immediately before sending `/cabin` — never assume a specific warp landing tile.

## Harness limits that constrain every plan

These limits inform every per-test plan and are stated once here so each plan can reference them without re-deriving:

1. **No client-side cabin/building inspection.** The test-client mod (`tests/test-client/`) exposes `/status`, `/farmer`, `/menu`, `/connection`, etc., but no endpoint reading `Game1.getFarm().buildings` from the client's view. This means we cannot directly observe what tile each peer's rewritten `locationIntroduction` placed their cabin at — the dummy-cabin invariant in Step 4 is unobservable end-to-end. We can only assert what's reachable via the master snapshot in `/cabins` or via chat replies.
2. **`/cabins` reports master server state**, derived from `Game1.getFarm().buildings` directly (`ApiService.cs:1219-1275`). After Step 3, the master cabin building IS relocated by `cabin.Relocate(newPosition.ToPoint())`, so `/cabins` will show the new tile. The dummy that Step 4 inserts is only present in the *outgoing message to peers* and never in master state — `/cabins` cannot see it.
3. **No `/restart` or `/reload-save` endpoint.** The closest available primitive is `POST /newgame` (`ApiService.cs:1614`), which destroys all save data. To exercise the `OnSaveLoaded` path that wipes `/cabin`-placed positions today (and that Step 5 is meant to defend), we need either (a) a new server-side endpoint that triggers a save+reload of the same world, or (b) a container-level restart that preserves the save volume. Tests 03, 05, 08 all rely on this primitive — Test 03 ranks the implementation options.
4. **The per-test timeout is 300 s** (`TestTimings.PerTestTimeout`). Plans that need long sequences (multi-client + chat + reconnect) must budget against that.
5. **`StartingCabins` and other config-hash inputs split the server cache** (`test-broker-invariants.md`). Every test that sets a non-default `[TestServer(StartingCabins=...)]`, `CabinStrategy`, or `ExistingCabinBehavior` either gets its own pool or shares with siblings carrying identical config — picking the same numbers as siblings keeps the run fast.
6. **Tests in `CabinStrategyTests` run with `IsolationMode.SharedAssembly` (no per-test reset).** The class's existing tests assume other tests on the same shared server may have farmers/cabins assigned (see `CabinStack_AfterPlayerJoins_CabinReplenished`'s comment about "ourCabin scoped to client.FarmerName"). New tests added there must scope every assertion to "our farmer" by UID, never assert global counts. Tests that need a clean farm should declare a new game via `Exclusive=true + CreateNewGameOnServerAsync(...)`, like `CabinStrategyNoneTests`.
7. **No existing 2+ client test pattern.** Grepping `Clients\s*=` across `tests/JunimoServer.Tests/` shows only `Clients = 0` (API-only) — no test today uses `Clients = N` for `N ≥ 2`. `Farmers.ConnectNewAsync` (Fixture/FarmerTestHelper.cs) only manages the primary `GameClient`. Multi-client tests must build their own orchestration on top of `LeaseClientAsync` (TestBase.cs:323): leasing the second client, threading its `GameTestClient` through a parallel `ConnectionHelper`, and tracking its farmer separately. Estimate: ~80–120 LOC of test-side helper code per multi-client class. Tests 05, 06, 07, 08 all rely on this. Recommend authoring a small `MultiClientCabinHelper` once and reusing it.
8. **`Helper.Data.WriteSaveData(...)` is in-memory until the next save event.** This is a SMAPI semantic, not specific to this fix. Tests that restart the server to verify persistence MUST first trigger a save (sleep through a day, or invoke a force-save chat command if available) — otherwise the dict is in-memory only and is lost on restart regardless of the code fix. A green test that didn't save first proves nothing.

## Out of scope for this fix

- Bounds checking on `/cabin` placement (TODO `(b)` in CabinCommand.cs:43). A separate plan if/when needed.
- Preview / cancel / confirm modes for `/cabin` (TODO `(c)` in CabinCommand.cs:44).
- Adding a server-side endpoint to manually delete a player's saved position (operator escape hatch). Achievable today by editing the save file's mod data.
