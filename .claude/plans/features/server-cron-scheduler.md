# Server-native cron scheduler

## Goal

A reusable, server-native scheduler that runs recurring wall-clock jobs reliably and on time,
configured per deployment through environment variables and observable over the HTTP API. The
first consumer is the weekly world reset ([`server-world-reset.md`](server-world-reset.md));
scheduled restarts and periodic off-site backups are plausible later consumers. This plan
covers **only the scheduler**.

A GitHub-Actions reset (a scheduled workflow that SSHes in and wipes the saves volume) was
prototyped and rejected: CI shell is untestable, needs host SSH + Docker privilege, and
hand-mirrors compose's volume naming. The in-mod approach uses capability that already exists
(`ApiService`, `GameManagerService`, DI).

## Prior art in the tree (why this isn't a duplicate, and scope honesty)

`Services/Backup/BackupScheduler` already exists but is **game-event-driven**: it backs up on
SMAPI `SaveCreated`/`Saved` (per in-game day), not on wall-clock time. It stays as-is; this
scheduler is the **wall-clock/cron complement**, a different trigger model. The reset is
currently the **only** concrete cron consumer — the reusable layer is a stated requirement
(the user's explicit "cron system now, reused later"), so it is kept deliberately **minimal**:
a task interface, a tick-driven trigger, run history, and a read/run/cancel API. No job
CRUD, no parameter store, no settings-file section — a job is configured the way every other
operator-facing server knob is configured (`SERVER_TPS`, `SERVER_PASSWORD`): by env var.

**The scheduler's contract with a task is exactly:** identity (`Key`, `Description`),
schedule (its env var), execution (`RunAsync`), result, cancellation token, history, and API
exposure. It knows nothing about countdowns, chat, world readiness, save folders, or
deployments, and never special-cases a task key.

## Decision 1 — in-process code, not system cron or a scheduling framework

- **Not an OS cron daemon** in the container: the game-server image ships none, it splits state
  (crontab vs app) and can't be API-observed.
- **Not Quartz.NET / Hangfire / Coravel**: heavyweight, bring their own thread pools and
  persistence stores, and — decisively — drag transitive dependencies that would violate this
  project's strict assembly-version floors (`JunimoServer.csproj`: DI Abstractions pinned
  5.0.0.0, System.Text.Json 6.0.0). They also don't integrate with the game-loop marshaling the
  tasks need.
- **Not hand-rolled cron math**: DST-correct next-occurrence over standard cron is the wheel
  worth reusing — via a dependency-free parser (Decision 4).

## Decision 2 — driver: evaluate on the game loop, execute off it

- **Evaluation (cheap, on the game thread).** `SchedulerService : ModService` subscribes
  `GameLoop.UpdateTicked` in `Entry()` **only when at least one job is armed** (Decision 3).
  Env is immutable for the process, so this is decided once: no armed job → no subscription
  ever; one or more → exactly one subscription for the process lifetime; no dynamic
  subscribe/unsubscribe; manual runs of unarmed tasks need no subscription. Not
  `OneSecondUpdateTicked` (12 s at `SERVER_TPS=5`, `one-second-update-ticked-fires-per-game-tick.md`);
  self-gated to once per real second. Each armed job carries an **in-memory** `NextDueUtc`. A
  pass compares `Clock.Now >= NextDueUtc` over the armed list — no cron parsing, no
  next-occurrence computation, no expensive work per tick. (Zero allocation is the target,
  not a licence for fragile state; a trivial allocation beats complexity.)
- **Clock.** The scheduler reads time through one `Func<DateTimeOffset>` field (default
  `() => DateTimeOffset.UtcNow`). A test-only `POST /test/scheduler/clock?offsetSeconds=`
  shifts it, so due detection, late evaluation, skipped occurrences, and clock jumps are
  E2E-testable without wall-clock waits. Nothing else reads the clock.

  **The clock shift does not exist outside `Env.IsTest` — it is unreachable, not merely
  unauthorized.** Two independent gates, neither of which is the bearer-auth check: (1) the
  handler lives in `ApiService.TestEndpoints.cs` and is dispatched only through
  `DispatchTestEndpointAsync`, which the router calls only when `Env.IsTest`; every other
  process answers `/test/*` with the same 404 an unknown route gets (after auth, so its
  existence is not leaked either), and `OpenApiGenerator` omits it from the production spec;
  (2) `SchedulerService.ShiftClock` itself throws `InvalidOperationException` unless
  `Env.IsTest`, so no future caller — console command, another endpoint, a task — can move
  the production clock even if gate (1) is bypassed. `Env.IsTest` is `SDVD_ENV=test`, set
  only by the test harness (`ServerContainer`), never by `docker-compose.yml`.
