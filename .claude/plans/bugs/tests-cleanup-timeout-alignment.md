# `FarmerDeleteTimeout` is conflated across 3 semantics; disconnect-wait inherits the wrong default; stale "15s/16s" comments

Status: open. Adversarially validated against current code AND the 2026-06-28 measured cost model. The first-pass "503-storm" rationale is **withdrawn** — measured at 0.1s, not the theorized ~37s — so this plan is now a *conflation + naming* cleanup, not a timeout-sizing fix.

## What measured data changed vs the first-pass review

The E2E cost model (run 2026-06-28) measured the relevant primitives directly:

- **DELETE-503-storm: refuted, ~0.1s** (not the theorized 5×5s+4×3s = 37s). The game thread is not actually blocked through five delete attempts in practice. So there is **no** "delete deadline can't cover a 503-storm" problem, and **no** request-cap or constant-bump is warranted for it.
- **Customization-sync `/wait/farmhands` p90 = 20s.** This is `WaitForFarmhandByNameAsync` waiting for a fresh join's slot to appear/customize. It confirms that helper legitimately needs a ~35s budget and must **not** drop to a 2s disappear-poll budget.

Per `runtime-post-conditions-are-gates.md`, measured runtime outranks arithmetic. The surviving issues are real but are about **naming/semantics**, not sizing — no timeout value changes anywhere in this plan.

## Root issue — one constant, three unrelated jobs

`TestTimings.FarmerDeleteTimeout` (35s) is borrowed by call sites with three distinct meanings:

| Semantic | What it waits for | Sites | Correct home |
|---|---|---|---|
| **disappear-poll** | a disconnected farmer to vanish from the `/players` snapshot (<50ms) | `WaitForPlayersRemovedByNameAsync` default (`ServerApiClient.cs:2417`), `WaitForPlayersRemovedByIdAsync` default (`:2474`), `TestLifecycle.cs:603` (omits arg → inherits default) | `FarmerRemovalBudget` (2s) |
| **appear/customize** | a fresh join's farmhand slot to appear & customize (p90 **20s** measured) | `WaitForFarmhandByNameAsync` default (`:2533`) — **16 callers** across 8 files | `CabinAssignmentTimeout` (35s) |
| **delete-retry** | a DELETE to succeed (retry until `Success`) | `WaitForFarmhandDeletedByNameAsync` default (`:2581`), `TestLifecycle.cs:738` delete-loop deadline | `FarmerDeleteTimeout` (35s) — keep; now matches its name |

Plus two test-level borrowers that pass `FarmerDeleteTimeout` explicitly for ad-hoc waits:
- `ServerApiTests.cs:38` — waits for a *previous test's* cleanup → `/players` count 0 (cross-test lag).
- `FarmhandManagementTests.cs:109` — polls `/farmhands` for a just-deleted name to be gone (own delete + a tick).

The conflation is why the constant's name is misleading: it's read as "delete timeout" but governs *appear* and *disappear* polls too. The clean, idiomatic fix routes each site to a **semantically-correct home that already exists** — no new constants, no value changes.

## Finding 1 — disconnect-wait inherits 35s instead of the 2s its siblings use

`RunCleanupCoreAsync` calls `WaitForPlayersRemovedByIdAsync(myUids, ct: ct)` at
`TestLifecycle.cs:603` with **no `timeout`**, inheriting the 35s default. The three other
disconnect-wait sites all pass the 2s budget explicitly:
`PersistentSession.cs:202-205`, `PersistentSessionCoordinator.cs:346-349`,
`FarmerTestHelper.cs:291` (via `WaitForPlayerRemovedByIdAsync`). The poll hits a
read-only `/players` snapshot (<50ms), so 2s is ~40× the happy path; 35s is dead headroom.

This path does **not** poison on timeout — it returns `false` and logs a warning
(`TestLifecycle.cs:607-612`), so the impact is hygiene/consistency, not a wrong outcome.
Fixed for free by the disappear-poll default change below (the default *becomes* 2s), but
the call site should still pass the budget explicitly to match its siblings and read clearly.

## Finding 2 — WITHDRAWN

First-pass claimed the delete-loop's 35s deadline couldn't cover a ~37s all-503 cycle and
poisoned the server. Measured cost is **0.1s** (cost model 2026-06-28); a five-attempt 503
storm does not occur in practice. No request cap, no constant bump. (The underlying
*conflation* finding survives in the root issue above — only the storm rationale is dropped,
per `adversarial-review-split-findings.md`: weak framing withdrawn, valid finding kept.)

## Finding 3 — three stale comments cite a "15s" game-thread timeout / "~16s" 503

The real `RunOnGameThreadAsync` timeout is **5s** (`mod/JunimoServer/Services/Api/ApiService.cs:1746`); a 503 means a ~5s game-thread block, and the measured delete cost is ~0.1s. Verified verbatim:

- `TestTimings.cs:91-94` (`FarmerDeleteTimeout`): *"Must exceed the server's RunOnGameThreadAsync timeout (15s) so that a single 503 (which takes ~16s wall-clock) doesn't exhaust the entire budget. 35s allows one 503 + one successful call with headroom."*
- `TestTimings.cs:203-205` (`CabinAssignmentTimeout`): *"Must exceed the server's RunOnGameThreadAsync timeout (15s) ... A single 503 takes ~16s wall-clock; 35s allows one 503 + one successful call."* — also wrong domain: this guards a customization-sync wait (p90 20s), not a 503 path.
- `TestTimings.cs:227` (`CleanupTimeout`): *"Must accommodate at least one 503 retry (~16s) per cleanup phase."*

