# Scheduled world reset

## Goal

Automatically reset the public preview test server to a clean, fresh farm on a schedule
(weekly), so map clutter and abandoned progress self-heal with no moderation. Players are
warned with an in-game countdown before it happens. The schedule is deployment
configuration (an env var), never repo content.

This is the **first consumer** of the cron scheduler
([`server-cron-scheduler.md`](server-cron-scheduler.md)) and depends on it: the reset is a
single `IScheduledTask`, adding no scheduling machinery of its own. It uses service-level
operations only — never HTTP handlers, manufactured request context, HTTP timeouts, or API
validation as its safety boundary.

Deploys already keep the world (down→up, saves volume retained) — the desired "progress
persists across preview builds" behaviour. That stays. There is no scheduled reset of any
kind on `master` today; this plan adds it in-process rather than as a CI workflow (the
scheduler plan's Decision 1 gives the reasons a CI-shell reset was rejected).

## Mechanism: in-process `/newgame`, not a volume wipe

The reset uses the mod's in-process new-game path, not Docker volume deletion:

- `GameManagerService.RequestNewGame(config)` builds a fresh farm from `server-settings.json`
  (`NewGameConfig.FromSettings`) with full cabin allocation, and returns a `Task` that
  completes when the new world is created, loaded **and its day-0 save has finished**
  (`GameManagerService.OnUpdateTicked` resolves it on the first tick after `SaveLoaded` for
  which the day-transition predicate holds). It re-rolls `Game1.uniqueIDForThisGame`
  (`GameCreatorService.CreateNewGame`), writing a **new** save folder and repointing the load
  pointer (`GameLoaderService.SetCurrentGameAsSaveToLoad`, stored in global data).
- No old map, cabins, or farmhands carry into the new world. Correct for the goal.
- No host privilege, no container restart, no compose-name mirroring.

**The one gap `/newgame` leaves:** it does not delete the old save folder — stale folders
accumulate on the `saves` volume over weeks. The reset task deletes the previous save after a
confirmed create.

### `GameManagerService` changes (all in this plan)

1. **Reject a second new game in `RequestNewGame`.** It rejects when a *reload* is in flight
   but not when another *new game* is: a second call re-arms `_newGameCompletion` and orphans
   the first caller. It gets the same rejection as the cross-op branch (`_newGameCompletion
   != null` → `InvalidOperationException`). Not a coalesce: two requests can carry different
   configs, and `RequestReloadSave`'s own coalesce is incomplete — it keys on
   `_pendingReload`, which `ConditionallyStartGame` clears before `SaveLoaded`, so a second
   reload in that window re-arms after all. Fix that guard in the same change to key on
   `_reloadCompletion != null` alone. With the lease below held by every caller, both guards
   are defence-in-depth.
2. **Recover from a failed creation.** The new-game branch of `ConditionallyStartGame`
   faults the completion on a throw but, unlike the reload branch, does not reset
   `_gameStarted`, so the server parks at the title screen until restart. Mirror the reload
   branch (`_gameStarted = false`): the next tick re-enters `ConditionallyStartGame` with no
   pending config and reloads whatever the load pointer targets — the old save if creation
   threw before `SetCurrentGameAsSaveToLoad`, else a fresh world. This fixes the HTTP path
   too.
3. **`IsDayTransitionComplete()`** — `ApiService.ComputeDayTransitionComplete` (pure `Game1`
   reads; its own doc says it exists for `GameManagerService`) moves here. All three callers
   are rewired: `ApiService` (snapshot), `AlwaysOn.HandleAutoPause`, and
   `GameManagerService.OnUpdateTicked` itself. A scheduled task must not depend on
   `ApiService` (cycle risk, scheduler plan).
4. **World-disruption lease — one mutual-exclusion point for everything that drops
   players or has told them it will.** It is deliberately *not* named a reset lock or a
   transition lock, because it has two classes of holder and the code must say so:
   *transitions* (new game, reload — the world is replaced) and *announced disruptions*
   (the reset countdown, the deploy restart warning — players have been told a drop is
   coming and a second announcer must not interleave). Modelled as its own small type,
   `WorldDisruptionLease` (`Services/GameManager/`), exposed as
   `GameManagerService.Disruption`: `bool TryAcquire(string holder)` (false if held),
   `Release(string holder)` (clears only when the name matches, so a stale release can never
   clear a later holder), `string? Holder`. The type's doc comment states the two holder
   classes and the rule that *any* new code path that disconnects players or announces a
   disconnect must acquire it — that comment is the terminology's home; the API and docs
   say "world disruption", never "reset lock". A lock inside is what lets a holder release
   from a task continuation on the thread pool — every holder below ends its lease when its
   operation's returned `Task` settles, not on the game thread. Memory-only: a process kill
   clears it.

   **Every world transition holds it, not just consults it.** The transitions are
   exhaustive: the only callers of `RequestNewGame`, `RequestReloadSave`, and
   `Game1.ExitToTitle` in the mod are `HandlePostNewGameAsync`, `HandlePostReloadAsync`, and
   `SavesCommand.TryReloadActiveWorld` (grep, mod-wide), plus this reset and the restart
   warning below. The two HTTP handlers collapse their two marshals into **one action** that
   runs after body validation: lease check → client-count check → take lease →
   `RequestNewGame`/`RequestReloadSave`. That is atomic (today's count-then-request is two
   marshals with a join window between them), it orders the 409s deterministically (lease
   before count, so the body is stable while the reset's own client is still connected), and
   it means no early-return path ever holds a lease it must give back. The lease is released
   when the operation's `Task` settles — never on the 120 s HTTP timeout, so an operation that
   outlives its request still holds it until the world is loaded. `TryReloadActiveWorld` does
   the same inside its existing one-shot action and releases from its `ContinueWith`. Held →
   the two handlers 409 `"<name> in progress"`, `TryReloadActiveWorld` `Warn`s and returns,
   the reset `Skipped`s. Consult-only would leave an operator
   `/newgame` in flight invisible to a reset starting concurrently: `Game1.hasLoadedGame`
   goes true at `SaveLoaded`, but `_newGameCompletion` stays armed until the day-0 save
   finishes, so step 5 would reject after the reset had already broadcast and kicked.
   `HandlePostNewGameAsync` currently fails open when the marshal times out ("allow the
   attempt anyway") and changes to the 503 fail-closed behaviour `HandlePostReloadAsync`
   already has. `RequestNewGame` itself does not check the lease — the holder is the one
   calling it. Console `cabins migrate` and `settings` edits are not gated: they mutate a
   world the reset is about to discard, and `cabins migrate`'s `SaveNow` is covered by the
   save-suppression of `UpdateTicked`.

## Configuration (env, feature-first naming)

| Variable | Meaning | Default |
|---|---|---|
| `WORLD_RESET_CRON` | Standard 5-field cron, **UTC**. Unset/empty = no scheduled reset. | unset |
| `WORLD_RESET_WARNING_SECONDS` | Descending comma list of warning marks before the reset. | `300,120,60,10` |

The scheduler derives the first from the task key (`world-reset` → `WORLD_RESET_CRON`,
scheduler plan Decision 3). The task reads the second itself in its constructor: `Trim()`;
**unset or empty is the silent default** (compose always passes the variable, empty when the
operator set nothing, so a `Warn` here would fire on every boot); otherwise split on commas,
keep positive integers, sort descending, dedupe, and a non-empty value that yields nothing
logs `Warn` naming the variable and uses the default. Named "warning" to match the deploy's
`RESTART_WARNING_SECONDS` — same concept, same word. No `SDVD_` prefix (operator-facing). The
`environment.md` row states **UTC** prominently and gives the DST consequence (a UTC schedule
shifts by an hour in local time). Wiring in the same change: `docker-compose.yml`
(`"${WORLD_RESET_CRON:-}"`, `"${WORLD_RESET_WARNING_SECONDS:-}"`), `.env.example`,
`docs/admins/configuration/environment.md`. The experimental `docker/modern/docker-compose.yml`
is untouched — it already omits the other operator knobs and is Renovate-excluded.

**Deployment:** `deploy-server.yml`'s "Create .env file" step appends one more `echo` line,
`WORLD_RESET_CRON="$WORLD_RESET_CRON"` (double-quoted — the value contains spaces, and
`docker compose` parses quoted `.env` values), from a GitHub Environment **variable** passed
through the step's `env:` (`vars.WORLD_RESET_CRON`, not a secret — the build workflows already
use `vars.*` for non-secret config). The `public-test-preview` environment sets it to
`0 4 * * 0` (Sunday 04:00 UTC) in GitHub, not in the repo. Self-hosters get no reset unless
they set it.

Save retention is a task constant: the previous save is always deleted after confirmation
(SMAPI's own save-backups on the `game-data` volume remain the recovery path). A retention
knob is not built — no consumer.

## The task

`WorldResetTask : IScheduledTask` in `JunimoServer/Services/Scheduler/Tasks/`:

- `Key = "world-reset"`, `Description = "Reset the world to a fresh farm"`.
- Ctor deps (constructor-injected): `IModHelper` (for `SendPublicMessage`), `IMonitor`,
  `ServerSettingsLoader`, `GameManagerService`, `GameLoaderService` (gains a public
  `SaveNameToLoad` getter — today the pointer is private), `SaveImportService` (gains a public
  `HasPendingImport` over its existing intent read). Uses `Game1`, `OnlineFarmers`,
  `Constants` — all via the game thread. Never `ApiService`.
- A reset missed during downtime waits for the next occurrence (scheduler boot recompute).
  A reset whose occurrence coincides with a deploy restart is therefore **skipped for that
  week** — accepted operational behaviour, stated here.

`RunAsync(ScheduledJobContext ctx, CancellationToken ct)` — the kick-then-swap pattern of
`SavesCommand.TryReloadActiveWorld`, with a countdown prepended. Steps 1–3 pass the run `ct`
to every context call; steps 5–7 pass `CancellationToken.None`.

1. **Precondition + lease + capture**, one game-thread action, checks before the lease so no
   skip ever has to give one back: if `gameManager.Disruption.Holder` is non-null →
   `Skipped("<name> in progress")` (an operator transition or a restart warning is running);
   if `saveImport.HasPendingImport` → `Skipped("save import pending")` — the reset would
   repoint the load pointer away from the imported save and strand the import, and a queued
   import is an in-flight world disruption like any other; if not `Game1.hasLoadedGame &&
   Game1.player != null` → `Skipped("no world loaded")`. Else
   `Disruption.TryAcquire("world reset")` (cannot fail here — same action as the check) and
   capture `Constants.SaveFolderName` (`/newgame` re-rolls it). The lease is released in the
   task's `finally` — see step 6 for the one case where the `finally` deliberately does not
   run early.
2. **Who's online:** `OnlineFarmers.CountOthers()` on the game thread — a count, never a
   `Farmer` list handed to the background thread. The count decides only whether the long
   countdown runs; **the kick in step 5 is authoritative** for whoever is present then (a
   player who joins during the countdown sees the remaining marks and is kicked).
3. **Countdown** (count > 0): `CountdownAnnouncer` over the configured marks, given an
   announce delegate `Func<string, CancellationToken, Task>` that the task builds as
   `ctx.RunOnGameThreadAsync(() => helper.SendPublicMessage(text), ct)`. Deadline-based, not
   chained delays: `T = now + marks[0]`; announce `marks[0]` immediately; for each next mark
   `m`, `Task.Delay(max(0, T - m - now), ct)` then announce `m`; finally
   `Task.Delay(max(0, T - now), ct)`; so drift never accumulates and the reset lands at `T`.
   The clamp matters: an announce marshal that waited out a save can leave the next delay
   negative, and `Task.Delay` throws on a negative span. `ct` is signalled only by
   `POST /schedules/cancel` and aborts **before** any destructive step. A failed broadcast is
   logged `Warn` and the countdown continues — announcements are best-effort, the deadline is
   the contract. The last mark's message is the final warning players see. English text;
   `SendPublicMessage` detects the script for the language tag (`ChatLanguageDetector`,
   `chat-font-language-tag.md`).
4. **Point of no return.** The last `ct`-honouring await is the final delay in step 3. Once
   step 5's action is enqueued, cancel is ignored; the transition is a single line in code
   and the cancel test exercises both sides of it.
5. **Check quiet, kick, request — ONE game-thread action**, so no day transition can begin
   between check and act. First `gameManager.IsDayTransitionComplete()`; if false (a
   transition or save is being pumped over ticks — an auto-sleep can start one during the
   countdown) return "not ready" without touching anything; the task re-marshals every
   `QuietPollInterval` (1 s) for up to `QuietWaitTimeout`, then throws → `Failed`, nothing
   touched. Both are named constants on the task with the derivation in their doc comment:
   **`QuietWaitTimeout` is two minutes because that is already the project's ceiling for
   the longest world operation** — the E2E harness gives a full new-game or reload load 120 s
   (`ManagedServer.WaitForServerOnline` after `/newgame` and `/reload`), and the `/newgame`
   HTTP completion timeout is the same 120 s. A day transition (sync barrier + save + map
   reload) is a strict subset of that work, so one that is still incomplete after two minutes
   is not a slow save but a wedged loop or a peer that never finished its new-day handshake —
   the same condition step 6 treats as a server-level failure. Waiting longer would keep
   players who were told "reset in 10 s" in limbo without improving the odds; failing lets the
   next occurrence retry against a loop that has either recovered or been restarted. If a
   legitimate transition is ever observed to exceed the bound, raise the constant — the
   derivation comment is what makes that a one-line, reasoned change. When ready, in the same action:
   `Game1.server.kick(uid)` for each `OnlineFarmers.Others()` (no broadcast here — the same
   action ends in `ExitToTitle` → `CleanupReturningToTitle` → `multiplayer.Disconnect`, which
   tears the connections down in the same tick, so a message queued now never renders),
   `settings.Reload()` (only the reload path re-reads the file today; `ServerSettingsLoader`
   is game-thread-only, so the reload happens here), `NewGameConfig.FromSettings(settings)`,
   `gameManager.RequestNewGame(cfg)`, returning its completion `Task` out of the marshal. The
   kick is for clean UX only — `RequestNewGame`→`ExitToTitle`→`CleanupReturningToTitle`→
   `multiplayer.Disconnect` drops every peer regardless.
6. **Await the new world — no artificial timeout.** The engine's new-game operation cannot be
   aborted or observed to have stopped from outside; a scheduler-side timeout would declare
   the run finished, release the lease, and let an operator `/newgame` or `/reload` race an
   engine operation that is still unwinding. So the run stays `Running`, and the lease stays
   held, until `RequestNewGame`'s task settles. Its only two settlements are: faulted on a
   creation throw (change 2 above then reloads the old or a fresh world automatically), or
   completed after the day-0 save. A creation that never settles is a wedged game loop —
   visible as `running: true` in `GET /schedules`, a server-level failure that a restart
   resolves (boot repair → `Failed`). On fault, rethrow → `Failed`, **nothing deleted**.
7. **Delete the previous save** only after a confirmed create. Read the active
   `Constants.SaveFolderName` and `GameLoaderService.SaveNameToLoad` on the game thread, then
   delete **on the background thread** (filesystem work has no business on the game loop)
   under the invariant below. The pointer can be null here (`settings newgame --confirm`
   clears it); null counts as "differs", never as a match or a throw. Wrap in try/catch → on
   failure log `Warn` and return `Success` (the reset itself succeeded; a stale folder is not
   a reset failure).
   `FarmhandOwnershipService` binds its per-save store lazily by save name and its
   `OnSaveLoaded` resets that binding, so no live handle points at the deleted folder.

**Deletion invariant (code comment + test):** the captured old folder is deleted only when
*all* hold: the run has positively observed the new world's day-0 save complete (step 6);
`capturedOldName` is a single path segment (no separators, not `.`/`..`);
`Path.GetFullPath(Path.Combine(Constants.SavesPath, capturedOldName))` is directly under
`Path.GetFullPath(Constants.SavesPath)`; it differs from the active `Constants.SaveFolderName`
**and** from the load-pointer target. `Constants.SavesPath` is SMAPI's saves directory
(`<data>/Saves`), the same value the existing `/test/*` save endpoints and `SavesCommand` use.

**Pinned `RandomSeed` (verified, not assumed):** `GameCreatorService.CreateNewGameCore`
re-rolls `uniqueIDForThisGame` only when `RandomSeed` is null; otherwise `loadForNewGame`
assigns `uniqueIDForThisGame = startingGameSeed` (`Game1.loadForNewGame`), so the folder name
repeats and the new world's day-0 save overwrites the main file and `SaveGameInfo` in place
— the vanilla behaviour for a fixed seed. The invariant above then correctly skips deletion
(same name as the active save). The folder's ownership store is reconciled by
`FarmhandOwnershipService.OnSaveLoaded`, which drops records whose farmhand uid is absent
from the new save and writes the store. Log `Info` that the folder was reused.

### What "failed reset" guarantees — precisely

Not "the old world stays active": `RequestNewGame` leaves the old world (`ExitToTitle`)
before the new one exists. The guarantees are: **the old save folder is never deleted until
the new world's day-0 save is confirmed**, and after a failed creation the server
**recovers to a world by itself** (change 2: the load pointer still targets the old save if
creation threw before `SetCurrentGameAsSaveToLoad`, so the old world reloads; if it threw
after, a fresh world is created and the old folder remains on disk for manual recovery).

### Failure-state matrix

| Stage | Event | Result | Old folder deleted | Lease |
|---|---|---|---|---|
| 1 | lease held / import pending / no world | `Skipped` | no | not taken |
| 3 | cancel | `Canceled` | no | released |
| 3 | broadcast throws | continues | no | held |
| 5 | engine never quiet (`QuietWaitTimeout`) | `Failed` | no | released |
| 6 | creation throws | `Failed`; server reloads old or fresh world | no | released |
| 6 | creation never settles | stays `Running` | no | held until restart |
| 7 | delete throws / invariant fails | `Success` + `Warn` | no | released |
| 7 | success | `Success` | yes | released |
| any | process killed | boot repair → `Failed` | no | cleared by kill |

## Observability

The scheduler records the run outcome in the task's persisted run ring, queryable via
`GET /schedules`. The task logs its own progress:

- **`Monitor` (production):** `Info` at start (online count), each countdown broadcast, kick
  count, new-game start/complete, save deletion or folder reuse; `Warn` on a non-fatal
  deletion failure, a broadcast failure, a precondition skip, or a cancel. Never `Error`.
- **`ModEventLog` (test-only):** `world_reset_countdown` (secondsRemaining, onlinePlayers),
  `world_reset_kick` (count), `world_reset_newgame_started`, `world_reset_completed`
  (deletedSave | reusedFolder) — documented in `docs/developers/events-schema.md` per its "How
  to add a new event type" procedure. Start and failure are the scheduler's
  `scheduler_job_triggered` / `scheduler_job_failed`, not duplicated here. The kick and
  newgame events exist so the cancel test can assert their *absence*.

## Shared countdown + deploy restart warnings

A **CI deploy** (every push to `master`) also disrupts players: `deploy-server.yml` does
`compose down`→`up`. The world persists, but there is no shutdown save hook in the mod or the
container's service scripts, so every player is dropped and **progress since the last
in-game sleep is lost** (`GameManagerService.RunHealthCheck` documents the same for its
restart). Today that happens with no warning. The announce loop is extracted so CI shell
never owns countdown logic:

- **`CountdownAnnouncer`** — the deadline-based loop of step 3, `ct`-aware, given its
  announce delegate; knows nothing about "reset" or chat. A reusable utility, not a scheduled
  job, and not scheduler infrastructure.
- **`POST /notify/restart?seconds=N`** — same `[ApiEndpoint]` conventions and the **same
  central bearer auth** as every other POST (it causes a player-visible event and is called
  from deployment infrastructure). It is a **notification only** — it restarts nothing; the
  deploy owns the shutdown. **Synchronous:** the handler takes the lease `"restart warning"`
  (409 if held — checked **before** the player count, so a reset mid-creation with everyone
  already kicked still answers 409, not "nobody online"), runs the announcer to the deadline
  with the request as its lifetime, releases, and returns. No background task, no owned CTS,
  no notice object — the lease is the only state. A held request of up to N seconds matches
  the existing shape (`/newgame` holds one for up to 120 s; handlers are per-connection
  `Task.Run` with no global timeout). Responses: **200** warned and the deadline elapsed, or
  nobody was online (immediate); **400** non-positive N; **409** lease held. There is no
  cancel; it is independent of `/schedules/cancel`.

**Deploy wiring (on, 60 s):** delete the "Graceful shutdown preparation" placeholder step
and the `skip_graceful_shutdown` input. Inside the existing "Pull and restart containers"
SSH script (the API is only reachable on `localhost` there; `API_KEY` and `API_PORT` come from
the deploy directory's `.env` — the generated file writes `API_KEY`, and `API_PORT` falls back
to compose's `8080` default when absent), **between `docker compose pull` and `docker compose
down`** so the announced lead time is real. `RESTART_WARNING_SECONDS: 60` is a
workflow-level `env`, forwarded through the action's `envs:`; `0` skips the block entirely.
Shell contract, under `set -e`, one call that can never fail the deploy: `curl -sS
--max-time $((N + 15)) -w '\n%{http_code}' -X POST …` captured into a variable with `|| true`,
last line the status, the rest the body, then continue regardless — no temp file in the
deploy directory. The server does the waiting, so there is no client-side sleep and no status
table: 200 means the warning ran (or nobody was there), 409 means a reset is in flight and
has already warned its players — the deploy kills it, accepted (its residue is one undeleted
folder, boot repair marks it `Failed`, that week's reset is skipped), and 404 / refused /
timeout means an old image or a dark API. Echo the status and the response body on anything
but 200 — the Actions log is the only place this outcome lands.

**Operational caveat — the deploy now depends on one application-level HTTP request staying
alive for the whole warning period.** In-process behaviour is deterministic; an SSH session
holding a `curl` against the mod's API for 60 s is less so (SSH keepalive, runner network,
a game thread busy with a save when the announce marshal lands). The mitigations above make
every failure mode non-fatal to the deploy, so the risk is a *silent* warning that never
reached players, not a broken deploy. **Watch the first few real deployments** for exactly
that: the echoed status must be 200 and the SSH step's duration must grow by roughly N
seconds; a 200 that returns immediately with players online, a non-200 echo, or a step that
does not lengthen means the warning did not run. The kill switch is
`RESTART_WARNING_SECONDS: 0` in the workflow `env`, which skips the block entirely with no
server change. If the dependency proves unreliable, the fallback design is to make the
endpoint asynchronous (202, announcer on a background task, deploy sleeps client-side) — the
lease is the only state either shape needs, so the switch is confined to the handler and the
shell block.

## API

The reset needs no endpoint of its own: it is one row in `GET /schedules`,
`POST /schedules/run?task=world-reset` triggers a reset on demand (202 — the run lasts
minutes), and `POST /schedules/cancel?task=world-reset` aborts a running countdown; the
console equivalents are `scheduler run world-reset` / `scheduler cancel world-reset`
(scheduler plan, Decision 5). No broadcast endpoint is added: the countdown sends through
`SendPublicMessage` directly, and the existing WebSocket `chat_send` message remains the only
external chat path.

## CI changes (part of this change)

- In `deploy-server.yml`, exactly three edits: add the `WORLD_RESET_CRON` line to the
  generated `.env`; add the restart-warning call between pull and down; delete the
  "Graceful shutdown preparation" placeholder step and the `skip_graceful_shutdown` input.
  Nothing else in the workflow changes — the concurrency group, the SSH action, and the
  `sleep 30` + `docker compose ps` verify step stay as they are.
- No Discord notification for reset outcomes — deliberate: they are in `GET /schedules` and
  the server log, and a Discord hook for scheduler runs would be a separate feature with its
  own consumer question.
- Set `WORLD_RESET_CRON` on the `public-test-preview` GitHub Environment (operator step).
- Per `plan-discipline.md`, `git rm` both plan docs when this and the scheduler land, and grep
  their filenames across `.claude/`/`docs/` to repair citations.

## Testability

E2E only (see the scheduler plan). Harness wiring in the house pattern (explicit property →
config hash → env): `TestServerAttribute` gains `WorldResetCron` and
`WorldResetWarningSeconds` (strings), threaded through `ResourceRequirements` (both enter
`ComputeConfigHash`), `ServerContainerOptions`, and `ServerContainer`. `ServerApiClient` gains
`GetSchedulesAsync`, `RunScheduleAsync`, `CancelScheduleAsync`, `NotifyRestartAsync`,
`GetTestSavesAsync`. No save-listing probe exists today: add `GET /test/saves` (test-only)
returning the folder names under `Constants.SavesPath` plus the active name. The test class
is **not** `KeepConnected` (the reset kicks its own client; follow
`PasswordProtectionDisruptiveTests`) and is `[TestServer(Exclusive = true)]` at class level:
`SharedClass` runs methods concurrently, the reset kicks every connected client, and the
lock, restart-notice, and overlap tests all contend the one lease. The reset and cancel tests
use different warning values and each provision their own pooled server; the reset test
leaves its server on a fresh world, a valid pooled state.

- **Reset** (`WorldResetWarningSeconds = "2"`): connect, run, poll `GET /schedules` until
  `running` is false, assert `Success`, fresh world via the HTTP snapshot, old folder absent
  in `/test/saves`, and — from the run artifact, per
  `passing-test-isnt-proof-the-scenario-ran.md` — that `world_reset_countdown`,
  `world_reset_kick`, `world_reset_newgame_started`, `world_reset_completed` all fired.
- **Lock, both phases:** during the countdown (client still connected) `POST /newgame` and
  `POST /reload` → 409 with the lease message; and immediately after `POST /schedules/run`
  returns, before the first countdown mark, the same two calls → 409 (the lease is taken in
  the precondition action, before any await).
- **Cancel** (`"120"`): connect, run, cancel, assert `Canceled`, folder name unchanged, and
  that `world_reset_kick` / `world_reset_newgame_started` / `world_reset_completed` **never**
  fired. Boundary: a second variant cancels after `running` has already gone false → 409.
- **Restart notice vs reset:** `POST /notify/restart` during a countdown → 409; start
  `NotifyRestartAsync(seconds: 60)` **without awaiting it** (the call is synchronous for its
  full N), then run the reset → `Skipped` with the lease reason; await the notice task at the
  end so the lease is released before cleanup.
- **Manual overlap:** two back-to-back runs → second 409, one `runId`.
- **Armed/unarmed rows and occurrence semantics:** scheduler plan (test clock).

## Compatibility verification

- **`RequestNewGame` while players connected** — `ExitToTitle` disconnects everyone
  (`Game1.CleanupReturningToTitle`); the 409 guard is on the HTTP endpoint, not the service
  method. ✓
- **Operator world transitions during a reset, and a reset during one** — exhaustive set
  (three call sites) both hold and consult the lease; `/newgame` made fail-closed. ✓
- **Pending save import** — refused at step 1; the load pointer is never repointed away from
  a queued import. ✓
- **Reset landing on a day transition** — check and act in one game-thread action; a save
  in progress holds every marshal (`UpdateTicked` suppressed). ✓
- **Creation never settles** — run and lease stay held; no false release. ✓
- **Cancel after the point of no return** — token not passed to any later call; the reset
  completes with its real outcome. ✓
- **Deleting the old save** — invariant above; `FarmhandOwnershipService` rebinds per save. ✓
- **Scheduler history survives the reset** — mod-UID global data on the saves volume. ✓
- **Precondition** — lease held / import pending / no world → `Skipped`, all checked before
  the lease is taken. ✓
- **Chat font** — script-detected tag. ✓
- **Settings freshness** — `settings.Reload()` on the game thread before `FromSettings`. ✓
- **Self-hosters** — `WORLD_RESET_CRON` unset → unarmed, no per-tick cost, no reset. ✓
- **Deploy during a reset** — accepted, with the consequence stated: that occurrence is
  skipped; killed mid-countdown nothing happened; killed mid-creation the load pointer already
  targets the new folder (`SetCurrentGameAsSaveToLoad` precedes the day-0 save) and boot
  either loads it or creates fresh; residue is one undeleted previous folder. ✓
- **Restart warning vs reset** — mutually exclusive via the lease; no interleaved
  announcers; lease checked before the player count. ✓

## Implementation milestones — separately validated, each green on its own

This plan touches `GameManagerService`, the API handlers, the scheduler, chat, the deploy
workflow, and the test harness at once. The risk is regression, not design, so the work
lands as four milestones. Each is a self-contained commit set that leaves the tree
shippable and has its own validation gate; the next milestone starts only when the previous
gate is green. One PR is fine; four unvalidated commits in one push is not.

| # | Milestone | Contents | Validation gate |
|---|---|---|---|
| 1 | **Lease + `GameManagerService` fixes** | Changes 1–4 above: `WorldDisruptionLease`, the second-new-game rejection, the reload-coalesce guard, failed-creation recovery, `IsDayTransitionComplete` move, the two HTTP handlers collapsed to one action, `TryReloadActiveWorld` holding the lease, `/newgame` fail-closed. No scheduler, no task. | Existing API and reload E2E green unchanged (the behaviour-preserving bar, same as the dispatcher extraction in the scheduler plan). New tests: `/newgame` during `/newgame` → 409; `/reload` during `/newgame` → 409; a creation that throws leaves the server on a loaded world. |
| 2 | **Reset task** | `WorldResetTask` with countdown, quiet wait, kick, create, delete-after-confirm; env knobs, compose and docs wiring; `GET /test/saves`; harness properties. Requires the scheduler plan landed. | The Reset, Lock (both phases), Cancel, and Manual-overlap tests; run-artifact check that all four `world_reset_*` events fired. |
| 3 | **Countdown extraction + restart notification** | `CountdownAnnouncer` pulled out of the task (the task's behaviour is unchanged — milestone 2's tests are the regression net), `POST /notify/restart`, `ServerApiClient.NotifyRestartAsync`. | Milestone 2's tests still green; the Restart-notice-vs-reset tests. |
| 4 | **Deployment wiring** | `deploy-server.yml` restart-warning block, `WORLD_RESET_CRON` in the generated `.env`, delete the graceful-shutdown placeholder and its input, set the GitHub Environment variable. | One real deploy to `public-test-preview` observed per the operational caveat above (status echo 200, step duration grew by N); `GET /schedules` on the deployed server shows `world-reset` armed with the expected `nextRunUtc`. |

Milestone 1 is the one with the widest blast radius and no new feature to show for it — it
must be reviewed as a refactor, on its own diff, before any reset code is in the tree.

## Out of scope

The scheduler itself (own plan); the web UI; save retention beyond "delete the previous
save"; per-job timezone display.
