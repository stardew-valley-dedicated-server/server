# Fix: `AnotherPlayerInFootprint_RejectsAndDoesNotMove` flake — resend `!cabin` per poll + self-identifying reply assert

## Symptom

```
JunimoServer.Tests.CabinPlacementValidationTests.AnotherPlayerInFootprint_RejectsAndDoesNotMove:
Expected an 'another player is standing there' rejection - No trace available
```

The assert at `CabinPlacementValidationTests.cs:212` fires when the keyword
`"another player is standing there"` never appears in chat. `"No trace available"` is the
reporting layer noting no artifact was captured for the run — not part of the assertion.

## Why it can fail (two indistinguishable scenarios)

The keyword can be absent for two different reasons, and the current bare `Assert.True(rejected, "Expected …")` can't tell them apart:

1. **Move accepted** — the validator's farmer-collision loop didn't see farmer B at the
   footprint tile when `!cabin` ran, so the move succeeded and *no* `"Can't move cabin"` reply
   was sent at all.
2. **Wrong rejection reason** — a tile checked *before* B's tile failed buildability first, so
   the reply was `"Can't move cabin: blocked by terrain or object."` (or similar) instead of the
   farmer-collision message. The keyword match fails even though a rejection *was* sent.

## Root cause (residual race after #453)

The geometry and the validator are correct (verified): with A at `(40,18)` and B at `(42,18)`,
tile `(41,18)` passes buildability and tile `(42,18)` yields the collision message
(`CabinPlacementValidator.cs:48-68`; bbox `(Position.X+8, Position.Y, 48, 32)` per
`Farmer.cs:5940`). #453 already added `WaitForFarmerServerTileAsync` to confirm B sits in
`farm.farmers` at the footprint tile *at poll instant* — the right gate.

The residual gap is **how `!cabin` is sent**. The happy-path test
`ValidPlacement_MovesCabinToFarmerTilePlusOne` **resends `!cabin` on every poll iteration**, with
this comment (`CabinPlacementValidationTests.cs:68-71`):

> "Resend each poll: the !cabin handler reads the server's view of the farmer location, which can
> lag the client warp by a tick."

The three rejection sites do **not** resend per poll. They call
`Chat.AssertResponseAsync("!cabin", …)` — which sends once per attempt (2 attempts) and burns the
full `CabinAssignmentTimeout` inside its own loop — wrapped in an outer
`PollingHelper.WaitUntilAsync`. So the outer poll barely iterates and the "resends absorb
server-location lag" comment at the call sites is effectively not happening. A single `!cabin`
landing on a tick where the server's farmer view lags by one tick produces scenario 1 or 2, and
there's no per-poll re-fire to recover.

`OffFarm` gets away with single-shot because its gate is *static server-side state* — a fresh
farmhand spawns off-Farm, so the location gate is true the instant the client connects, with no
cross-process replication in the loop. `AnotherPlayer`'s "obstacle" is a *live, replicated farmer*,
exactly the tick-lag-sensitive state the happy-path comment warns about — which is why it's the one
that flakes.

`ObstacleInFootprint` is a **different and more dangerous case** — and my first analysis of it was
wrong. The Garden Pot is **not** durable-server-side the instant `PlacePot` returns:
`ActionsController.PlacePot` does `location.Objects.Add(tile, new IndoorPot(tile))` in the
*test-client's* `Game1` process (`tests/test-client/GameControl/ActionsController.cs:224-268`), and
the test asserts only `pot.Success` (client-side add) — it never waits for the pot to replicate to
the server's `farm.objects`, which is the *same* cross-process gap #453 fixed for farmer B, with no
equivalent server-side wait here. So the pot can be absent server-side when `!cabin` runs.

**Critically, resend-per-poll is *unsafe* for `ObstacleInFootprint`** — it would convert a transient
miss into a permanent one. If any early `!cabin` fires before the pot replicates, the validator
finds the footprint clear and **accepts** the move: `cabin.Relocate((41,18))` puts the 5×3 cabin
over `(41..45, 18..20)`, which covers the pot tile `(42,18)`. On every subsequent resend the
validator's self-overlap guard (`CabinPlacementValidator.cs:37-41`:
`farm.buildings.Contains(cabin) && cabin.occupiesTile(tile) → continue`) **skips** the pot tile
because the now-relocated cabin occupies it — so the obstacle is never re-checked and no rejection
is ever produced. Resending makes this strictly worse than single-shot. `ObstacleInFootprint`
therefore needs a **server-side wait that the pot is visible before the first `!cabin`**, not
resend-per-poll.

## Honest caveat

