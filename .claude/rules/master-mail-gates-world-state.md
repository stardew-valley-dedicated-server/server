---
paths:
  - "mod/JunimoServer/Services/SaveImport/**"
  - "mod/JunimoServer/Services/GameLoader/**"
---

# Master mail/events gate world geometry — a master swap must copy them

A large class of world state — CC completion, greenhouse, Pam's house upgrade, Joja route, movie theater, island/parrot bridges — is gated on `Game1.MasterPlayer.mailReceived` / `eventsSeen`, which are **per-`Farmer`** `NetStringHashSet`s (`Farmer.cs:253`), NOT `FarmerTeam` state. ~200 gate sites across ~52 files: `hasCompletedCommunityCenter()` is a pure `mailReceived` check (`Farmer.cs:7249`); `ccDoorUnlock`/`ccPantry` (`GameLocation.cs:9416/9462`), `pamHouseUpgrade` (`GameLocation.cs:3047`), `communityUpgradeShortcuts` (`:970`). So any flow that replaces or blanks the master farmer (e.g. a save-import host swap installing a fresh "Server" master) silently *reverts the world*: `CommunityCenter.areasComplete[]` survives (location-stored), but nothing reconstructs the master's ccX mail flags from it — CC doors relock, the greenhouse becomes ruins, perfection breaks.

**Why:** Found designing the farm-importer host swap: an imported endgame save with a blank replacement master loses its world progression even though the location-level completion data is intact — the gates read per-Farmer flags that no code rebuilds.

**How to apply:** When replacing the master `Farmer`, copy `mailReceived`/`eventsSeen` from the original master onto the replacement verbatim (an allowlist drifts). Checks routed through `Utility.HasAnyPlayerSeenEvent` (all farmers, incl. offline) stay satisfied if the old owner remains as a farmhand, but direct `MasterPlayer.mailReceived.Contains(...)` checks do NOT — the copy is the load-bearing fix. Verified safe across a master swap *without* copying (team/independent stores, re-bound to the new master on load via `teamRoot`, `SaveGame.cs:908-972`): money/`totalMoneyEarned`, `constructedBuildings`, completed special orders, `friendshipData` (incl. player-to-player marriage), Junimo-chest `globalInventories`, world seed, `Building.owner` UIDs. Caveat: `cellarAssignments[1]` is force-reassigned to the new master each load (`updateCellarAssignments`, `Game1.cs:4521`) — a structural single-cellar limitation, not fixable by copying.
