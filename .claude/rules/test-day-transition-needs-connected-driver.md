---
paths:
  - "tests/**/*.cs"
---

# To E2E-drive a day transition, use one of the two supported drivers — never an empty server's clock

A test that needs a real day/season transition has exactly two drivers:

1. **A connected sleeper.** A farmhand sleeps → the host auto-sleeps → the group transitions. `SleepToSaveAsync` for the single-client case, or a `SecondFarmer` (`Farmers.ConnectSecondFarmerAsync`, `driver.Client.Actions.Sleep()`) when the scenario needs a *specific* farmer offline.
2. **The empty-server grace sleep.** With no authenticated player connected, `SetTime(TestTimings.PrePassOutTime)` (2550) pauses the server past 1:00 AM and, after `AUTO_SLEEP_GRACE_SECONDS` (`5` in the test harness, `ServerContainer`), the host sleeps on its own. `DayChange.WaitAsync` then observes the new day. A lobby-only server (unauthenticated players) takes this path too.

What is **not** a driver: the empty server's clock. `SetTime`/`SetClockSpeed` with nobody connected never reach the 2:00 AM pass-out — the world is paused, and even unpaused the lone-host `shouldTimePass` tail advanced only intermittently (`host-automation.md` invariant 11).

**Why:** The two `CropSaverTests` `WhileOwnerOffline` methods disconnected the crop owner and relied on `SetTime(2550)` + `SetClockSpeed(20)` while the old auto-pause still unpaused an empty server past 2500. The transition froze intermittently on the lone-host clock gate — the ~35% "Day did not advance" flake. Fixed with a `SecondFarmer` driver. The grace sleep later made the empty-server case deterministic, and `LobbyHomedSpouseTests`/`PasswordProtectionTests` drive server-only nights through it.

**How to apply:** When a test advances a day via `DayChange.WaitAsync`, name which driver it uses. A connected sleeper when the test has a client; the grace sleep when the scenario is "server alone" — and then set time past 2500 first, since the grace timer only runs there. If you see `SetClockSpeed` near a `DayChange.WaitAsync` with no connected sleeper and no time past 2500, that's the flake shape.
