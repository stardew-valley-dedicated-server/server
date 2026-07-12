# Investigate: per-tick game-code inefficiencies the server mod could rewrite

## Goal

Identify which inefficiently-written per-tick paths in vanilla SDV are worth extracting and rewriting (via Harmony) in the server mod, on a headless host (`multiplayerMode=2`, `SERVER_TPS=5`, no rendering). This is an **investigation + decision plan**, not an implementation plan. It ends with a ranked candidate list, the seam each would patch, the correctness risk, and the **measurement that gates committing to each one**.

**Hard prerequisite, stated up front:** none of these should be implemented before the headless network load tester (`.claude/plans/features/tests-headless-network-client.md`) lands and produces a load signal. The reason is in the findings below — every genuinely-real cost here is gated on *player/NPC presence*, and the existing test suite shows **zero `slow_tick` events across a full run** (health-watchdog telemetry in `infrastructure.jsonl`), i.e. game-thread execution is not currently the bottleneck at test loads. We cannot rank these by real impact without the ability to put 20–50 connected players on the server. Per `runtime-post-conditions-are-gates.md`, a fix's predicted effect is a claim to verify against runtime data, not a reason to ship.

## What was already verified (and what was debunked)

A static audit (2026-06-22, no measurement) used four parallel readers over `decompiled/sdv-1.6.15-24356/`, then I verified the load-bearing claims at the cited lines. **Three of the four top "CRITICAL" agent claims were false or overstated on direct read** — recorded here so this plan doesn't re-chase them, per `subagent-findings-are-claims.md`.

### Already gated/cached by vanilla — DO NOT rewrite these

- **Empty-cabin heavy update is already skipped.** `Game1._UpdateLocation` (`Game1.cs:5846-5871`) sets `flag = location.farmers.Any()` (plus a remote-viewing check) and only runs `UpdateWhenCurrentLocation(time)` — the terrain/debris/projectile/sprite/furniture cascade — when `flag` is true. Empty cabins run only the light `updateEvenIfFarmerIsntHere`. The agent's "skip empty locations for ~85% reduction" is mostly already done by the engine.
- **Location delta broadcast is dirty-gated.** `Multiplayer.broadcastLocationDeltas` (`Multiplayer.cs:390-397`) only serializes when `location.Root != null && location.Root.Dirty`. The "re-serializes every location every tick" claim is false. `broadcastWorldStateDeltas` (`:481-495`) and `broadcastFarmerDeltas`/`broadcastFarmerDelta` (`:287-330`) are likewise dirty-gated and serialize **once** then fan the same byte[] to all peers (no per-peer reserialize).
- **NetField tick tree short-circuits clean subtrees.** `AbstractNetSerializable.Tick()` (`:183-201`) only recurses into children with `NeedsTick || ChildNeedsTick`. So `Multiplayer.updateRoots`'s per-location `updateRoot` (`Multiplayer.cs:362-373`) walk is O(locations) of a `Clock.Tick()` + cheap dirty check, **not** O(locations) of serialization. Real but small; see Candidate 5.
- **`GetMachineData()` / `Crop.GetData()` do not reparse.** `DataLoader.Machines`/`Crops` (`DataLoader.cs:329-332, 181-184`) call `content.Load<T>("Data\\Machines")`, which is content-manager-cached — it returns the *same dictionary instance* every call. Per-call cost is a cache hit + `GetValueOrDefault`, not a parse. The agent's "no cache → reparses every call → most impactful fix" is overstated. (Docstring at `DataLoader.cs:180` even directs most code to the global `Game1.cropData` cache.)

## The candidates (verified real; impact unproven without measuring)

Ranked by static badness × plausible frequency on *this* server. Every one carries the same caveat: it only does work when NPCs are moving/pathing or a farmer is present in the location, so a cabin-clustered server may rarely hit it.

### Candidate 1 — A* allocates per node, with no pooling (the standout architectural smell)

