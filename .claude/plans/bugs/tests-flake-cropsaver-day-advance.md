# CropSaverTests `GardenPotCrop_KillSuppressedOnSeasonTransition_WhileOwnerOffline` flakes

This one test has **two distinct intermittent failure modes**. Keep them separate — they have
different symptoms, different root causes, and different fixes.

- **Mode 1 — "Day did not advance" (host time frozen).** `testBodyMs > 120s`, fails the
  `DayChange.WaitAsync` `Assert.True(..., "Day did not advance from Spring 28 → Summer 1")`.
  INVESTIGATED, not fixed, not yet reproduced with a clean mechanism. Kept below unchanged.
- **Mode 2 — pot destroyed overnight (`Assert.NotNull(stillThere)` null).** `testBodyMs ≈ 25s`,
  the day *does* advance, but the post-transition crop lookup finds nothing. ROOT-CAUSED and the
  fix is chosen (Option A below). This is the failure in the latest results
  (`2026-06-29T22-54-25Z_864566d`, ~18% over 11 recent runs).

---

# Mode 2 — pot destroyed by vanilla overnight weed spawn (ROOT-CAUSED)

## Symptom
Fails at `CropSaverTests.cs:172` `Assert.NotNull(stillThere)` with `Assert.NotNull() Failure:
Value is null`, phase `test_body`, `testBodyMs ≈ 25s` (well under the 120s `DayChangeTimeout`, so
this is NOT Mode 1). The Spring 28 → Summer 1 transition completes cleanly (`after save, starting
summer 1 Y1` in the server log); the pot crop at Farm (64, 22) is simply gone from the
`/test/crops` snapshot the assertion reads.

## Root cause (verified against decompiled SDV + the failing run log)
The test places its Garden Pot on **open Farm grass at (64, 22)** (`CropSaverTests.cs:53-69`
deliberately picked open grass south of the porch), then drives an overnight season transition.
The CropSaver Harmony fix (`KillCrop_Prefix`) correctly suppresses vanilla's *seasonal*
`Crop.Kill()` — but the crop here didn't die of season change. **Vanilla's overnight weed/debris
spawn destroyed the whole pot**, via a code path the mod never touches:

- `Farm.DayUpdate` (`decompiled/.../StardewValley/Farm.cs:536`) calls
  `spawnWeedsAndStones((Game1.season == Season.Summer) ? 30 : 20)` every non-winter morning, with
  the default `spawnFromOldWeeds = true`.
- That loop (`GameLocation.cs:15243`) picks existing weed tiles (`Utility.TryGetRandom(objects,…)`)
  and spreads debris to an adjacent tile. When the target tile holds an **unprotected** object it
  does `objects.Remove(vector + vector2)`, sets `Game1.debugOutput = value5.Name + " was
  destroyed"`, and drops a weed/stone in its place (`GameLocation.cs:15384-15396`). The only
  protected objects are Fence / Chest / Tapper (`(O)590`) / Mushroom Log — **a Garden Pot is not
  protected**.
- The Standard farm seeds rocks/weeds/twigs at game-creation (`CropSaverTests.cs:327-328`'s own
  comment acknowledges this), so old-weed source tiles surround the open-grass pot location.

The failing run log shows it exactly (`containers/server-1/container.log`, ~`23:03:32`): the
transition logs both `Killing crop owned by 6120…` (the sibling `IsRegistered` test's *leftover*
pot, killed by the legitimate seasonal path) **and** `DebugOutput: Garden Pot was destroyed` (the
weed-spawn removing a pot). The post-transition `/test/crops` snapshot then has no pot at (64, 22),
so `SingleOrDefault(...)` returns null.

**RNG-driven** (overnight weed-spread tile selection), which is why it's ~18% flaky rather than
always-failing — most nights the spread misses the pot tile; occasionally it lands on it.

### Why it's the pot, not the crop
`KillCrop_Prefix` only gates `Crop.Kill()`. The weed spawn removes the **`IndoorPot` object** from
`location.Objects` outright — the crop is collateral. No mod hook sits on that path, and no
CropSaver change can protect the pot there; the pot must simply not be where the spawn can reach.

### Contributing factor — SharedClass pot accumulation
`CropSaverTests` is `[TestServer(Isolation = SharedClass)]`, so
`GardenPotCrop_IsRegisteredWithCropSaverWatcher` (TileA = 64,21) runs first on the **same Farm** and
leaves its pot + crop behind. Two pots in the open-grass spawn zone plus accumulated weeds raise the
per-night probability that the weed-spread RNG lands on a pot tile. Not the root cause, but it
compounds Mode 2's flake rate. (Also means the `Killing crop owned by 6120…` in the failing log is
the *prior* method's crop — a red herring for this failure; the failing test's own crop at (64,22)
survived the seasonal path and was destroyed only by the weed spawn.)

