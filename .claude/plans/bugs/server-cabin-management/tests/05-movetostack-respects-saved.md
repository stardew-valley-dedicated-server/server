# Test 05 — CabinStack `MoveToStack`: `HasSavedPosition` filter — Theory covering both selectivity directions

## Verifies

The `SyncExistingCabins` MoveToStack branch (`CabinManagerService.cs:335-346`) — Step 5's first filter site — moves visible cabins to the hidden stack except those owned by a player with an entry in `PlayerCabinPositions`.

This test covers **both directions** of the filter as a Theory:
- **Variant A** — no saved positions: every visible cabin moves. Guards against "the filter over-matches and the sweep dies."
- **Variant B** — one saved position: that one cabin stays at its tile, all others move. Guards against "the filter under-matches and the sweep wipes intentional positions."

## Strategy

CabinStack with `ExistingCabinBehavior=MoveToStack`. Seed via `None` strategy (which spawns visible cabins via `BuildNewCabinVisible`), then reload with the strategy switched. Since the variants share setup-up-to-`/cabin` and only differ in whether `/cabin` was invoked, a Theory is materially cheaper than two separate tests.

## Test class & method

`tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs` →
`CabinStack_MoveToStack_HasSavedPositionFilter` (`[Theory]`, two `[InlineData]` variants).

`[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true, DeferAcquisition = true)]` — same shape as `CabinStrategyNoneTests`, plus `DeferAcquisition` so the test can configure each variant's server itself.

## Preconditions

- Save+reload primitive (Test 03 harness limits — same blocker).
- Save-flush primitive (sleep-through-day or `/save` chat command — same as Test 03).
- 2 farmers reach the Farm location and run `/cabin` (variant B only). 2 clients required (see README "Harness limits #7" — multi-client orchestration cost).
- Visible cabins to seed `SyncExistingCabins` MoveToStack with: start under None strategy, ≥ 2 starting cabins.

## Steps (Theory body, parameterized by `int savedPositionsCount`)

1. `await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 2);` — seed 2 visible cabins at Paths-layer positions.
2. **Connect 2 farmers (A and B)** through the multi-client helper (README #7).
3. (Variant B only — `savedPositionsCount == 1`) Drive farmer A to the Farm at a known tile, send `/cabin`. Capture `aChosenTile`. **Capture cabin B's pre-reload tile from `/cabins`** (do NOT hard-code; depends on map data — see README "How tests should write expected coordinates" / Test 01's rationale).
4. Both farmers disconnect.
5. **Save flush** — sleep through a day OR force-save (Test 03 step 7).
6. Switch CabinStrategy to CabinStack and ExistingCabinBehavior to MoveToStack via the available knob (chat `/settings`, `POST /settings`, or in-place file edit before reload — investigate during implementation).
7. **Reload save** (Test 03 step 8 primitive).
8. `var after = await ServerApi.GetCabins(TestCt);`.

## Assertions

**Variant A — `savedPositionsCount == 0`** (no `/cabin` runs in the test):
- `after.Cabins.Count(c => c.Type == "CabinStack" && !c.IsHidden) == 0` — every CabinStack cabin is at the hidden stack.
- For both A's and B's cabins: `(TileX, TileY) == (-20, -20)` and `IsHidden == true`.

**Variant B — `savedPositionsCount == 1`** (only A ran `/cabin`):
- A's cabin: `OwnerId == uidA`, `(TileX, TileY) == aChosenTile`, `IsHidden == false`.
- B's cabin: `OwnerId == uidB`, `(TileX, TileY) == (-20, -20)`, `IsHidden == true`.
- `after.Cabins.Count(c => c.OwnerId != 0)` is unchanged from pre-reload (no cabins lost in the migration).

## Harness limits

- **Same save+reload, save-flush, multi-client, and warp blockers as Tests 02, 03.** All of them gate this test.
- **Cabin B's starting tile is map-dependent.** Variant B's "B's cabin tile is unchanged through the strategy switch" assertion is implicit-Theory-A. The Theory pattern lets us assert "B's tile == (-20, -20)" in Variant B (it WAS moved by the sweep) and **does not** assert "B's tile == its pre-seed visible position" — that property is Variant A's job under None→CabinStack with no saved positions, but None→CabinStack with `MoveToStack` is exactly Variant A here.

## Why this test exists

This is the test most directly exercised by issue #64 in production. Catches:
- Step 5 filter inverted (`HasSavedPosition` returns true for `!ContainsKey`) — Variant B fails: A is moved to stack, B stays.
- Step 5 over-filters (`HasSavedPosition` always returns true) — Variant A fails: nothing moves, the sweep is dead.
- `HasSavedPosition` reads `Data.PlayerCabinPositions` during a window when `Data` is null or in transit between `Read()` and `SyncExistingCabins` — both A and B incorrectly classified.
- Owner-UID extraction in `HasSavedPosition` (`cabin.GetIndoors<Cabin>()?.owner?.UniqueMultiplayerID`) silently returning `0` on imported cabins where `owner` is rehydrated lazily.
- Owner-name vs owner-id confusion (`PlayerCabinPositions` is keyed by UID; using owner.Name to look up would break for any farmhand without an active connection at reload time).
- The opposite-bug failure mode where the filter applies to the wrong tile (`IsLobbyOrEditing` precedence change) and cabins-with-saved-positions land at lobby tile `(-21, -21)`.

## Note on Test 04

Test 04's earlier framing ("MoveToStack baseline: imported visible cabins with no saved positions → all moved") is **Variant A here**. The standalone Test 04 plan file is retained as a pointer to this Theory; if implementation prefers two `[Fact]`s over a `[Theory]`, the same coverage is achievable by splitting back — but the setup is shared and a Theory keeps the wall-clock down.
