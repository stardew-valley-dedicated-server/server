---
paths:
  - "mod/JunimoServer/Services/GameManager/**"
  - "mod/JunimoServer/Services/Api/ApiService.cs"
  - "mod/JunimoServer.Shared/RenderingController.cs"
  - "tests/JunimoServer.Tests/Containers/ServerContainer.cs"
---

# Cold-start boot-band: measure where the run executed, and don't re-chase the known dead ends

**Measurement trap.** A test server's boot-band cost is dominated by **where the run executed and whether recording was on**, not by the Docker image: a remote VPS over SSH with recording (`SERVER_FPS=5`) inflates it several-fold over local headless `SERVER_FPS=0` (SSH transport + parallel-startup `StartLimiter` contention, not the image). Always check `host_id` and `SERVER_FPS`/recording before quoting a number; for image-resident cost, reproduce locally against `sdvd/server:local` + the real `server_game-data` volume.

**Every test server generates a new game.** Each test gets a fresh per-runId saves volume (`sdvd-test-saves-{runId}`, `ServerContainer.cs:217`) → `HasLoadableSave()` is always false → `CreateNewGameFromConfig()` (`GameManagerService`, ~216-219). Only the `!loadedGame`-only delta (NewDay/flush/GenerateBundles) is avoidable via a pre-baked save; the map-build floor runs on both the load and create paths.

**Already-verified dead ends — do NOT re-investigate as boot-band perf wins:**
- **Xvnc/X cannot be removed.** A live `GraphicsDevice` is needed even at `SERVER_FPS=0` (`RenderingController.ShouldGameDraw` does `GraphicsDevice.Clear`, `RenderingController.cs:108`; MapUtil/ApiService need it).
- **OpenAPI DLLs are load-bearing** — the spec is generated live at runtime (`ApiService.cs:1580`, `OpenApiGenerator.Generate`).
- **polybar + resize-handler boot in parallel** with `app` and don't gate startup.
- **`ntpdate` is absent**, so `init_time_sync` is a few ms, not a stall.
- A large fraction of the local boot band is **intrinsic Mono + SMAPI Cecil assembly-rewrite bootstrap** — not addressable in the Docker/rootfs layer.

**How to apply:** Attribute a latency number to host/recording before treating it as image cost. The dead ends above were each verified non-removable — don't re-open them as perf wins.