## Fix — Option A: make the pot spawn-immune by clearing its 3×3 neighborhood (SETTLED, not yet applied)

**This is a test-environment flaw, not a server bug** — the CropSaver feature works; the test put
the pot where vanilla overnight debris-spawn can destroy it. The realization of "Option A" is NOT a
special tile — it's **clearing the pot's 3×3 tile neighborhood of objects before the overnight
transition**, which makes the pot deterministically immune at its *current* (64,21)/(64,22) tiles.
No map decode, no guessed coordinates, and the test's contract stays intact (still outdoors +
non-season-immune, so the seasonal `Crop.Kill` path — the thing under test — still fires and
`KillCrop_Prefix` is still exercised).

### The exact destroy path (fully traced in decompiled SDV — this is what makes 3×3 sufficient)
`Farm.DayUpdate` (`Farm.cs:533-538`) runs two overnight spawn passes. Only ONE can destroy a pot,
and it is strictly local:

- **`spawnWeedsAndStones(30)` (`Farm.cs:536` → `GameLocation.cs:15243`), default
  `spawnFromOldWeeds = true`.** The target tile is `key + vector` where `key` is a *random existing
  object* (`Utility.TryGetRandom(objects, …)`, `:15286`) and `vector` is a random offset in
  `[-1,1]²` (`:15274`). So it can only ever act **within ±1 tile of an object that already exists**.
  When that target holds an unprotected object it does `objects.Remove(…)` + logs
  `"<name> was destroyed"` (`:15384-15396`); a Garden Pot is not on the protected list
  (Fence/Chest/Tapper `(O)590`/Mushroom Log). **⇒ if no object sits in the pot's 3×3 neighborhood,
  no spread can select the pot tile.**
- **`spawnWeeds(weedsOnly:false)` (`Farm.cs:538` → `GameLocation.cs:4947`).** Picks random map-wide
  tiles, but the placing branch requires `value == null` (empty tile, `:4989`) and only *adds* a
  `Grass` terrainFeature — it **never removes an existing object**. **⇒ cannot destroy the pot.**

The Standard farm seeds rocks/weeds/twigs at game-creation (`CropSaverTests.cs:327-328` comment),
which is exactly what supplies the adjacent objects that let pass 1 reach the pot — clearing the
3×3 removes them.

### Why place + plant + season-transition all still work (verified in decompiled SDV — no empirical run needed)
- **Placement:** `PlacePot` (`tests/test-client/GameControl/ActionsController.cs:224-272`) does not
  check tile placeability — it clears the tile's object+terrainFeature and does
  `location.Objects.Add(tile, new IndoorPot(tile))` directly.
- **Planting:** `PlantCrop` (`:360`) plants into `pot.hoeDirt.Value` (the pot's own inner
  `HoeDirt`). Vanilla `HoeDirt.plant` (`HoeDirt.cs:536`) → `CanPlantSeedsHere` →
  `CheckItemPlantRules(itemId, isGardenPot, GetData()?.CanPlantHere ?? IsFarm, …)`
  (`GameLocation.cs:670`) gates only on the crop's `PlantableLocationRules` + season
  (`data.Seasons.Contains(season)`); the `tileX/tileY` params are unused for seeds and **nothing in
  the seed path checks Diggable or NoSpawn**. Cauliflower is planted on Spring 1 (in season) so the
  season gate passes. ⇒ place+plant succeed at the current tiles regardless of the neighborhood.
- **Season-transition Kill path stays exercised:** `IsCropSeasonImmune()` is a per-*location*
  property (indoor/greenhouse), independent of tile. The Farm is always `IsOutdoors &&
  !IsCropSeasonImmune()`, so keeping the pot on the Farm keeps `KillCrop_Prefix` under test.

(These are all settled by reading the code — do NOT re-open them as "verify empirically". The only
thing a run confirms is the *absence* of the destroy line, per the verification gate below.)