No pass/fail artifacts are available, so I can't *confirm* which scenario (1 vs 2) fires — per
`diff-flaky-runs-before-theorizing-mechanism.md`. The resend fix addresses the established race;
the reply-capture change (below) makes the *next* failure self-identify scenario 1 vs 2 instead of
recurring as another opaque "no trace" report. Both changes are low-risk and align the rejection
sites with the already-proven happy-path pattern.

## Change 1 — resend-per-poll primitive (max reuse, no duplicated loop)

`tests/JunimoServer.Tests/Infrastructure/Fixture/ChatTestHelper.cs`

Extend the **existing** `PollForKeywordsAsync` with one optional per-iteration hook so the resend
variant reuses its poll/seq-filter/match body rather than copying it:

- Add a `Func<Task<long>>? prepareIteration` parameter. When provided, the poll loop calls it at
  the **start of each iteration**; it resends the command and returns that send's fresh
  `TotalReceived` seq cursor, which the iteration then matches against (so a duplicate reply text
  from a prior send can't satisfy a later poll). When null, behavior is unchanged (fixed
  `seqBefore`) — `AssertResponseAsync` and `SendAndWaitAsync` keep working as-is.
- Keep the existing `onPoll` callback as the reply-capture channel — no second matching loop.

Add a thin wrapper that the rejection tests call:

```csharp
public readonly record struct CommandResponse(bool Matched, string? ObservedReply)
{
    public string Describe() =>
        ObservedReply is null
            ? "no reply observed (command accepted or no response sent)"
            : $"got reply \"{ObservedReply}\"";
}

// Resends `command` each poll until `expectedContains` appears; captures the latest reply in the
// command's family (`replyFamilyPrefix`, e.g. "Can't move cabin") so a wrong-reason failure names
// the actual reply. Idempotent commands ONLY — resending a mutating command double-applies it.
public Task<CommandResponse> ResendUntilResponseAsync(
    string command, string expectedContains, string? replyFamilyPrefix = null, TimeSpan? timeout = null)
```

Implemented as: `prepareIteration` = (snapshot seq, `SendChat(command)`, return seq);
`onPoll` = record the latest message containing `replyFamilyPrefix` and, on a hit, the matching
message. Returns `(matched, observedReply)`. Reuse the existing `Log(...)` for the response dump.

**Reuse check:** does NOT touch `ChatClient.SendAndWaitForResponseAsync` (lower-level, separate
concern) or the single-shot `AssertResponseAsync` (still correct for mutating lobby commands like
`!lobby create/rename/delete`, which must NOT resend). Only idempotent `!cabin` rejection reads
move to the resend path.

## Change 2 — self-identifying asserts at the rejection call sites

Replace the `PollingHelper.WaitUntilAsync(… AssertResponseAsync …)` wrapper at each idempotent
`!cabin` rejection site with a direct `ResendUntilResponseAsync` call (the outer wrapper becomes
redundant — resend-per-poll is the poll), then assert on the captured reply with a message that
names what actually came back.

Sites that are safe to resend. A site is resend-safe only if accepting an early `!cabin` (before
the obstacle is server-visible) does **not** permanently mask the rejection. `AnotherPlayer`
qualifies: a standing farmer keeps tripping the collision check on every resend, and an accepted
move can't relocate over its own footprint to hide the farmer. `OffFarm` and `FarmhouseStack` reject
on static server-side gates with no replication race at all, so resend is harmless (included only
for the self-identifying-reply message + one consistent assert pattern, not because they flake).
**`ObstacleInFootprint` is NOT on this list** — see Change 3.

| Test | File:line | expected keyword | replyFamilyPrefix |
|---|---|---|---|
| `AnotherPlayerInFootprint_RejectsAndDoesNotMove` | `CabinPlacementValidationTests.cs:206` | `another player is standing there` | `Can't move cabin` |
| `OffFarm_RejectsAndDoesNotMove` | `CabinPlacementValidationTests.cs:144` | `Must be on Farm` | `Must be on Farm` |
| `RejectsMove` (FarmhouseStack) | `CabinStrategyFarmhouseStackTests.cs:65` | `keep all cabins` | `Can't move cabin` |

Example (AnotherPlayer):

```csharp
var rejection = await Chat.ResendUntilResponseAsync(
    "!cabin", "another player is standing there", replyFamilyPrefix: "Can't move cabin");
Assert.True(
    rejection.Matched,
    $"Expected an 'another player is standing there' rejection; {rejection.Describe()}");
```

On a future failure this prints either `got reply "Can't move cabin: blocked by terrain or
object."` (scenario 2) or `no reply observed (command accepted or no response sent)` (scenario 1)
— making the flake decidable without artifacts.

