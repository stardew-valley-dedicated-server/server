---
paths:
  - "tests/**/*.cs"
---

# To E2E-drive a day transition, keep a connected player to trigger it — don't rely on an empty server's clock

A test that needs a real day/season transition must have a *connected* player drive it (a farmhand sleeps → the host auto-sleeps → the group transitions), or use `SleepToSaveAsync` (which sleeps the primary client). An empty server (`otherFarmers.Count == 0`) deliberately does not advance its own clock — `SetTime`/`SetClockSpeed` with nobody connected freezes on the lone-host `shouldTimePass` gate (see `host-automation.md` invariant 11). If the behavior under test requires a *specific* farmer to be offline, keep a **second, unrelated** farmer connected as the transition driver rather than disconnecting everyone.

**Why:** The two `CropSaverTests` `WhileOwnerOffline` methods disconnected the crop owner and then relied on `SetTime(2550)` + `SetClockSpeed(20)` to advance the day with no one connected. The server won't advance an empty server's clock, so the transition froze intermittently — the ~35% "Day did not advance" flake. Fix: connect a `SecondFarmer` (`Farmers.ConnectSecondFarmerAsync`) as the driver, sleep it (`driver.Client.Actions.Sleep()`) so the host auto-sleeps, while the crop owner stays offline so the offline code path under test still fires. The crop's `ownerId` is stamped before the driver joins, so the driver never becomes the owner. This was a *test* bug, not a server bug — the initial instinct to make the empty server roll over on its own was a rejected mod change.

**How to apply:** When a test advances a day via `DayChange.WaitAsync`, ensure a connected player triggers the transition — `SleepToSaveAsync` for the single-client case, or a `SecondFarmer` driver when the scenario needs one farmer offline. Never disconnect all players and expect the day to roll over on the clock. If you see `SetClockSpeed`/`SetTime` near a `DayChange.WaitAsync` with no connected sleeper, that's the flake shape.
