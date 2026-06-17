# Cabin management — open work

Issue #64 (cabin position lost on reload) is fixed and merged (PR #349: `PlayerCabinPositions` intent map + `HasSavedPosition` exemption on both bulk movers, verified by `CabinPositionPersistenceTests`). Cabin footprint validation shipped separately (PR #403). This folder holds what remains, ordered by dependency and cost.

| # | Plan | Type | Blocked on |
|---|------|------|------------|
| 01 | [Cabin command guard tests](01-cabin-command-guard-tests.md) | Tests | nothing — runnable today |
| 02 | [Second-farmer test helper](02-second-farmer-helper.md) | Test infra | nothing |
| 03 | [Multi-client cabin tests](03-multi-client-cabin-tests.md) | Tests | 02 |
| 04 | [`!cabin reset` command](04-cabin-reset-command.md) | Feature | nothing |
| 05 | [Dummy cabin at the shared stack location](05-dummy-cabin-at-stack-location.md) | Feature | design questions inside |
| 06 | [`!cabin` preview mode](06-cabin-preview-mode.md) | Feature | investigation-first; lowest priority |

Shared context for all plans:

- Chat commands use the `!` prefix in-game (`!cabin`).
- E2E tests assert via the server HTTP API snapshot (`/cabins`, `/farmhands`), never mod events (`.claude/rules/tests-assert-via-http-api.md`). `/cabins` exposes `SavedPositionPlayerIds` (the intent map's keys), so intent writes are directly observable.
- Test primitives that exist today: warp + footprint clear (`CabinPlacementHelper.WarpAndClearFootprintAsync`), save flush (`SleepToSaveAsync`), in-process world reload (`ReloadServerAsync` → `POST /reload`), in-container strategy switch (`SwitchCabinStrategyAsync` in `CabinPositionPersistenceTests`).
- Missing primitive: connecting a second concurrent farmer (plan 02).