- **Execution (long, off the game thread).** A due job's `RunAsync` runs as a tracked
  background `Task` (a countdown can be minutes; it must not block the loop) and marshals its
  own discrete game-thread steps back via the task context (Decision 6). The wrapper is
  started under `ExecutionContext.SuppressFlow()`: `Task.Run` flows the ambient
  `ModRequestContext`, so a run started from inside the `POST /schedules/run` marshal would
  otherwise attribute every event it emits for minutes to that one HTTP request
  (`asynclocal-pitfalls.md`).

Cron is evaluated in **UTC**. Field names carry it (`nextRunUtc`, `lastTriggeredUtc`), and the
env docs state it prominently — an operator in Europe will otherwise read `0 4 * * 0` as
local 04:00; across DST the local time of a UTC schedule shifts by an hour.

A dedicated background-timer driver would only be warranted for sub-second precision or firing
*during* a long save; neither applies to minute/hour/day jobs. SMAPI suppresses `UpdateTicked`
while a save is in progress, so evaluation pauses for the save and resumes on the next tick.

### Occurrence semantics (the scheduler's core invariant)

**Every cron occurrence is consumed at most once, and an occurrence that becomes overdue while
the job is not runnable is skipped, never replayed.** Concretely:

- `NextDueUtc` is always *the first occurrence strictly after the moment it was last
  computed*. It is (re)computed from `Clock.Now` at: boot; acceptance of a scheduled trigger;
  and completion of any run (scheduled or manual). A manual run of an unarmed task computes
  nothing (there is no cron) and never mutates schedule state.
- A scheduled trigger is accepted when `Clock.Now >= NextDueUtc` and the task is not running.
  Acceptance advances `NextDueUtc` past **now** (not past the consumed occurrence), so with a
  stalled loop from 04:00 to 04:05 the 04:00 occurrence fires once at 04:05 and every
  occurrence in between is discarded.
- While a run is in flight the armed job is not evaluated. At completion `NextDueUtc` is
  recomputed from now, so occurrences that fell during the run are discarded — with
  `* * * * *` and a six-minute job: 12:00 fires, 12:01–12:06 are skipped, completion at 12:06
  leaves the next due at 12:07. No catch-up run, ever.
- Boot recompute is the whole missed-window policy: occurrences elapsed during downtime are
  never seen. A "run-once catch-up" mode is a trivial addition when a consumer needs it
  (noted, not built).
- **Clock jumps.** Forward: at most one occurrence fires, then `NextDueUtc` is past the new
  now. Backward, with `NextDueUtc` still in the future: the job waits until the wall clock
  reaches it again. Backward after the due moment was passed but before the next evaluation:
  the pass sees `now < NextDueUtc` and simply waits — the occurrence fires when the clock
  reaches it. Deterministic in all cases.

These are E2E-tested through the test clock (Testability).

### Run lifecycle

- **One acceptance path.** `TryStartRun(taskKey, trigger)` is the single scheduler-owned
  method used by cron evaluation and by `POST /schedules/run`. It runs on the game thread
  (evaluation is already there; the API marshals to it), so acceptance is serialized by
  construction — never "check `IsRunning`, then insert" from two contexts. It owns: the
  running check, `runId` creation, `CancellationTokenSource` creation, live handle insertion,
  the persisted `Running` ring entry, and (for scheduled triggers) advancing `NextDueUtc`. It
  returns the new run or "already running".
- **Overlap guard on live state.** `taskKey → RunHandle { Task, Cts }` in memory, where
  `Task` is the **whole wrapper** — `RunAsync` plus the marshaled completion write — so a new
  run cannot start while the previous run's history write is still queued. A persisted
  `Running` entry is **not** the guard (a dropped completion write would otherwise wedge the
  job until restart); it exists only for crash detection at boot.