**Per the project convention, every touched `Assert` carries a custom message naming the field and
the actual value** (`tests-assert-via-http-api.md`). The follow-up `IsHidden` / position asserts at
each site already have messages; leave those and only enrich the rejection assert.

## Change 3 — server-side pot-visibility wait for `ObstacleInFootprint` (single-shot, NOT resend)

`ObstacleInFootprint` has the same cross-process replication gap as the farmer case but the
**opposite** remedy: it must gate the *first* `!cabin` on the obstacle being server-visible, and
must stay single-shot (an accepted move masks the pot forever via the self-overlap guard — see Why).
This mirrors exactly how #453 fixed the farmer case with `WaitForFarmerServerTileAsync`.

There is no existing server-side object-at-tile probe (`/test/farmers`, `/cabins`, `/players`,
`/farmhands`, `GetFarmBuildings` are the only server reads), so add a minimal one mirroring
`/test/farmers`:

1. **New endpoint** `GET /test/object_at_tile?location=Farm&x=&y=` in
   `ApiService.TestEndpoints.cs` (next to `HandleGetTestFarmersAsync`): on the game thread, read
   `Game1.getLocationFromName(location).getObjectAtTile(x, y)` — the *same* lookup the validator's
   `IsTileBuildable` reaches via `CanItemBePlacedHere` — and return presence + `QualifiedItemId`.
   Never log at `Error` (test poison, `debugging.md`); surface failures via the response. Add the
   response model next to `TestFarmersResponse`.
2. **New client wait** `ServerApiClient.WaitForObjectAtServerTileAsync(x, y, …)` mirroring
   `WaitForFarmerServerTileAsync` (per-request timeout, retry on transient HTTP, one new `WaitName`
   variant `Polling_ServerApi_WaitForObjectAtServerTile`). Document the cross-process-replication
   rationale on it the way `WaitForFarmerServerTileAsync` is documented.
3. **Use it in the test**, between `PlacePot` and the `!cabin` poll:
   ```csharp
   var potVisible = await ServerApi.WaitForObjectAtServerTileAsync(
       CabinPlacementHelper.ExpectedCabinTile.X + 1, CabinPlacementHelper.ExpectedCabinTile.Y, ct: ct);
   Assert.True(potVisible, "Garden Pot did not replicate to the server before the rejection check");
   ```
   Keep the existing single-shot `Chat.AssertResponseAsync("!cabin", "Can't move cabin")` here, but
   enrich its assert message to name the actual reply for parity with Change 2 (capture the reply
   via the existing `onPoll` channel rather than the resend path). Do **not** move this site to
   `ResendUntilResponseAsync`.

(If a lighter touch is preferred over a new endpoint: `getObjectAtTile`'s `QualifiedItemId == "(O)590"`
artifact-spot carve-out aside, no existing endpoint exposes farm objects, so a new probe is the
direct solution — `simplest-solution.md`. Reusing `GetFarmBuildings` won't work; a pot is an
`Object`, not a `Building`.)

## Out of scope (verified, not silently dropped)

- No change to `CabinPlacementValidator.cs`, the `/test/farmers` endpoint, or
  `WaitForFarmerServerTileAsync` — all verified correct.
- The validator's self-overlap guard (`CabinPlacementValidator.cs:37-41`) is correct behavior (a
  cabin legitimately overlaps its own footprint when re-validated). It is the *reason*
  `ObstacleInFootprint` can't use resend, not a bug — left untouched.

## Verification

- `make test FILTER=CabinPlacementValidationTests` and `FILTER=CabinStrategyFarmhouseStackTests`
  green locally (covers all four sites + the new pot wait).
- Re-run `AnotherPlayer` several times (the reported flake) to build a small pass population.
- Confirm via run JSONL that for the resend sites `!cabin` resent more than once on at least one
  iteration (poll `iterations > 1`), so the new path is exercised — not just that the assert passed
  (`passing-test-isnt-proof-the-scenario-ran.md`).
- For `ObstacleInFootprint`, confirm the new `WaitForObjectAtServerTileAsync` matched (server saw
  the pot) *before* the `!cabin` reject — and that the site stayed single-shot — so the self-overlap
  masking can't recur.
- Smoke-test the new `/test/object_at_tile` endpoint returns the pot's `QualifiedItemId` for the
  placed tile and absence for a clear tile.

## Risk

Low–moderate. Changes 1–2 are additive test-helper + call-site edits (default-null param keeps
every existing caller unchanged). Change 3 adds a new test-only endpoint + client wait mirroring an
established pair (`/test/farmers` + `WaitForFarmerServerTileAsync`); the endpoint is gated to the
test API surface and reads game state read-only on the game thread. No production mod/server logic
changes.
