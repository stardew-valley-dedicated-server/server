---
paths:
  - ".github/workflows/**"
  - "docker-compose.yml"
  - "mod/JunimoServer/Env.cs"
  - "mod/JunimoServer/Services/Settings/**"
---

# Deployment configuration is an env var — never a shipped `server-settings.json`

A per-deployment knob (a reset schedule, a warning lead time) is an env var written into the generated `.env` from a GitHub Environment variable (`vars.*`; secrets only for secrets), never a committed per-environment `server-settings.json` and never a value the deploy copies into that file.

**Why:** `server-settings.json` is server-owned runtime state, not a deploy artifact: the mod writes world-coupled values back into it (`ServerSettingsLoader.ApplyNewGameConfig` on every new game, `SetCabinStrategy` on a migration commit) precisely because `SyncFromSettings` re-applies the file on every load. Overwriting it on a deploy that keeps the world re-applies stale values at the next load — a hide-direction cabin-strategy switch applies silently, a materializing one is rejected at load, and `CabinLayoutNearby` changes which layout every cabin placement resolves. A "copy only if missing" variant just moves the drift to the VPS, and a dedicated enable flag is a special case where the cron string itself is the configuration.

**How to apply:** When a feature needs a per-deployment value, read it in the mod from env (the `VerboseLogging` precedent: env overrides the file), pass it through `docker-compose.yml` as `"${NAME:-}"`, document it in `.env.example` and `docs/admins/configuration/environment.md`, and have `deploy-server.yml` write it from `vars.NAME`. Name operator-facing knobs feature-first and unprefixed (`WORLD_RESET_CRON`, like `API_*`, `SERVER_*`); the `SDVD_` prefix is reserved for test-harness and kill-switch knobs (`SDVD_ENV`, `SDVD_TPS_AGNOSTIC_PACING`, everything in `.env.test.example`).
