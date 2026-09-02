---
paths:
  - "mod/JunimoServer/**"
---

# Pick a persistence store by the lifetime it should have

SMAPI global data (`helper.Data.ReadGlobalData`/`WriteGlobalData`) lives at `.smapi/mod-data/<mod-uid>/<key>.json` under the game data path, which is the `saves` volume mount (`docker-compose.yml`), keyed by mod id. It therefore survives `/newgame`, `/reload`, and a save-folder deletion, and dies only with the saves volume. Per-save state belongs in the save (`ReadSaveData`) or in a file inside the save folder (`FarmhandOwnershipService`'s store).

**Why:** A scheduler's run history had to outlive the world reset it performs — the reset re-rolls the save folder — while the old CI reset deleted the whole saves volume and wiped every global-data store with it. Establishing where the file actually lands took a scan of the SMAPI assembly plus the compose mount; the placement decides whether state survives a reset, a reload, or a volume wipe.

**How to apply:** Before adding a `WriteGlobalData` key, state which of those three events the data must survive. World-independent state (load pointer, run history, persistent options) goes to global data; anything that describes one world goes with that world, or it silently carries over into the next one.
