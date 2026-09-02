---
paths:
  - "mod/JunimoServer/**"
---

# Nothing shared may be pumped from an `ApiService` event handler

`ApiService.Entry` returns before subscribing any game-loop event when `API_ENABLED=false`. Infrastructure that other services depend on — a game-thread dispatcher, a scheduler, a periodic snapshot — must own its own `UpdateTicked` subscription (be its own `ModService`), never ride on `ApiService.OnUpdateTicked`.

**Why:** Extracting `ApiService`'s game-thread action queue into a shared dispatcher, with the drain left in `ApiService.OnUpdateTicked`, would have hung every scheduler marshal forever on a server with the API disabled — a supported configuration. The design read as a mechanical extraction and the early return is the second line of `Entry`; it was caught only on a third review pass. `/health` already avoids the same trap by reading a tick timestamp written from `UnvalidatedUpdateTicked`, independent of the action queue.

**How to apply:** Before making any other service depend on something `ApiService` currently does per tick, check whether it lives below the `Env.ApiEnabled` early return in `Entry`. If it does, move it into a service whose `Entry` always subscribes and let `ApiService` subscribe to that service's events instead.
