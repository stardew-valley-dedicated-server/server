# Plan: Make `ConnectionHelper` test-runner log wording/format consistent

## Goal

Normalize the `[ConnectionHelper] …` Trace lines emitted from
`tests/JunimoServer.Tests/Helpers/ConnectionHelper.cs` so every line follows
one shape and one wording convention. Pure log-string cleanup — no behavioral,
control-flow, or diagnostic-event (`EmitDiagnostic`) changes.

## Background — how these lines are produced

- A single private `Log(string)` (`ConnectionHelper.cs:178`) prepends the
  `[ConnectionHelper] ` prefix and emits a `Trace`-level `TestAnnotation` via
  `SetupEventBus`. So the prefix is already uniform; the inconsistency is purely
  in the **message** each call passes.
- The bulk of call sites already carry a second `[Phase]` sub-tag
  (`[Connect]`, `[Join]`, `[Auth]`, `[Retry]`, `[Checkpoint]`). These sub-tags
  exist **only** in this file (verified: grep of `[Connect]/[Join]/[Auth]/[Retry]/[Checkpoint]`
  across `**/*.cs` hits only `ConnectionHelper.cs` plus unrelated tags in
  `PasswordProtectionService.cs` / `FarmhandSenderService.cs`). They are a local
  convention, free to normalize.

## Decision (confirmed with user)

**Tag every line by phase.** The three success-summary lines that currently
carry no sub-tag get the matching phase tag so all 21 message-producing call
sites are `[ConnectionHelper] [Phase] …`.

## Full inventory of the 21 message call sites

(Line numbers are pre-edit, from the current file.)