- **What vanilla does:** `PathFindController.findPath` (`PathFindController.cs:~206-235`) and the parallel `findPathForNPCSchedules` (`:~431-476`) allocate a `new PathNode(...)` for every neighbor expanded and a `new Rectangle(...)` for every walkability test, up to a 30,000-node cap. `findPathForNPCSchedules` also calls `priorityQueue.Contains(node, priority)` (O(n)) before every `Enqueue`.
- **Frequency:** Per pathfind, not per tick. Pathfinds fire on NPC schedule transitions and movement re-routes. Spikes at the midnight schedule rebuild for all NPCs at once.
- **Why a rewrite is defensible:** classic alloc-per-node A* with no object pool; on a 24/7 server the GC churn accumulates. This is the most architecturally clear win *if* it proves hot.
- **Patch seam:** Harmony-replace `findPath`/`findPathForNPCSchedules` with pooled `PathNode`s + a reused `Rectangle`, or transpile the allocation sites. High-effort, high-risk (pathfinding correctness is load-bearing for NPC movement replication).
- **Gating measurement:** load tester + a synthetic NPC-heavy location with players present; observe `AvgTickMs` spikes correlated with schedule transitions, and GC gen-0 rate. If pathfinds are rare on a cabin-clustered server, this is noise — do not build it.

### Candidate 2 — `isCollidingPosition` is a full linear scan, called per A* node and per movement frame

