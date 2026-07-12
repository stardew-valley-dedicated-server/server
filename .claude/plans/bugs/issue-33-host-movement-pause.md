# Issue #33 — Inconsistent host movement & pause at startup

**Verdict:** 🟡 partial — half fixed, half intentional behavior.
**Action:** re-scope the ticket (close as won't-fix-as-reported, or lower the
pause floor to 6:00 if a true 6:00 pause is wanted).

## Findings

Both reported symptoms traced in
`mod/JunimoServer/Services/AlwaysOnServer/AlwaysOn.cs`:

- **Movement freeze — effectively fixed.** `OnSaveLoaded` calls
  `EnableAutoMode()` (`AlwaysOn.cs:216`, method at `:474`), which immediately
  calls `Game1.player.Halt()` (`:489`) and enables input suppression via
  already-installed Harmony prefixes — before the first update tick. No window
  remains for the host farmer to wander.
- **6:00-vs-6:10 pause — still present, but intentional.** The empty-server
  pause condition is `Game1.timeOfDay is >= 610 and <= 2500`
  (`AlwaysOn.cs:1012`), with a comment explaining the bounds: pause during
  normal hours, but unpause after 25:00 so the forced pass-out sequence at
  26:00 (2:00 AM) can proceed. The 6:10 lower bound means the first in-game 10
  minutes tick by before the pause engages.

## Task

1. Decide the re-scope (maintainer call):
   - **Close as won't-fix-as-reported** — movement is fixed, the 6:10 floor is
     deliberate; or
   - **Lower the floor to `600`** at `AlwaysOn.cs:764` if a true 6:00 pause is
     wanted. Before changing it, confirm a 6:00 pause doesn't interfere with
     anything that must run on the first time-tick of the day (the comment only
     justifies the *upper* bound; verify the 610 lower bound against the forced
     pass-out / day-start flow before assuming it's arbitrary).
2. Comment on #33 with the split verdict and `file:line`, then close or re-title
   to the remaining 6:00-pause decision.