- **Completion** marshals its status write onto the game thread with the token overload and
  `CancellationToken.None` (nothing is waiting on it; a busy thread just delays it), removes
  the handle, disposes the CTS, and recomputes `NextDueUtc` for an armed task.
- **Cancellation.** `POST /schedules/cancel?task=` signals the run's token (marshaled, so it
  serializes with completion: a run that has already completed answers 409). Tasks decide
  where the token is honoured, so a run ends `Canceled` only if it actually stopped on the
  token — `OperationCanceledException` carrying that token — else with its real outcome.
- **Error isolation:** any other exception → `Failed` with `error`, logged `Warn`. A failing
  task never kills the loop. **Why `Warn` and not `Error` for something an operator cares
  about:** in this mod `LogLevel.Error` is reserved for conditions that should fail an E2E
  run — `ServerContainer` cancels the test on any `ERROR`/`FATAL` log line
  (`debugging.md`), and `ModEntry` pairs its own `Error` lines with `Environment.Exit(1)`.
  A task failure is a *task-level outcome* that scheduler tests deliberately provoke
  (cancellation, precondition skip, a task that throws), so it cannot be `Error` without
  poisoning those tests. Operational significance is carried by the durable channels
  instead: the `Failed` ring entry with its `error`, `lastStatus`/`lastError` in
  `GET /schedules`, and the `scheduler_job_failed` test event. A task that detects a
  condition the *server* cannot recover from (state it cannot repair) is free to log `Error`
  itself — the scheduler's rule covers only the outcome it records on the task's behalf.
- **Preconditions:** a task may be inapplicable at fire time. `RunAsync` returns
  `ScheduledRunResult.Skipped(reason)` → recorded `Skipped`, distinct from `Success`.
- **Lifecycle / shutdown.** There is no mod shutdown hook and `ModService` has no dispose;
  SIGTERM ends the process. Policy: no graceful task completion is attempted, nothing blocks
  or `Wait()`s, and the next boot's crash-safety pass repairs state with one rule: every ring
  entry still `Running` → `Failed`, `error = "interrupted by restart"`; one repaired store is
  persisted. `NextDueUtc` is computed from boot time, so the interrupted occurrence is not
  re-run.

## Decision 3 — configuration by env var; history in SMAPI global data

**One job per task, configured by one env var.** Each task declares `Key` (`world-reset`)
and the scheduler derives its cron variable as `<KEY_UPPER_SNAKE>_CRON` (`WORLD_RESET_CRON`).
Feature-first naming matches the repo's grouping (`API_*`, `SERVER_*`, `STEAM_AUTH_*`) and
keeps a task's variables adjacent; no `SDVD_` prefix — that prefix marks test-harness and
kill-switch knobs, not operator-facing settings. The value is `Trim()`med. Semantics:

- unset / empty → the task is **not armed**: it appears in `GET /schedules` with
  `cron: null, nextRunUtc: null`, can be run manually, and costs nothing per tick.
- parseable → armed; `Info` log at boot with the cron and first `NextDueUtc`.
- unparseable → not armed; `Warn` naming the variable and the parse error, and the row's
  `configError` carries the same message so an operator sees it in `GET /schedules` without
  reading the log. Never a boot failure — a typo must not take a server down.

There is no runtime edit path. That is the accepted cut: every other operator knob has the
same property, and a settings-backed store can be added when a UI actually needs one.

**Run history** survives restarts for observability. `SchedulerStore` wraps
`helper.Data.ReadGlobalData/WriteGlobalData<SchedulerData>("scheduler")`
(`SchedulerData { Dictionary<string, List<RunRecord>> Runs }` keyed by task key — one bounded
ring of the last 10 runs per task, nothing else), the `GameLoaderService` pattern. The
"last run" summary a reader wants is the newest ring entry and is derived at read time, never
stored twice. Verified: SMAPI global data lives at `.smapi/mod-data/<mod-uid>/`
under the game data path, which is the **saves** volume mount (`docker-compose.yml`), keyed by
mod UID **not per-save** — so the reset's own `/newgame` does not wipe its history. Assert in a
code comment. Only run state lives there; configuration never does. An unreadable store
(corrupt JSON) logs `Warn` and starts empty — history is diagnostics, never a boot blocker
(the `FarmhandOwnershipService.ReadStore` pattern).