### Steps
1. **Clear the pot's 3×3 neighborhood right before the overnight transition** in
   `GardenPotCrop_KillSuppressedOnSeasonTransition_WhileOwnerOffline`. The existing
   `GameClient.Actions.ClearArea(locationName, tileX, tileY, width, height)`
   (`ActionsController.cs:281`) already does exactly `removeObjectsAndSpawned` over a rect — call it
   for the 3×3 centered on TileB: `ClearArea("Farm", TileB_X - 1, TileB_Y - 1, 3, 3)` (confirm
   `ClearArea`'s (x,y,width,height) semantics = top-left origin; adjust origin if it's centered).
   Do this **after** planting/arming but **before** `SetTime`/`SetClockSpeed(20)` triggers the
   pass-out, and crucially **after the owner disconnects** (the disconnect deletes the farmhand's
   cabin, which won't reintroduce Farm objects, but sequence it so the cleared state is what the
   `DayEnding` spawn sees). The farmhand must still be connected for `ClearArea` (it's a test-client
   action on `player.currentLocation`); call it while the farmhand is on the Farm, i.e. fold it into
   `PlacePotAndPlantCauliflowerAsync` right after `PlacePot`, OR add a dedicated clear step before
   the disconnect. Prefer the latter so both crop tests share one obvious "make immune" line.
   - The tiles themselves stay at **(64,21)/(64,22)** — no constant changes, no map decode.
2. **Refresh the XML-doc comment** (`CropSaverTests.cs:53-69`). Keep the tile coordinates but
   replace the "visible on screenshot / plantable" reasoning with the real invariant: *the pot's
   3×3 neighborhood is cleared before the overnight transition so vanilla `spawnWeedsAndStones`
   (`Farm.DayUpdate`, spreads debris only within ±1 of an existing object) cannot destroy the pot;
   the tile stays on the outdoor, non-season-immune Farm so the seasonal Kill-suppression path is
   still exercised.* Delete the stale "open Farm grass south of the porch" rationale.