- **What vanilla does:** `GameLocation.isCollidingPosition` (`GameLocation.cs:~2493-2800`) linearly scans animals, buildings, resourceClumps, furniture, largeTerrainFeatures, objects-at-corners, and characters for every call. No spatial index.
- **Frequency:** Called once per A* node expansion (compounding Candidate 1) and ~4× per moving NPC per tick from `Character.MovePosition` (`Character.cs:~824-976`, which also allocates a `Rectangle` per direction via `nextPosition`).
- **Why a rewrite is defensible:** O(location contents) per call with no broadphase. A per-location spatial grid (rebuilt on mutation) would cut node-expansion cost dramatically — but only where pathfinding/movement is actually hot.
- **Patch seam:** a mod-side spatial index keyed per location, consulted by a `isCollidingPosition` prefix. Very high-effort (must mirror vanilla's full collision resolution or risk NPCs walking through objects — see `mirror-target-component-resolution.md`), high-risk.
- **Gating measurement:** same as Candidate 1; these two are coupled — Candidate 2's cost is dominated by being called inside Candidate 1.

### Candidate 3 — Per-tick closure/array allocation on active locations

- **What vanilla does** (all in `GameLocation.UpdateWhenCurrentLocation`, so only when a farmer is present):
  - `critters?.RemoveAll((Critter c) => c.update(time, this))` (`:4072`)
  - `debris.RemoveWhere((Debris d) => d.updateChunks(time, this))` (`:4152`)
  - `projectiles.RemoveWhere((Projectile p) => p.update(time, this))` (`:4155`)
  - `tempAnimals` copy-then-clear over `animals.Pairs` (`:4270-4281`)
  - `animals.Values.ToArray()` in `updateEvenIfFarmerIsntHere` (`:4389`) — this one runs for non-current locations *with animals* (barns/coops), so it fires on the farm even with no farmer in the barn.
- **Frequency:** Per tick on locations with a farmer (first four); per tick on any location with animals (the `.ToArray()`).
- **Why a rewrite is defensible:** each closure/`RemoveWhere`/`ToArray` allocates per tick; the project treats per-tick game-thread allocation as a from-the-start constraint (`mod-game-thread-allocation.md`). Rewrites are mechanical: backwards-index `for` loops with in-place removal, reused buffers.
- **Patch seam:** transpile or prefix-replace the specific update bodies. Medium-effort, **medium-risk** — must preserve removal semantics exactly (the predicates have side effects: `update`/`updateChunks` *do work* and return whether to remove).
- **Gating measurement:** the `.ToArray()` at `:4389` is the most likely steady-state win because it runs whenever animals exist regardless of player presence — measure GC gen-0 with N barns. The farmer-present closures need a populated active location to matter.

### Candidate 4 — `performTenMinuteClockUpdate` cascade (per-10-min, not per-tick)

- **What vanilla does:** `Game1.performTenMinuteClockUpdate` (`Game1.cs:~6005-6014`) loops all locations doing a `NameOrUniqueName` **string** compare per location, then `performTenMinuteUpdate` + `timeUpdate(10)`, which cascade into `passTimeForObjects(10)` (`GameLocation.cs:~13624`, loops all objects calling `minutesElapsed`) and all-NPC `checkSchedule`/`performTenMinuteUpdate` (`GameLocation.cs:~13640-13652`).
- **Frequency:** 60×/day (every 10 in-game minutes), **not per tick** — so ranked below the per-tick candidates despite touching many objects.
- **Why a rewrite is defensible (modestly):** the per-location string compare (`current2.NameOrUniqueName == currentLocation.NameOrUniqueName`) is replaceable with reference equality; `passTimeForObjects` over hundreds of machines is O(objects) but only 60×/day. This is a "tidy if measured to matter" item, not an architectural fix.
- **Patch seam:** prefix on the string-compare; otherwise leave the cascade alone (it's correctness-critical machine processing).
- **Gating measurement:** look for a 10-minute-boundary stall in the tick-time trace under load with many machines. Likely low — daily-rate work amortized.

### Candidate 5 — `checkSchedule` runs per-NPC per-tick; `updateRoots` walks all locations per tick

- **What vanilla does:** `NPC.checkSchedule` (`NPC.cs:~4093-4158`) is called every tick from `NPC.update` (`:3194`); it gates the actual dictionary lookup behind `lastAttemptedSchedule < timeOfDay` but still resets per-tick state each call. `Multiplayer.updateRoots` (`Multiplayer.cs:362-373`) `ForEachLocation` + `Clock.Tick()` per location per tick.
- **Frequency:** Per NPC per tick; per location per tick.
- **Why it's *low* priority:** both are already mostly-gated. `checkSchedule`'s expensive branch is time-gated; `updateRoots`'s per-location work is the cheap clock-tick + dirty-check verified above. The "per-tick" framing overstates them.
- **Patch seam:** batching `checkSchedule` to 10-game-minute boundaries is possible but risks NPC schedule timing correctness for unclear gain. Probably not worth it.
- **Gating measurement:** only revisit if the load tester shows NPC-heavy locations dominating tick time *and* Candidates 1–2 are addressed.

## Cross-cutting risks

- **Correctness over speed.** Every candidate patches a path whose behavior is load-bearing for multiplayer state replication (pathfinding drives NPC position deltas; collision drives where NPCs end up; removal predicates do real per-frame work). A wrong rewrite produces NPCs walking through walls or desynced positions — bugs that look like server faults. Any implementation plan must include adversarial compatibility verification per `plan-discipline.md` (LAN+Steam, players present/absent, day transition, festival).
- **Headless still loads more than "no rendering" implies.** Per `chat-font-language-tag.md`, measure/parse paths run on the game thread even with `Game.Draw` patched out — don't assume a path is dead just because it's visual-adjacent. Confirm each candidate actually executes headless before patching it.
- **Mirror full resolution.** Candidate 2's spatial index must reproduce vanilla `isCollidingPosition`'s *complete* resolution order, not the happy path (`mirror-target-component-resolution.md`), or NPCs silently clip.
- **`simplest-solution.md` exception applies narrowly.** Allocation-minimal rewrites (pooling, buffer reuse) are the qualifying simplest only on a *declared, measured* hot path (`mod-game-thread-allocation.md`). Until the load tester declares the path hot, building a spatial index or A* pool is speculative over-engineering.

## Recommended sequencing

1. **Land the load tester first** (`tests-headless-network-client.md`). No candidate proceeds without it.
2. **Instrument before patching.** Use the existing `/stats` (`AvgTickMs`, `Tps`, `MemoryMb`, `GcGen0/1/2`, `GameThreadWaitMs` — `ApiService.cs:337-368`); if it can't attribute cost to a path, add a temporary per-path `Stopwatch` emit (gated to `Env.IsTest`) rather than guessing. The boot-phase instrumentation plan (`tests-boot-phase-instrumentation.md`) is the template for additive `mod_phase`-style events.
3. **Pick the one candidate the data implicates.** Best a priori guess is the **A* allocation + collision-scan pair (Candidates 1+2)** — the only genuinely architectural smell — but on a cabin-clustered server the steady-state winner may instead be the **`animals.Values.ToArray()` at `GameLocation.cs:4389`** (Candidate 3), since it's the one real per-tick allocation that runs without a player present. Let the load signal decide; do not batch-implement.

## Open questions for the implementation phase

- Does the server actually run NPC pathfinding at a meaningful rate, or do most NPCs sit idle because no player is in town? (Determines whether Candidates 1–2 are real or theoretical.)
- How many barns/coops does a typical hosted farm have? (Sizes Candidate 3's `.ToArray()` cost.)
- Can a Harmony transpiler replace the A* allocation sites without forking the whole method? (Determines Candidate 1 effort/risk.)
