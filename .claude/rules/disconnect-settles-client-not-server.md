---
paths:
  - "tests/JunimoServer.Tests/**/*.cs"
---

# `DisconnectAsync` settles the client, not the server — gate server-side assertions on removal

`TestBase.DisconnectAsync` (Exit → title screen → client disconnected) and `SecondFarmer.DisconnectAsync` (container disconnect) confirm only CLIENT-side state; neither waits for the server to process the disconnect and remove the player. Before asserting anything that assumes the player is gone server-side — offline/persisted `farmhandData` behavior, freed slots, "server sleeps alone" day transitions — gate on `ServerApi.WaitForPlayersRemovedByIdAsync([...uids])` (or the singular variant, or `Farmers.DisconnectAndWaitForSlotAsync` when a later join needs the slot back).

**Why:** The lobby-homed-spouses steady-state test disconnected both farmhands and immediately drove "server-only" nights. Nothing confirmed server-side removal, so the first offline night could race still-registered farmhands — and `getAllFarmhands` yields the LIVE Farmer root for registered players, so `marriageDuties` would read the live object instead of the persisted `farmhandData` entry the phase existed to exercise. The test passed repeatedly with the race latent (the disconnect usually wins); an external review caught it, not the runs.

**How to apply:** Any test sequence of `DisconnectAsync(...)` followed by assertions or day-advances that assume the player is offline must insert the removal gate between them. The tell is the phase's own description: "offline", "server alone", "persisted", "while disconnected". A green run does not prove the gate is unnecessary — it makes the scenario deterministic instead of usually-true.
