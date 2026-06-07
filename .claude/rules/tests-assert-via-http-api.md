---
paths:
  - "tests/JunimoServer.Tests/**/*.cs"
---

# E2E tests assert via the server HTTP API snapshot, not mod events

E2E tests assert against the **server HTTP API snapshot** — `/cabins` (`CabinsResponse`: TotalCount/AssignedCount/AvailableCount + per-cabin OwnerId/OwnerName/IsAssigned), `/farmhands` (`FarmhandsResponse`: per-slot Id/Name/IsCustomized), `/players`, plus client-side `/status` and `/wait/*` — and the `WaitFor*` helpers on `ServerApiClient`.

Mod `Diagnostics.ModEventLog.Emit(...)` events (`cabin_*`, `auth_*`, …) are transported via `Console.Out` lines prefixed with `SDVD_EVENT` → `SimpleContainerLogStreamer` → `infrastructure.jsonl` on disk. They are **diagnostics only**. No test asserts on a mod-emitted event, and there is **no "wait for mod event X" API** — the event stream is not a test assertion surface.

**Caveat — the snapshot can't see a stuck claim.** `/cabins` `IsAssigned` requires `owner.isCustomized == true` and `AvailableCount = TotalCount − AssignedCount`, so a cabin stuck with `userID` set but `isCustomized == false` (the abandoned-claim bug) still counts as **available** — indistinguishable from a healthy empty slot. Proving such a heal needs a **functional** gate (a real claim attempt succeeds, or exhaust all other slots to force a joiner onto the stuck one), not a snapshot count.

**How to apply:** Gate E2E assertions on `ServerApi.GetCabins/GetFarmhands/GetPlayers` + the `WaitFor*` helpers, never on an observed mod event (a plan once proposed asserting `cabin_claim_abandoned` — not achievable). When the snapshot can't distinguish the state you care about, design a functional probe instead.