**Write cost.** `WriteGlobalData` is a synchronous JSON write of one small file (bounded: 10
ring entries per task, a few KB), on the game thread, only at trigger, completion, and the
boot repair — never per tick. It stays on the game thread: `GameLoaderService` and
`PersistentOptions` already write the same store from the same thread at load time, and a
few-KB Newtonsoft serialize plus one file write is sub-millisecond against a 200 ms tick at
`SERVER_TPS=5`. No off-thread path, no measurement gate.

**Wiring:** `Env.cs` gets nothing job-specific — the scheduler reads its variables itself
(`Environment.GetEnvironmentVariable`) because the name is derived from the task key.
`docker-compose.yml` passes `WORLD_RESET_CRON: "${WORLD_RESET_CRON:-}"` (and each later task's
variable). `.env.example` and `docs/admins/configuration/environment.md` document each variable
in the same change that adds its task (`verify-claims.md`: knob + consumer together).

## Decision 4 — dependency: Cronos (verified)

**Cronos** (`PackageReference`, exact `0.13.0`) — maintained, DST-correct, UTC-aware.
Verified against the published package, not the docs: the nuspec declares **no dependencies**
for the `netstandard2.0` and `net6.0` groups; a scratch `net6.0` project referencing it
resolves `Cronos` as its only package, transitive included; and the `lib/net6.0/Cronos.dll`
assembly references exactly one assembly, `System.Runtime 6.0.0.0` — the framework itself, so
there is nothing that can collide with the DI 5.0.0.0 / System.Text.Json 6.0.0 floors. No
implementation-time dependency check is needed; the first E2E boot covers the mod load like
any other change.

API used (signatures confirmed by reflection on that DLL): `CronExpression.Parse(string)`
(standard 5-field; throws `CronFormatException` on bad input — caught for the boot `Warn`)
and the **`DateTimeOffset` overload** `GetNextOccurrence(DateTimeOffset from,
TimeZoneInfo.Utc, inclusive: false)` (returns `DateTimeOffset?`; null when a cron will never
fire again → `nextRunUtc: null`, dormant). The test cron `0 0 29 2 *` resolves from
2026-09-02 to 2028-02-29 00:00 UTC, so the "armed but cannot fire" row is real. All scheduler
timestamps are `DateTimeOffset`, so no `DateTimeKind` conversion exists anywhere. Used
directly, no wrapper. `BundleExtraAssemblies=ThirdParty` auto-bundles it (the `DeployMod`
target globs `$(OutDir)*.dll`).

## Data model (`JunimoServer/Services/Scheduler/`)

Internally separated by file, one service: `Scheduling/` (armed jobs, occurrence math),
`Runs/` (`TryStartRun`, handles, cancellation), `SchedulerStore` (persistence + boot repair),
`Api` DTO mapping in the `ApiService.Schedules.cs` partial.

- `IScheduledTask` — `string Key`, `string Description`,
  `Task<ScheduledRunResult> RunAsync(ScheduledJobContext ctx, CancellationToken ct)`. A task
  reads its own knobs from env in its constructor — the scheduler knows nothing about task
  parameters.
- `ScheduledRunResult` — `Success` or `Skipped(string reason)`. Failure is a thrown exception;
  cancellation is the token's `OperationCanceledException`.
- `ScheduledJob` (in-memory, one per **armed** task) — `IScheduledTask Task`,
  `CronExpression Cron`, `string CronText`, `DateTimeOffset? NextDueUtc`.
- `RunRecord` (persisted; a bounded ring of the last 10 per task) —
  `{ runId, trigger (Schedule|Manual), scheduledUtc?, acceptedUtc, finishedUtc?, status
  (Running|Success|Skipped|Failed|Canceled), durationMs?, error? }`. `scheduledUtc` is the
  cron occurrence consumed (null for manual), `acceptedUtc` the `TryStartRun` moment — the
  pair diagnoses late evaluation. The entry is appended at acceptance with `status: Running`
  and completed in place by `runId`. There is no separate "last run" summary: the API derives
  it from the newest entry, and a task that has never run reports it as null.