## Fix

No timeout **value** changes. Repoint each conflated default to its correct existing home,
fix the one disconnect-wait call site, and correct the comments. Grouped by file.

### A. `tests/JunimoServer.Tests/Clients/ServerApiClient.cs` — repoint 3 helper defaults

1. **`WaitForPlayersRemovedByNameAsync`** (`:2417`): default `timeout ?? FarmerDeleteTimeout` → `timeout ?? FarmerRemovalBudget`.
2. **`WaitForPlayersRemovedByIdAsync`** (`:2474`): same change → `timeout ?? FarmerRemovalBudget`.
3. **`WaitForFarmhandByNameAsync`** (`:2533`): default `timeout ?? FarmerDeleteTimeout` → `timeout ?? CabinAssignmentTimeout` (the customization-sync home; p90 20s measured, 35s budget).

Leave **`WaitForFarmhandDeletedByNameAsync`** (`:2581`) on `FarmerDeleteTimeout` — it is a
genuine delete-retry, the constant's now-sole meaning.

### B. `tests/JunimoServer.Tests/Infrastructure/Fixture/TestLifecycle.cs` (~line 603)

Pass the disappear-poll budget explicitly (matches siblings; also independent of A1/A2):

```csharp
var ok = await _testBase.ServerApi.WaitForPlayersRemovedByIdAsync(
    myUids,
    timeout: TestTimings.FarmerRemovalBudget,   // 2s — match PersistentSession path
    ct: ct
);
```

Leave the delete-loop deadline at `TestLifecycle.cs:738` (`FarmerDeleteTimeout`) — correct, no change.

### C. Two test-level borrowers — pass an explicit, intent-revealing budget

- **`ServerApiTests.cs:38`** (`GetPlayers_WhenNoPlayersConnected`, waits for `/players`==0
  while a *prior* test's cleanup drains): keep a generous budget but stop borrowing the
  delete constant — pass `TestTimings.ServerReadyBetweenTests` (30s), which is the
  established "wait for prior-test teardown to settle" budget. (Verify this holds; if a
  flake appears, fall back to an explicit 35s.)
- **`FarmhandManagementTests.cs:109`** (polls `/farmhands` for own just-deleted name to
  disappear): a delete the test already confirmed via `WaitForFarmhandDeletedByNameAsync`
  at `:87`, then one snapshot tick — pass `TestTimings.FarmerRemovalBudget` (2s) to match
  the disappear-poll semantics, OR keep `FarmerDeleteTimeout` if 2s proves tight. Decide
  from the verification run, not by assumption.

### D. `tests/JunimoServer.Tests/Helpers/TestTimings.cs` — comment corrections

- **`FarmerDeleteTimeout` (`:87-95`)**: rewrite to state it is the **delete-retry**
  budget only; real game-thread timeout is 5s and measured delete cost ~0.1s, so 35s is
  ample retry headroom (not "one 503 + one successful call"). Drop "(15s)" / "~16s".
- **`CabinAssignmentTimeout` (`:201-206`)**: rewrite to the real driver — customization/
  `isCustomized` sync after a fresh join lags (p90 **20s** measured); 35s gives ~1.75×
  headroom. Note it now also backs `WaitForFarmhandByNameAsync`. Drop the 503 sentence.
- **`CleanupTimeout` (`:223-228`)**: replace "one 503 retry (~16s)" with the real serial
  budget — bounded disconnect wait (2s) + farmer-delete loop + the 10s diagnostic check.

## Verification

1. `dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` — compile (TimeSpan/comment + default-arg edits).
2. `grep -n "15s\|~16s\|timeout (15" tests/JunimoServer.Tests/Helpers/TestTimings.cs` → **zero** hits.
3. `grep -n "FarmerDeleteTimeout" tests/` → only `WaitForFarmhandDeletedByNameAsync` (`:2581`), the `TestLifecycle` delete loop (`:738`), and the constant itself remain. No disappear/appear poll references it.
4. Run the affected classes — `make test FILTER=FarmhandManagementTests` and
   `make test FILTER=ServerApiTests` — and confirm green. Then read `infrastructure.jsonl`:
   the `poll_completed` for `Polling_ServerApi_WaitForPlayersRemovedById` shows
   `timeoutMs: 2000`, and `Polling_ServerApi_WaitForFarmhandByName` shows `timeoutMs: 35000`.
   (Per `passing-test-isnt-proof-the-scenario-ran.md`: verify the budgets in the artifact,
   not just the green check.)
5. Spot-check the two test-level borrowers (step C) didn't regress — if `ServerApiTests`
   or `FarmhandManagementTests:109` flake on the tightened budget, widen that one site and
   note it.

## Scope (explicitly out)

- **No timeout value changes** anywhere — pure repointing + comments. `CabinAssignmentTimeout`,
  `FarmerDeleteTimeout`, `FarmerRemovalBudget`, `CleanupTimeout` all keep their current values.
- **No new constants** — every semantic already has a correct home (`FarmerRemovalBudget`,
  `CabinAssignmentTimeout`, `FarmerDeleteTimeout`).
- **Not renaming `CabinAssignmentTimeout`** despite it now backing non-cabin farmhand waits.
  A rename touches 30+ call sites and is separable churn; if desired, do it as its own
  follow-up, not bundled here (`plan-discipline.md` — don't grow a signed-off scope silently).
- No change to DesyncKicker, long-poll, client-wait, recording, or WebSocket timeouts — all
  verified correctly ordered. No change to the server mod (`RunOnGameThreadAsync` 5s,
  `WaitMaxTimeout` 10s).
