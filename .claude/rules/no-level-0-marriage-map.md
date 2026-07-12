---
paths:
  - "mod/**/*.cs"
  - "tests/**/*.cs"
---

# A farmhand must be at houseUpgradeLevel ≥ 1 before marrying — there is no level-0 marriage map

`FarmHouse.updateMap` derives the map name as `"Maps/FarmHouse" + (upgradeLevel==0 ? "" : level) + (married ? "_marriage" : "")` (`FarmHouse.cs:1803`). At `upgradeLevel == 0` that yields `"Maps/FarmHouse_marriage"`, which exists in **no** SDV install — only `FarmHouse1_marriage`/`FarmHouse2_marriage.tmx` ship. So a level-0 married farmhouse crashes the host's `_newDayAfterFade` (`updateFarmLayout` → `_ApplyRenovations` → `loadMap`) with `ContentLoadException: FarmHouse_marriage.xnb not found` — aborting the day transition. Vanilla never hits this because it **requires `houseUpgradeLevel >= 1` to accept an engagement** (`NPC.cs:2283`, `RejectMermaidPendant_NeedHouseUpgrade`). A cabin's `upgradeLevel` is `owner.HouseUpgradeLevel` (the `FarmHouse.upgradeLevel` getter, `FarmHouse.cs:106-116`), so a farmhand at level 0 with an NPC spouse reproduces it.

**Why:** A wedding E2E that engaged a level-0 farmhand directly (bypassing the proposal's house-level gate) crashed the server the moment the marriage applied — one wasted E2E cycle with a non-obvious `ContentLoadException`. The crash is a *test/flow artifact* (real engagements are gated on level ≥ 1), not a server bug.

**How to apply:** Any test or mod flow that marries a farmhand to an NPC must first set that farmhand's `HouseUpgradeLevel >= 1` so the engine resolves `FarmHouse1_marriage` (which exists) instead of the missing level-0 map. Set it on the farmhand's own client — a farmhand's `Farmer` root is client-authoritative, so a host-side write is overwritten by the client's nightly full-root resend before the wedding fires. If you're synthesizing an engagement directly rather than going through the proposal path, you've skipped the `houseUpgradeLevel >= 1` check the proposal enforces — restore it yourself.
