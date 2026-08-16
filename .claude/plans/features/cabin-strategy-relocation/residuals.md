# Cabin-strategy relocation — remaining residuals

Exhaustive list of every remaining issue, residual, or skipped item on this branch as of the
post-review state (full suite green: 180 passed / 0 failed / 6 skipped). Delete with the other
plan files at merge; carry over anything still open.

1. **The CabinStack branch lacks the corruption tripwire.** If a save ever has a one-way
   farmhand↔cabin link (farmhand points at a cabin that doesn't point back), the
   FarmhouseStack join path in `OnLocationIntroductionMessage` logs a warning; the CabinStack
   path silently does nothing and the player joins with their cabin still hidden. One-line
   fix, not yet made.

2. **The FarmhouseStack warning's text is misleading.** It says "cabin ownership may not be
   linked yet", implying a timing race that provably cannot happen — vanilla
   `GameServer.checkFarmhandRequest` rejects the join before any location message is built
   unless the farmhand exists and has a cabin home (`TryAssignFarmhandHome`), all in one
   synchronous game-thread call. The warning should say what it actually means: inconsistent
   save data. Not yet reworded.

3. **The warp-convergence window still exists — it's guaranteed to close, not gone.** For up
   to ~250 ms in production (3 s at test `CLIENT_TPS=5`; `InterpolationWait`, 15 client
   ticks) after a join or a cabin move, the client's exit warp points at the old target. A
   real player can't reach the door that fast, so this is accepted, not fixed. If clients
   ever run below 5 TPS the window grows (fixed 15 ticks; at 2 TPS it's 7.5 s against the
   test's 10 s `NetworkSyncTimeout` wait — thin margin).

4. **The rejoin production bug is fixed by mechanism but has no dedicated test.** No E2E
   disconnects and reconnects a FarmhouseStack player and walks out the door. The
   `SetWarpsToFarm` force-delta fix for the equal-value/no-delta rejoin case is verified by
   code-reading plus the general suite, not by a test of the exact scenario it fixes.

5. **The handoff's prescribed A/B experiment was never run.** The deleted delta interceptor
   (`OnLocationDeltaMessage`) was concluded uninvolved via code-reading (name mismatch makes
   it dead) plus the fix passing — it was never restored and re-run to empirically prove the
   deletion changed nothing. Evidence considered conclusive; experiment itself skipped.

6. **The test's step-1 gate is "any on-map target", not "the farmhouse door tile".**
   `FarmhouseStack_ExitWarp_RepointsAfterMoveOut` waits until the exit warp target is
   non-negative, then walks. It can't distinguish the farmhouse door from some other on-map
   tile; exact-tile correctness rides on the later assertions. Slightly weaker than it could
   be.

7. **Reconnect-only convergence for ghost cabins and dummy interiors remains** (deferred
   ledger items 22/14). A connected player still doesn't see a stack-spot change or a
   post-migration-commit world until they reconnect. `MarkDirty` being public on every net
   field (`AbstractNetSerializable`) means a live heal is now plausibly buildable — recorded
   in the ledger, not implemented.

8. **`settings validate` logs `[FAIL]` at `LogLevel.Error`** — latent server-side test
   poison (`debugging.md`) if a test ever validates a bad config. Pre-existing shape,
   acknowledged as known.

9. **`cabin_owner_changed` diagnostics are noisy by design.** The event is a tile-keyed 1 Hz
   snapshot diff, and all hidden cabins share one tile, so join-time "owner shuffle" events
   are artifacts. Documented as don't-chase (`cabin-system.md` invariant 10), not fixed.

10. **One benign infrastructure timeout in the final full run.**
    `FestivalTests.SpiritsEve_DoesNotAutoEndImmediately` hit a
    `WaitForPlayersRemovedByIdAsync` timeout during cleanup (test itself passed). Unrelated
    to cabins, observed once, not investigated.

11. **The 6 skipped tests were not verified against the baseline's skip set.** The final full
    run was 180 passed / 0 failed / 6 skipped; the pre-review baseline record only says
    "180 passed / 0 failed". Almost certainly the usual Steam-gated skips, but unverified.

12. **Historical flake-tracker entries for seven other tests remain unexplained
    individually** (StackSpot_SetViaConsole, GalaxyReloginGate, NewGame_NoneStrategy,
    SameSweep, DeleteFarmhand_WhenOffline, StagedMigration_RecordSurvives, DefaultSettings
    StartingCabins). All passed in the final runs, and most recorded failures are probably
    the stop-on-fail cascade from the original failing run — but none were root-caused.

13. **The new `location_warps` endpoint is not in the test-client's API definitions file**
    (`ApiDefinitions.cs`). Matches the precedent of `farm_buildings`, which is also absent —
    the docs file is consistently incomplete rather than newly inconsistent.

14. **The adversarial reviewer's "verified non-issues" list is unrecoverable in-session.** It
    lives only in a pre-clear transcript message (user scrollback); it was never persisted to
    a file.

15. **Everything is uncommitted.** The entire branch — feature work, 15 review fixes, and the
    warp-delta fix — is working-tree state pending review. One accidental `git checkout .`
    loses it all; a WIP commit is advisable before further iteration.
