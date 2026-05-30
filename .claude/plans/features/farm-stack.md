# Farm Stack — Research Spike

## Goal

Eventually: per-player farm instances (`Farm_JS_N`, `FarmHouse_JS_N`, …) so each player has their own farm rather than sharing the master farm. **This document is not a build plan** — it is a 1-2 day decompiled-first investigation that gates a future build plan.

## Why a spike, not a plan

A build plan for this feature must answer the questions below first. Per:

- `.claude/rules/host-automation.md` — **decompiled-first**: read the game's own implementation before designing patches around it.
- `.claude/rules/universal/simplest-solution.md` — verify a downstream consumer before adding scaffolding; don't build infrastructure to feed something whose feasibility is unknown.

LandGrants is the primary point of reference for "feasible-looking" multi-farm support, but it targets SDV 1.5 and uses techniques (e.g. `Game1.currentLocation.modData["JunimoServer." + targetName]` reads, global `String.Equals` patches) that need re-validation against 1.6 before any patch design rests on them. Until the questions below are answered, the feature has no grounded patch surface.

## Research questions

Each question is answered by reading `decompiled/sdv-1.6.15-24356/` (and grepping the runtime mod sources where relevant), not by speculation.

1. **`Game1.getFarm()` resolution path.** Read `decompiled/sdv-1.6.15-24356/Game1.cs`. How does it locate the primary `Farm`? List every call site that depends on a single primary `Farm` returning. (Spec out the blast radius before touching any of them.)

2. **`SaveGame` location-name assumptions.** Read `decompiled/sdv-1.6.15-24356/SaveGame.cs`. Does the loader iterate `locations` by index, by name, or by type? Does it require name-uniqueness for serialization? Where would `Farm_JS_2` need a corresponding `<location>` element to round-trip cleanly?

3. **Hardcoded `"Farm"` / `"FarmHouse"` strings in NPCs/events.** Grep the decompiled tree (`grep -rn '"Farm"' decompiled/sdv-1.6.15-24356/` and similar for `"FarmHouse"`). Categorize hits: name-literal compares vs. type checks vs. event-script tokens. Each hit is a place secondary farms either need to spoof "Farm" or be invisible to.

4. **Existing `dedicatedServer.Tick()` primitives.** Read `decompiled/sdv-1.6.15-24356/DedicatedServer.cs` (and any 1.6 dedicated-host helpers). Does the engine already provide farm-routing or location-resolution primitives we can hook into instead of patching every call site?

5. **Cabin-stacking overlap.** The user need driving "farm-stack" might already be served by the existing cabin-stacking (multiple cabins on one Farm). Document the actual user requirement that cabin-stacking does *not* satisfy. If none, archive the feature.

6. **LandGrants 1.5 → 1.6 portability.** Two LandGrants techniques bear scrutiny — a global `String.Equals` patch (treats `"Farm" == "Farm_JS_1"` as true everywhere) and per-farmer routing via `Game1.currentLocation.modData["JunimoServer." + targetName]`. For each:
   - Does it still work on 1.6's location resolution?
   - Is reading from `currentLocation.modData` (not `farmer.modData`) intentional?
   - Are these techniques considered fragile by the LandGrants maintainer (see their issues/notes)?

## Out of scope for this spike

- Any code changes.
- Any Harmony patches.
- Any new services, env vars, commands, or persistence.
- Permission management. Layering access control on top of unproven farm-instance plumbing is premature; revisit only once feasibility is established.

## Output

A markdown report (in `.claude/plans/research/` or appended below the spike, as preferred) with:
- A short answer to each numbered question above, citing decompiled file:line.
- A recommendation matching one of the three exit criteria below.

## Exit criteria

1. **Feasible** — answers to questions 1–4 show a tractable hook surface, and question 6 shows LandGrants' techniques port cleanly to 1.6. → Write a fresh build plan citing the decompiled findings.
2. **Infeasible without engine changes** — questions 1 or 2 reveal `Game1.getFarm()` or `SaveGame` assumptions that would require pervasive patching with no clean hook. → Archive the feature; document in admin docs why JunimoServer doesn't support per-player farms.
3. **Already covered by cabin-stacking** — question 5 shows the real user need is met by stacking cabins on the master farm. → Archive farm-stack; document the cabin-stacking migration path for users who think they want farm-stack.

## References (read-only)

- `decompiled/sdv-1.6.15-24356/Game1.cs` — `getFarm`, location iteration.
- `decompiled/sdv-1.6.15-24356/SaveGame.cs` — location serialization.
- `decompiled/sdv-1.6.15-24356/DedicatedServer.cs` — engine-side dedicated-host helpers (1.6).
- `mod/JunimoServer/Services/CabinManager/` — invariants per `.claude/rules/cabin-system.md`.
- [LandGrants mod](https://github.com/Platonymous/Stardew-Valley-Mods/blob/master/LandGrants/LandGrantsMod.cs) (also known as "Instant Multi Farm" on [Nexus](https://www.nexusmods.com/stardewvalley/mods/15855)) — for 1.5-era technique reference, not as proof of 1.6 feasibility.
