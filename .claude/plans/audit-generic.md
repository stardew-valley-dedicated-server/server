# Outstanding Gameplay & Stability Issues

Open bugs, server-side risks, and player-impacting logic problems in `mod/JunimoServer/`.

---

## 1. Performance problems

### P1. `EndOfDayTimeOut` is effectively never triggered

- **File:** `Services/AlwaysOnServer/AlwaysOn.cs:273` with config in `AlwaysOnConfig.cs:23`
- **Why:** `Config.EndOfDayTimeOut` defaults to `120000`; `Env.ServerTps` defaults to `60`. Multiplied: `120000 × 60 = 7,200,000` ticks ≈ 33 hours. The shipping-menu timeout never fires. (Festival timeouts in `AlwaysOnFestivals.cs` were converted to use `TicksToSeconds(Config.X)` which gives sane values; only the EndOfDay/shipping path is still broken.)
- **Impact:** Shipping menu timeout is non-functional. Server can get stuck indefinitely with a dangling ShippingMenu.
- **Fix:** Drop the `* Env.ServerTps` multiplier, or change the config value to represent ticks directly. Also revisit the default — 2000 seconds (33 min) would be a sane shipping-menu timeout.

### P2. `FarmCabinPositions.GetDesignatedPositions` scans entire map layer every call

- **File:** `Services/CabinManager/FarmCabinPositions.cs:20-57`
- **Why:** Nested for-loop over all tiles. Map is static within a session.
- **Fix:** Cache the result after first computation.

### P3. `CropWatcher._previousHasCrop` dictionary grows forever

- **File:** `Services/CropSaver/CropWatcher.cs:15`
- **Why:** Entries added (`:80,:87`) but never removed. `OnCropRemoved` clears the CropSaver _data_ dict but leaves the watcher's `(location,tile)→bool` entry behind permanently. The all-location scan (`:39-64`) keys across every location, so it accumulates faster than a Farm-only scan would.
- **Fix:** Prune entries for tiles that no longer have terrain features.

---

## 2. Minor logic problems

### L1. `HandleCommunityCenterUnlock` adds duplicate `ccDoorUnlock` mail

- **File:** `Services/AlwaysOnServer/AlwaysOn.cs:481`
- **Why:** `Game1.MasterPlayer.mailReceived.Add("ccDoorUnlock")` without checking `Contains`.
- **Impact:** Duplicate entries in NetStringList (minor data bloat).

### L2. Spirit's Eve and Winter Feast auto-end 10 seconds after the main event spawns

- **File:** `Services/AlwaysOnServer/AlwaysOnFestivals.cs:609,703`
- **Why:** `if (ElapsedSeconds(goldenPumpkinStartTime) >= TicksToSeconds(10))` and `if (ElapsedSeconds(winterFeastStartTime) >= TicksToSeconds(10))` trigger end-festival dialogue. A separate 2-minute warning exists (line 596-601) but for the _outer_ `SpiritsEveTimeOut` reset, not this 10s post-spawn auto-end.
- **Impact:** Players get almost no time at these festivals before auto-end triggers.
- **Fix:** Increase the 10s window, or wire a player-facing warning to this specific timeout.

### L3. `HandleJojaMarket` money can go negative

- **File:** `Services/AlwaysOnServer/AlwaysOn.cs:757-811`
- **Why:** Multiple independent `if` blocks deduct money without checking cumulative balance.
- **Impact:** Host player money goes negative.

### L4. `ServerOptimizer.CreateLobby_Prefix` hardcodes Public lobby with 150 members

- **File:** `Services/ServerOptim/ServerOptimizerOverrides.cs:74-75`
- **Why:** `privacy = ServerPrivacy.Public; memberLimit = 150;` regardless of server settings.
- **Impact:** Even "private" servers have a public Galaxy lobby. Privacy violation.

---

## Verification plan

To verify fixes for the above issues:

1. **P1 (EndOfDayTimeOut):** Force a stuck ShippingMenu. Confirm the timeout fires within the configured wall-clock duration.
2. **L3 (Joja money):** Drive host money near a Joja threshold with another purchase pending. Confirm cumulative deductions never push the balance below zero.
3. **L4 (lobby privacy):** Start a private server. Confirm the Galaxy lobby is not Public.
