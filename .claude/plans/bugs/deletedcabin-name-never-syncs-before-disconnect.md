# DeletedCabin_DoesNotPoisonSubsequentJoins flakes when the farmhand name never syncs before disconnect

Status: root-caused from run `2026-07-16T07-04-54Z_ecdbb05` (worktree steam-single-account-deadlock, full-suite run under heavy host contention — queueDurationTotal 54751s). Not yet fixed. First recorded failure of this test (ledger history is all passed/canceled since 2026-05-28).

## Symptom
`PasswordProtectionTests.DeletedCabin_DoesNotPoisonSubsequentJoins` failed `Delete should succeed: Farmhand 'Farmer62' not found` — `WaitForFarmhandDeletedByNameAsync` looped on "not found" for the whole 35s `FarmerDeleteTimeout` (`failure_context: WaitForFarmhandDeletedByNameAsync_timeout, lastResultSuccess=false`).

## Root cause — delete-by-name races the character-data sync; disconnect makes the loss permanent
- Log-verified: "Farmer62" appears ONLY in `containers/client-5/container.log` (`Set character data - Name: Farmer62` at 07:17:17, `[Spawn:AfterWarp] Player Name: Farmer62` at 07:17:18) and in NO server log. Sibling farmers of the same class (Farmer60/61/63) all appear in `containers/server-1/container.log` — server logs do record farmhand names when they sync.
- The test flow is `ConnectNewAsync(assertAuthenticated:true)` → `DisconnectAndWaitForSlotAsync` → `WaitForFarmhandDeletedByNameAsync(name)`. Auth/warp completing does not guarantee the farmhand's character data (name) has replicated into the server's farmhand entry; under load that sync lags (the customization-sync p90 is ~20s). Disconnecting right after auth can cut the sync off entirely — the server-side farmhand entry then stays unnamed forever, so delete-by-name can never succeed, regardless of retry budget.
- Same family as the second `failure_context` in the run: `SaveImportTests.Import_ForceReload_KicksThenFinalizes` hit `WaitForFarmhandByNameAsync_timeout` (`requireCustomized:true`, Farmer66) before being canceled.

## Fix sketch
Before disconnecting, gate on the server actually knowing the farmhand: `ServerApi.WaitForFarmhandByNameAsync(name, requireCustomized: true)` (35s `CabinAssignmentTimeout` home per the cleanup-timeout-alignment plan) inserted between `ConnectNewAsync` and `DisconnectAndWaitForSlotAsync` — either in this test or inside `Farmers.ConnectNewAsync` when `assertAuthenticated` is set (check other DisconnectAndWait-right-after-connect call sites for the same race before choosing the shared home).

## Non-causes (checked)
- Not the steam-deadlock branch's diff: zero `server_poisoned` events in the run (R1/R3 paths never executed); zero "Cleanup timed out" occurrences (the cleanup-token rebind produced no behavior change); the failure is in the test body's own delete loop, upstream of any lease cleanup.
- Not an instance swap: `config-a50586faedce` had exactly one instance for the whole run.