| # | Line | Current message (after `[ConnectionHelper] `) | Issue |
|---|------|-----------------------------------------------|-------|
| 1 | 208 | `[Checkpoint] Failed to capture screenshot '{label}': {ex.Message}` | ok |
| 2 | 244 | `[Connect] Returning to title for retry...` | ok |
| 3 | 289 | `[Connect] Attempt {attempt}/{maxAttempts} - connecting via invite code...` | dash-as-clause-separator; trailing `...` |
| 4 | 380 | `Connected ({n} slots, attempt {attempt}/{maxAttempts})` | **no sub-tag** |
| 5 | 421 | `[Connect] Returning to title for retry...` | ok (dup of #2) |
| 6 | 459 | `[Connect] Attempt {attempt}/{maxAttempts} - connecting via LAN to {fullAddress}...` | dash-as-clause-separator; trailing `...` |
| 7 | 499 | `Connected via LAN ({n} slots, attempt {attempt}/{maxAttempts})` | **no sub-tag** |
| 8 | 733 | `[Retry] Disconnect before retry failed: {ex.Message}` | sub-tag `[Retry]` vs phase `[Join]` (see §"Sub-tag set") |
| 9 | 833 | `[Join] GetState failed after world ready: {ex.Message}` | ok |
| 10 | 857 | `[Auth] Location after join: {state?.Location} (needsLogin={needsLogin})` | ok |
| 11 | 872 | `[Auth] Retrying !login (attempt {loginAttempt}/{m})...` | trailing `...` only (see note below — `loginAttempt` is NOT the connect/join counter) |
| 12 | 883 | `[Auth] Failed to send !login: {ex.Message}` | ok |
| 13 | 887 | `[Auth] Waiting for post-auth warp (location change to different cabin)...` | trailing `...` |
| 14 | 897 | `[Auth] Authenticated (confirmed via location change)` | ok |
| 15 | 902 | `[Auth] Auth not confirmed within {x}s` | redundant word "Auth" after `[Auth]` tag |
| 16 | 917 | `[Auth] Server player check returned null, server may be unhealthy` | ok |
| 17 | 925 | `[Auth] Server player check failed, game thread unresponsive: {ex.Message}` | ok |
| 18 | 993 | `Joined world as '{farmerName}' (uid={uid}){authStatus} (attempt {attempt}/{maxAttempts})` | **no sub-tag** |
| 19 | 1063 | `[Join] Server bounced back to farmhand selection (attempt {bounce}/{m}), re-selecting slot...` | trailing `...`; word "attempt" used for the `bounce` counter |
| 20 | 1069 | `[Join] Bounce attempt {bounce}/{m}, slot={i}, elapsed={ms}ms` | ok |
| 21 | 1084 | `[Join] Select slot {i} returned success={s}, error={e}` | ok |
| 22 | 1260 | `[Join] Re-picked slot {i} (was {prev}) after bounce-back` | ok |

(22 rows; #5 is a textual duplicate of #2, so 21 distinct strings.)

### Three distinct counters — do NOT cross-normalize their notation

A pre-publish review caught that the word "attempt" appears against **three
unrelated loop counters**, and an earlier draft wrongly treated them as one
quantity rendered inconsistently. They are independent and each renders
consistently *within itself*; there is no cross-counter notation defect to fix:

- `{attempt}` / `{maxAttempts}` — outer connect/join retry loop (#3, #4, #6, #7, #18).
- `{loginAttempt}` / `{AuthLoginMaxAttempts}` — the `!login` retry loop (#11, line 873). **Not** the connect attempt.
- `{bounce}` / `{maxBounceRetries}` — the farmhand-slot bounce-back loop (#19, #20).

The only real wording defect *among* these is in #19: line 1064 calls the
`bounce` counter an "**attempt**" (`(attempt {bounce}/…)`) while line 1070
correctly calls the same counter a "**Bounce attempt**". That's two words for one
counter, one iteration apart — fixed by edit #11 below. No other "attempt
notation" change is made.

## Conventions to standardize on

Derived from what the majority of lines already do, so the diff stays minimal:

1. **Every line carries a `[Phase]` sub-tag.** Phase = the lifecycle stage the
   call sits in.
2. **Counter notation is left as-is — there was no real inconsistency.** Each of
   the three counters (`attempt`, `loginAttempt`, `bounce`; see table note above)
   already renders consistently within itself. The only exception is the #19
   word-choice fix (`attempt {bounce}` → `bounce {bounce}`), which is a *wording*
   fix, not a notation change. The existing casing — sentence-initial
   `[Connect] Attempt 1/2 …` vs mid-sentence `(attempt 1/2)` — is correct English
   and stays untouched.
3. **No trailing `...`** on completed-action or in-progress status lines. The
   ellipsis is noise in a Trace log (it reads as "in progress" but these lines
   fire once and are not paired with a "done" line). Remove from #3, #6, #11,
   #13, #19. (Keep nothing relying on `...`.)
4. **Separator after attempt-count is a hyphen with spaces** only where a clause
   follows — but replace the ` - ` dash-clause join in #3/#6 with a colon to
   match the dominant `label: detail` style used everywhere else in the file
   (`Select farmhand slot: …`, `Wait for farmhands: …`). → `Attempt {n}/{m}: connecting via …`.
5. **Don't repeat the tag word in the message.** #15 `[Auth] Auth not confirmed`
   → `[Auth] Not confirmed …`.

### Sub-tag set

Resolve the `[Connect]` / `[Join]` / `[Auth]` / `[Retry]` / `[Checkpoint]`
sprawl to the four real lifecycle phases plus checkpoint:

- `[Connect]` — coop/LAN connect through farmhand-slot load (#2, #3, #4, #5, #6, #7).
- `[Join]` — slot selection, character creation, world-ready, visibility (#9, #18, #19, #20, #21, #22).
- `[Auth]` — auto-login flow (#10–#17).
- `[Checkpoint]` — screenshot capture failure (#1).
- **`[Retry]` (#8) → fold into `[Join]`.** It fires inside `JoinWorldCoreAsync`'s
  retry-cleanup; it's a join-phase event. One-off `[Retry]` tag for a single line
  is the kind of inconsistency this pass removes. New: `[Join] Disconnect before retry failed: {msg}`.

## Concrete edits

Each is a one-line string change inside an existing `Log(...)` call. No method
signatures, no control flow.

1. **#3 (line 289)**
   `[Connect] Attempt {attempt}/{maxAttempts} - connecting via invite code...`
   → `[Connect] Attempt {attempt}/{maxAttempts}: connecting via invite code`
2. **#6 (line 459)**
   `[Connect] Attempt {attempt}/{maxAttempts} - connecting via LAN to {fullAddress}...`
   → `[Connect] Attempt {attempt}/{maxAttempts}: connecting via LAN to {fullAddress}`
3. **#4 (line 380)** add tag:
   `Connected ({n} slots, attempt {attempt}/{maxAttempts})`
   → `[Connect] Connected ({n} slots, attempt {attempt}/{maxAttempts})`
4. **#7 (line 499)** add tag:
   `Connected via LAN ({n} slots, attempt {attempt}/{maxAttempts})`
   → `[Connect] Connected via LAN ({n} slots, attempt {attempt}/{maxAttempts})`
5. **#18 (line 993)** add tag:
   `Joined world as '{farmerName}' (uid={uid}){authStatus} (attempt {attempt}/{maxAttempts})`
   → `[Join] Joined world as '{farmerName}' (uid={uid}){authStatus} (attempt {attempt}/{maxAttempts})`
6. **#8 (line 733)** retag + nothing else:
   `[Retry] Disconnect before retry failed: {ex.Message}`
   → `[Join] Disconnect before retry failed: {ex.Message}`
7. **#11 (line 872)** drop trailing `...`:
   `[Auth] Retrying !login (attempt {n}/{m})...`
   → `[Auth] Retrying !login (attempt {n}/{m})`
8. **#13 (line 887)** drop trailing `...`:
   `[Auth] Waiting for post-auth warp (location change to different cabin)...`
   → `[Auth] Waiting for post-auth warp (location change to different cabin)`
9. **#15 (line 902)** de-duplicate tag word:
   `[Auth] Auth not confirmed within {x}s`
   → `[Auth] Not confirmed within {x}s`
10. **#19 (line 1063)** drop trailing `...` **and** fix `attempt`→`bounce` word
    (the `bounce` counter is mislabeled "attempt" here while line 1070 calls it
    "Bounce attempt"):
    `[Join] Server bounced back to farmhand selection (attempt {bounce}/{maxBounceRetries}), re-selecting slot...`
    → `[Join] Server bounced back to farmhand selection (bounce {bounce}/{maxBounceRetries}), re-selecting slot`

**Total: 10 `Log()` call sites edited** (11 distinct string mutations — edit #10
carries both the `...` drop and the `attempt`→`bounce` rename). Untouched because
they already conform: #1, #2/#5, #9, #10, #12, #14, #16, #17, #20, #21, #22.

> Note: the `attempt`→`bounce` rename folded into edit #10 was added after the
> adversarial review — an earlier draft missed it while over-focusing on a
> non-existent cross-counter "attempt notation" defect. It is the only *new*
> change the review produced; everything else the review touched was rationale,
> not edits.

## Out of scope (explicitly not touching)

- `EmitDiagnostic(...)` event names/payloads (`join_attempt_*`, `join_bounce`,
  `select_returned`, etc.) — these are the machine-parsed diagnostic surface, a
  different vocabulary from the human Trace lines; leave them per
  `prefer-live-stream-over-disk-artifact` separation. Do **not** try to align the
  Trace wording to the event names.
- The `[ConnectionHelper]` prefix itself and the `Log` method.
- Any logging in the other 8 files that reference `ConnectionHelper` (those are
  call sites / type references, not log producers in this class).
- Control flow, retry counts, timeouts, attempt math.

## Verification (post-edit, no build needed for string-only changes, but do build)

1. `dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` — confirms
   no interpolation typo broke a format string.
2. Re-grep `Log\(` in the file and eyeball that all 21 messages now start with a
   `[Phase]` tag and none end in `...`.
3. **Runtime check — this is the gate that actually proves the goal, not an
   optional extra.** The goal is consistent *human-readable appearance*; a green
   build only proves the format strings compile, it cannot prove the lines read
   consistently. Run one connect test (`make test FILTER=<a LAN connect test>`)
   and read the resulting `infrastructure.jsonl` / annotation stream to confirm
   the lines render as intended (e.g. `[ConnectionHelper] [Connect] Connected via
   LAN (1 slots, attempt 1/2)` and `[ConnectionHelper] [Join] Joined world as
   '…'`). As of this plan the wording has **not** been observed in a real run —
   only the static edits are specified; treat step 3 as a required post-condition,
   not a nicety.

## Risk

Near-zero. All edits are within string literals already inside `Log(...)` calls;
no identifier is renamed, no consumer parses these Trace strings (they are
diagnostics-only per `tests-assert-via-http-api`). The one thing to not get wrong
is interpolation-token preservation (`{attempt}`, `{ex.Message}`, etc.) — each
edit above keeps every `{…}` token intact.