- `ScheduledJobContext` — passed to `RunAsync`:
  `Task RunOnGameThreadAsync(Action, CancellationToken)` /
  `Task<T> RunOnGameThreadAsync(Func<T>, CancellationToken)`. Both use the dispatcher's
  **token overload, never the 5 s timeout**: a background job must wait out a multi-second
  save (`UpdateTicked` is suppressed while saving). The token is an **explicit parameter** so
  a task can pass its run `ct` before its point of no return (cancel then unblocks even a
  marshal stuck on a wedged game thread) and `CancellationToken.None` after it — an implicit
  run token would turn a late cancel into a skipped post-commit step and a false `Canceled`.
  Anything task-specific (chat broadcasts, world checks) is composed by the task from these
  two calls; the context carries no task vocabulary.

## DI wiring

`SchedulerService` is a `ModService`, auto-registered by the existing
`GetTypesWithInterface(typeof(IModService))` scan. Tasks are not `IModService` and are **not
discovered by reflection**: they are registered explicitly, one task per line pair, in a
`RegisterScheduledTasks(services)` method called from `LoadServices` after the `IModService`
loop. With one consumer (and a handful at most, ever) an explicit list is the readable form —
a new task is added by editing this method, and the diff says so.

```csharp
private static void RegisterScheduledTasks(ServiceCollection services)
{
    // Each task needs both descriptors — see the StartServices constraint below.
    services.AddSingleton<WorldResetTask>();
    services.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<WorldResetTask>());
}
```

**Why two descriptors per task (a `StartServices` constraint, not a discovery artefact).**
`StartServices` resolves `GetRequiredService(d.ImplementationType)` for every descriptor
whose `ImplementationType` is non-null and calls `Environment.Exit(1)` if that throws. A
single `AddSingleton<IScheduledTask, WorldResetTask>()` has `ImplementationType =
WorldResetTask` but registers only `IScheduledTask`, so that resolve throws and the server
dies at boot. The concrete registration makes the eager resolve succeed (one instance,
constructed once); the forwarding factory has a null `ImplementationType`, so `StartServices`
skips it, and `SchedulerService`'s `IEnumerable<IScheduledTask>` receives the same instance.
State this in the method comment so the next task author does not "simplify" it into the
one-liner. `SchedulerService` ctor takes
`IEnumerable<IScheduledTask>`, `GameThreadDispatcher`, `IModHelper`, `IMonitor`. Tasks take
their own deps by constructor injection (e.g. `GameManagerService`, `ServerSettingsLoader`) —
not static `Instance` accessors — plus `ScheduledJobContext` at call time. `ApiService` gains
a one-way `SchedulerService` dependency. **No task may depend on `ApiService`** (that would
close `ApiService → SchedulerService → task → ApiService`); engine-readiness predicates a task
needs live in game services, not the API (the reset plan moves one).

## Decision 6 — one shared `GameThreadDispatcher`, and store thread-safety

`ApiService.RunOnGameThreadAsync` and its `PendingGameAction` queue are extracted into
`Services/GameThread/GameThreadDispatcher : ModService` (auto-registered by the scan; a
`ModService` because it must **own its pump**: `ApiService.Entry` returns before subscribing
`UpdateTicked` when `API_ENABLED=false`, so a dispatcher drained from there would never run for
the scheduler). **This is the highest-risk refactor in the plan and is done first, as a
behaviour-preserving step with no scheduler code in the same commit — with exactly one
deliberate delta, named below.** Every `RunOnGameThreadAsync` call site across
`ApiService.cs` and `ApiService.TestEndpoints.cs` is `Action`-based and goes through the one
private method. That method **stays** as a thin private wrapper whose body becomes "time the
call, `await _dispatcher.RunAsync(action, 5 s)`, record the wait" — so zero call sites change
and the wait-time stats ring keeps its measuring point. Only the queue, the
`PendingGameAction` class, the drain handler, and the request-id rebind move.