3. **Make the final assertion self-identifying** (`tests-assert-via-http-api.md` — "give every
   assertion a description"). Replace the bare `Assert.NotNull(stillThere)` (`:172`) with a
   presence check that names the tile and the mechanism, so a residual flake self-diagnoses instead
   of surfacing a bare `Value is null`:
   ```csharp
   var potRow = cropsAfter.Crops.SingleOrDefault(c =>
       c.IsInPot && c.LocationName == "Farm" && c.TileX == TileB_X && c.TileY == TileB_Y);
   Assert.True(potRow != null,
       $"Garden Pot crop missing at Farm ({TileB_X},{TileB_Y}) after Spring 28 → Summer 1 — "
       + "the IndoorPot was removed (check the server log for 'Garden Pot was destroyed', vanilla's "
       + "overnight weed spawn spreading onto the pot tile), not merely killed.");
   Assert.True(potRow.IsAlive, /* existing message */);
   ```

### Out of scope / deliberately not done
- **No mod change.** CropSaver is correct; protecting the pot from the weed spawn at the mod layer
  would be papering over a test placement flaw (`simplest-solution.md`).
- **No tile-coordinate change and no map probe.** The 3×3-clear makes the current tiles immune, so
  the map-decode / non-Diggable-tile hunt is unnecessary. (For the record: the Standard farm's open
  area around (64,21) is Diggable — the cabin-placement tests treat that whole band as buildable
  (`CabinPlacementHelper` uses (40,18)/(40,30)) — so there is *no* conveniently passable
  non-Diggable open tile there anyway; the farmhouse-footprint tiles that ARE `NoSpawn` are mostly
  non-passable. The 3×3-clear sidesteps that entirely.)
- **Rejected Option B** (build a Gold Clock / set `goldenClocksTurnedOff=false` via a new `/test/*`
  endpoint to suppress the whole Farm spawn — `spawnWeedsAndStones` early-returns then,
  `GameLocation.cs:15245`) as heavier: needs a new endpoint + engine reconcile
  (`test-state-setter-runs-engine-reconcile.md`) and is unnecessary once the neighborhood is
  cleared. Recorded so the next reader knows it was considered.
- **Sibling immune-location test (`PotCropInImmuneLocation_…`, `:182`) needs no change** — its pot is
  in the Cabin interior, and `spawnWeedsAndStones`/`spawnWeeds` run via `Farm.DayUpdate` only, never
  on a cabin `FarmHouse` interior (which is also season-immune). Not exposed to Mode 2.
- **Sibling-pot cleanup between SharedClass methods** is a *contributing-factor* mitigation, not
  the fix; once the pot is spawn-immune it no longer matters. Skip unless the tile audit can't find
  two immune tiles and we fall back to reducing accumulation.

### Verification gate (runtime, per `runtime-post-conditions-are-gates.md`)
Re-run the test (ideally several times, e.g. `make test-llm FILTER=…` in a loop) and, in the
passing runs' `containers/server-*/container.log` for the test window, confirm **no `Garden Pot was
destroyed` line** appears for the (new-)TileB pot, and that `KillCrop_Prefix` still had something to
suppress (the crop went out-of-season) — i.e. the test still exercises the path it names, per
`passing-test-isnt-proof-the-scenario-ran.md`. A green run alone isn't proof; read the log.

---

# Mode 1 — "Day did not advance" (host time frozen) — INVESTIGATED, not fixed

Status: INVESTIGATED, not fixed. Intermittent. NOT caused by the watchdog/health-probe/daemon-fast-fail work
(host-poison-deadlocks-run.md) — confirmed: that work touches Program.cs / ManagedServer.cs /
ServerContainer.cs / GameClientContainer.cs, none of which touch host time progression; the
session's only `AlwaysOn.cs` change is the festival-handler move.

## Symptom
`GardenPotCrop_KillSuppressedOnSeasonTransition_WhileOwnerOffline` fails with
`Day did not advance from Spring 28 → Summer 1`. `testBodyMs` was 135.6s, exceeding the 120s
`DayChangeTimeout` (`TestTimings.cs:369`). (Seen in `2026-06-26T10-53-15Z_864566d`; distinct from
Mode 2 above, whose body is ~25s.)

## What the test does (`CropSaverTests.cs:126-155`)
Disconnects the owner (so the host farmer is alone, `otherFarmers.Count == 0`), sets time to
`PrePassOutTime` = 2550 (1:50 AM) via `/time`, sets clock to 20× via `/clock-speed`, then waits
120s (`DayChange.WaitAsync`) for the host to pass out at 2 AM (2600) and transition Spring 28 →
Summer 1. With the owner offline, `HandleAutoSleep` early-returns (`AlwaysOn.cs:1139`,
`otherFarmers.Count == 0`), so the test relies on vanilla's **forced 2 AM pass-out**.

## Root cause (isolated from `containers/server-0/container.log`, the CropSaver instance =
`sdvd-server-9140b536`)
- `[10:59:06/19] peer_disconnected` (the owner, id `1840541…`) — owner offline. ✓
- `[10:59:20] Time set to 2550 via API` ✓ and `[10:59:20] Clock speed set to 20x (35ms/game-minute)` ✓
- Then `/status` + `/health` keep flowing (host alive, responsive) through 11:01:23 — but **no
  2600, no pass-out, no `going to bed`, no `NewDay`/day-transition** ever appears until the test's
  cleanup `[11:01:23] Clock speed set to 1x`.
- So **time froze at ~1:50 AM and never reached the 2 AM forced pass-out within 120s, despite 20×
  clock.** 50 game-minutes at 35ms/min is <2 real seconds — it should have passed out near-instantly.

## The mechanism to chase when fixing (decompiled-first, do NOT rush)
Vanilla `Game1.shouldTimePass()` (`Game1.cs:9277`), multiplayer branch (`:9291`):
```csharp
if (IsMultiplayer && !ignore_multiplayer)
    return !netWorldState.Value.IsTimePaused;
```
This server is `IsMultiplayer`, so time passes iff `!IsTimePaused`. But the mod's `HandleAutoPause`
(`AlwaysOn.cs:1074`) sets **`IsPaused`**, and `NetWorldState.IsPaused` (`:252`) and
`IsTimePaused` (`:264`) are **independent fields with no cross-setting** — so `HandleAutoPause` does
NOT influence the multiplayer `shouldTimePass` branch. The freeze therefore comes from elsewhere
(some path setting `IsTimePaused`, or a different gate on the alone-host clock). Needs a focused
decompiled-first trace of what advances `Game1.timeOfDay` on the headless host when `otherFarmers
.Count == 0` and `IsTimePaused` is the only multiplayer gate. The `IsPaused`/`IsTimePaused`
distinction is a strong lead but was not confirmed as the freeze cause.

## Fix options (pick after the trace above)
1. **Test-only, minimal:** bump `DayChangeTimeout` (or this test's wait) — only correct if the host
   *does* eventually pass out just slowly under load. The log shows NO progression in 120s, so this
   likely just masks a real freeze — verify the host actually advances before choosing this.
2. **Mod fix:** if the alone-host clock is genuinely frozen during the 2500→2600 pass-out window,
   the forced pass-out needs to drive the transition directly (the host can't rely on the clock
   ticking to 2600 if time is paused for the lone host). This is a host-automation change — follow
   `.claude/rules/host-automation.md` (decompiled-first; check `DedicatedServer.cs` for how the
   game's own dedicated path forces the lone-host day end).

Separate from the wedge/finalization work — tracked here so it isn't lost.