The dispatcher owns exactly what today's private implementation owns: the `ConcurrentQueue`,
the tri-state atomic claim (`PendingGameAction`: pending → timed-out or executing,
`Interlocked`), the `TaskCompletionSource` with `RunContinuationsAsynchronously`, the
request-id capture + rebind across the pump (`ModRequestContext.Bind`,
`asynclocal-pitfalls.md` — needed from day one because `POST /schedules/run` emits its trigger
event from inside the marshaled action), and the drain in its own `UpdateTicked` handler (same
event as today — SMAPI suppresses it during saves, which is what keeps mutations out of a save
in progress). It raises `event Action Drained` after a pass that ran at least one action. The
two things that stay in `ApiService` wrap the dispatcher rather than live inside it: the
wait-time stats ring (measured around `await dispatcher.RunAsync`) and the post-drain
`TakeGameStateSnapshot()` refresh (subscribed to `Drained`). `/health` is independent of the
queue — it reads a tick timestamp written from `UnvalidatedUpdateTicked`. **The one
deliberate delta:** a throwing action is logged at `Warn`, not `Error` as today (`Error` is
test poison, `debugging.md`), and faulted to the caller as before. The existing "keep in sync
with the test client's `ExecuteOnGameThread`" note moves with the class.
`GameThreadOneShot` (console-command marshaling, no completion) stays as the console home.

Two overloads: `RunAsync(Action, TimeSpan timeout)` for request-style calls (5 s, as today)
and `RunAsync(Action, CancellationToken ct)` with no timeout for background work. Both resolve
through the same claim; the token registration takes the timed-out branch: a cancelled or
timed-out item still queued is skipped by the drain, and an item the drain has already claimed
runs to completion and reports its real result — a cancelled marshal can never execute later
behind the caller's back.

**Behaviour-preservation checklist for the extraction (each verified before the scheduler
lands):** timeout while queued (canceled, skipped by drain); timeout after execution started
(caller gets the real result); token cancel while queued; token cancel after execution
started; exception from the action (faulted to caller, `Warn`); request-id rebinding inside
the action; continuations off the game thread; no drain during a save; `API_ENABLED=false`
(dispatcher still pumps); service start ordering (`ApiService.Entry` subscribes to
`Drained` on an already-constructed dispatcher; alphabetical `StartServices` order is
irrelevant). Existing API E2E tests must pass unchanged after this step.

**Store single-writer, reads from a snapshot:** all run-state mutations (evaluation on the
game thread; API run/cancel marshaled to it) happen on the game thread, so `WriteGlobalData`
is only ever called there. After every mutation the scheduler publishes one immutable
`SchedulesSnapshot` (live handles, `NextDueUtc`, ring) into a `volatile` field — the
`ApiService._snapshot` pattern. `GET /schedules` reads that field with no marshal, so it
answers instantly during a save (when the dispatcher is not draining and a marshal would sit
in the 5 s timeout — exactly the window the reset test polls through), and `running`,
`lastStatus`, and `recentRuns` still describe one instant because they come from one object.

## Observability — three layers

1. **Live operator log (production) — SMAPI `Monitor`.** `Info` at boot per task (armed with
   cron + first due, or not configured), on acceptance (taskKey, schedule-vs-manual, runId)
   and completion (status + durationMs); `Warn` on unparseable cron, failure (with error),
   cancel, and precondition skip. No per-pass logging at any level; the scheduler itself
   never logs `Error` (Run lifecycle, "Error isolation").
2. **Durable, queryable history — persisted in the store.** The ring (last 10 per task).
   Survives restarts.
3. **Test event stream — `ModEventLog.Emit` (test-only; `SDVD_ENV=test`).** Emits
   `scheduler_job_triggered` / `_completed` / `_skipped` / `_failed` / `_canceled` for E2E
   assertions; documented in `docs/developers/events-schema.md` per its "How to add a new
   event type" procedure. Explicitly **not** a production channel.

## Decision 5 — API surface (matches the existing router)

The router is a flat per-method `switch (path)` (GET/POST/DELETE), exact-path with query
params. New cases go in those switches, in a new `ApiService.Schedules.cs` partial, with
`[ApiEndpoint]`/`[ApiResponse]` attributes for OpenAPI/Scalar and the central bearer auth
(POSTs require the API key when one is set, like every other write):

- `GET  /schedules` — served from the snapshot (Decision 6), one row per registered task:
  `taskKey`, `description`, `cron` (null when not configured — that *is* the armed flag),
  `configError`, `nextRunUtc`, `running` (**from the live handle**), `lastStatus` /
  `lastTriggeredUtc` / `lastError` / `lastDurationMs` (**derived from the newest ring entry**,
  all null for a task that has never run — after a crash `running` is false while
  `lastStatus` reads `Failed`; two concepts, documented as such), `recentRuns`.
- `POST /schedules/run?task=` — `TryStartRun(Manual)`, bypassing cron. Returns **202
  immediately** with `{ runId, acceptedUtc }` (acceptance, not execution start — the wrapper
  starts on the thread pool a moment later). 404 unknown task; 409 if already running.
  Allowed on an unarmed task; never touches schedule state.
- `POST /schedules/cancel?task=` — **request** cancellation (202). 404 unknown; 409 if
  nothing is running. Outcome is read from `GET /schedules` (Run lifecycle).

DTOs are explicit classes (like `NewGameResponse`) so OpenAPI documents them. `ServerApiClient`
(tests) gains the calls, plus the test-clock call.

**Console command — the same three operations for an attached operator.** `scheduler list`,
`scheduler run <task>`, `scheduler cancel <task>`: thin wrappers that marshal via
`GameThreadOneShot` with `requireLoadedSave: false` (the console home, like `saves reload`)
onto the same `TryStartRun` / cancel path and print the snapshot row. The console is where `saves`, `cabins`, and
`settings` live; triggering or aborting a reset must not require curl plus the API key.

## Edge cases (covered)

- **Cron never fires again** (`GetNextOccurrence` null) → `nextRunUtc: null`, dormant, not an
  error.
- **Two tasks due in the same pass** → both start; independent background tasks.
- **Manual run coincides with an auto-due tick** → `TryStartRun` serializes; one wins, the
  other is 409 / skipped.
- **Busy game thread at completion** → the timeout-free write just lands late; the live
  handle governs the overlap guard meanwhile.
- **Process killed mid-run** → boot repair records `Failed`; what the kill leaves behind is
  the task's concern (the reset plan covers it).

## Testability

No game-free assembly exists (both mod projects reference the game DLL and `make test-unit`
runs only `SteamService.Tests`), so coverage is E2E, using the world-reset task as the subject
(no test-only task) plus the test clock:

- The harness passes server env vars explicitly: a `TestServerAttribute` property →
  `ResourceRequirements` (enters `ComputeConfigHash`, so a distinct value provisions its own
  pooled server) → `ServerContainerOptions` → `ServerContainer`'s `WithEnvironment` chain.
  The reset plan adds `WorldResetCron` and `WorldResetWarningSeconds` on that path.
- **Rows:** default server → `world-reset` with `cron: null`. Armed:
  `[TestServer(WorldResetCron = "0 0 29 2 *")]` — Feb 29 00:00 UTC yearly, chosen because it
  is armed with a **non-null** `nextRunUtc` (the next leap day) yet cannot fire during a
  suite; not a weekly schedule, and not the never-fires case.
- **Occurrence semantics via the test clock** (armed server, `WorldResetWarningSeconds =
  "2"`, no client so the reset runs in seconds): shift the clock to just past the due moment →
  exactly one run, `scheduledUtc` equals the occurrence, `nextRunUtc` is the first occurrence
  after the shifted now; shift past several occurrences at once → still one run; shift while
  a run is in flight → no second run, and after completion `nextRunUtc` is after now.
- **Overlap:** `POST /schedules/run` twice back-to-back → second is 409 and exactly one
  `runId` in history; manual run in flight + clock shifted past a due moment → no scheduled
  run; scheduled run in flight + manual `POST` → 409.
- **Cancel** and the reset's own scenarios are in the reset plan.

## Implementation order

1. Extract `GameThreadDispatcher` (behaviour-preserving; existing API E2E green).
2. Scheduler models + store + boot repair. 3. Explicit task registration (DI wiring). 4. Cronos + occurrence
math + test clock. 5. `TryStartRun`, handles, cancellation, snapshot. 6. API partial + DTOs +
`ServerApiClient` + console command. 7. Scheduler E2E. Then the reset plan.

## Out of scope

World-reset semantics (own plan); the web UI and any runtime edit path; per-job timezone
display; run-once catch-up mode.
