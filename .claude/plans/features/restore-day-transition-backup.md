# Restore the Day-Transition Backup Feature

## Context

`mod/JunimoServer/Services/Backup/{BackupService,BackupScheduler}.cs` were committed in May 2024 with the apparent intent of POSTing a save-day descriptor to a sidecar `/backup` endpoint after every Stardew save event, so that an external service could snapshot the save volume at known-clean points. The wiring never landed:

- Neither class implements `IModService`, so the auto-registration in `ModEntry.LoadServices` (`mod/JunimoServer/ModEntry.cs:166`) skips them.
- Neither class is `new`-ed up anywhere in the mod.
- `HttpClient` is not registered with the DI container.
- No `POST /backup` endpoint exists on the server side or in any sidecar — `.claude/plans/features/cli-v4.md:7` lists `POST /backup` as one of the missing endpoints blocking the CLI's remote-backup feature.

So the day-transition backup never runs today. This plan restores it as a working feature: registered properly, fully async, and with a concrete decision on what `POST /backup` is supposed to *do* — because SMAPI already auto-zips every save into `/data/Stardew/save-backups/` (`docs/features/backup.md:11-27`), so the in-mod hook needs a clear differentiator or it duplicates work.

The audit-doc correction is owned by `C:\Users\Test\.claude\plans\backupservice-uses-sync-over-async-wait-composed-hollerith.md`. This plan is the actual feature work.

## Open product question — what does `/backup` *do*?

This must be answered before implementation. SMAPI already produces a per-save zip on every save event (free, automatic, written to a docker volume). Three plausible roles for an in-mod `/backup` hook:

**(A) Notification only — chat/log + diagnostics event.** The hook does *not* produce its own backup; it simply emits a structured `mod_event` ("save backup created at day X year Y, file Z") so the CLI / UI / external monitors can react. SMAPI's zip is the actual backup. This is small, useful, and avoids duplication.

**(B) Off-volume push to an external sidecar.** A new sidecar (separate from the existing `tools/steam-service`) listens on `/backup` and copies the most recent SMAPI zip to S3 / a host bind mount / a remote share. The mod's role is just the trigger: "a clean save just happened, day X, file Z — go fetch it." This was probably the original 2024 intent.

**(C) Cosmetic — chat message only ("Creating backup… backup successful").** Matches the original `BackupScheduler` strings (`BackupCreating`, `BackupSuccess`, `BackupFail`) but with no real backup work. Misleading and not worth shipping.

**Recommended default: (A).** Cheapest, immediately useful, and forward-compatible with (B) — the sidecar can be added later without changing the mod. (C) is rejected as theatre.

The user should confirm A vs B before code lands; this plan assumes (A) and notes where (B) would extend it.

## Goals

1. Wire the day-transition backup hook so it actually fires on `GameLoop.Saved` and `GameLoop.SaveCreated`.
2. Make the call path fully async — no `.Wait()` and no `.GetAwaiter().GetResult()` on the game thread.
3. For option (A): emit a structured diagnostics event and a chat/console message at each save; no HTTP call (the existing `_httpClient` field is removed).
4. For option (B): the same plus an HTTP POST to a configurable endpoint, awaited fully async on a background continuation.
5. Either feature is opt-in (default off) so it doesn't change existing operator behavior on upgrade.

## Architecture (option A — recommended)

### Wiring

`BackupScheduler` becomes a `ModService` so the auto-registration loop picks it up:

```csharp
public class BackupScheduler : ModService
{
    public BackupScheduler(IModHelper helper, IMonitor monitor) : base(helper, monitor) { }

    public override void Entry()
    {
        Helper.Events.GameLoop.Saved += OnSaved;
        Helper.Events.GameLoop.SaveCreated += OnSaveCreated;
    }

    // …
}
```

This matches the established pattern for ~20 other services in `mod/JunimoServer/Services/**/*.cs` (e.g. `AlwaysOnServer : ModService`, `ApiService : ModService`). Constructor takes only `IModHelper` and `IMonitor`; the existing `BackupService` field is removed since (A) doesn't make HTTP calls.

`BackupService.cs` is **deleted** in option (A) — there is no HTTP client and no service worth keeping. If the user picks (B), `BackupService` is rewritten as a fully-async client and kept.

### Event emission (option A)

On each save event, `BackupScheduler` emits a `mod_event` via the existing infrastructure used elsewhere in the mod (`Services/Diagnostics/ModEventLog.Emit(...)` — see `mod/JunimoServer/ModEntry.cs:44-49` for the pattern, where `mod_phase` events are emitted with structured fields). The new event:

```csharp
ModEventLog.Emit("save_backup_created", new
{
    day = SDate.Now().Day,
    season = SDate.Now().SeasonIndex,
    year = SDate.Now().Year,
    saveFolder = Constants.CurrentSaveFolderName,
    smapiBackupDir = "/data/Stardew/save-backups",  // documentary; the consumer knows where SMAPI puts zips
});
```

Plus a chat message ("Save backed up — day X, year Y") and a `LogLevel.Info` log line. **Use `LogLevel.Info`, not `Error`** — `.claude/rules/debugging.md` makes Error-level lines test poison.

### Settings gate

A new field on `ServerSettings` (`mod/JunimoServer/Services/Settings/ServerSettingsLoader.cs`):

```json
"BackupNotificationsEnabled": false
```

Default `false` so the feature is opt-in. The scheduler reads it via the existing settings loader pattern (cf. `runtime-settings.md` for how live-toggle would be added later — out of scope for the initial restore).

### What about the original async `.Wait()` problem?

In option (A), there is no HTTP call at all, so the `.Wait()` is removed by deletion of `BackupService.cs` rather than by patching the call. Done.

## Architecture (option B — if the user picks the sidecar push)

### Differences from (A)

1. Keep `BackupService.cs`, but rewrite to fully async:
   - `Task<bool> CreateBackupForCurrentDayAsync(CancellationToken ct)` — no `.Wait()`, no `.Result`.
   - The endpoint URL becomes a settings field (`BackupEndpointUrl`, default empty = disabled), plumbed via `ServerSettingsLoader`. `HttpClient` is constructed inside `BackupService` (one instance per service lifetime, like `SteamAuthApiClient` does at `mod/JunimoServer/Services/AuthService/SteamAuthApiClient.cs:34`) — *not* registered as a singleton in DI, since DI doesn't have `HttpClient` registration today and adding one DI shape just for this feature is wider than the change.
2. `BackupScheduler` invokes the async client inside the SMAPI event handler. Because SMAPI events run on the game thread and `GameLoop.Saved` does not return a `Task`, the handler **must not await** — it kicks off a fire-and-forget `Task.Run(async () => …)` and traces success/failure via `mod_event` once the request completes.
   - The `Task.Run` body must apply `using (ExecutionContext.SuppressFlow())` *outside* the body if any AsyncLocal context could leak, per `.claude/rules/asynclocal-pitfalls.md` § "Long-lived background `Task.Run`". For a per-save short-lived task this is borderline; not strictly required, but preferred for hygiene.
   - Use `HttpClient.PostAsJsonAsync(url, request, ct).ConfigureAwait(false)` and a per-call `CancellationToken` with a sane timeout (e.g. 30 s) so a hung sidecar doesn't pile up tasks.
3. Failure path emits `LogLevel.Warn` (not Error — see `.claude/rules/debugging.md`) and a `save_backup_failed` event with `reason` field.
4. The sidecar itself (server-side `POST /backup` listener) is **out of scope** for this plan — it's a separate service. Note that `cli-v4.md` already plans for it; that should be the implementation reference if/when (B) is built.

## Decision points to confirm with the user before coding

1. **Option A vs B.** Default is A.
2. **Setting name.** `BackupNotificationsEnabled` (option A) or `BackupEndpointUrl` (option B). Default off / empty.
3. **Chat-message visibility.** Public (`SendPublicMessage`, like the original `BackupScheduler`) or admin-only? Public matches the original code; admin-only avoids mid-session flicker for players. Recommend public (it's a user-friendly cue that progress was saved). Out of scope to flesh out further until A vs B is locked.
4. **Delete `BackupService.cs` if (A) is chosen?** Recommended — no other consumer, and keeping unused HTTP client code invites the same audit confusion that prompted this work.

## Critical files

Option A (recommended):
- `mod/JunimoServer/Services/Backup/BackupScheduler.cs` — rewrite as `ModService`, drop `BackupService` dep, emit `save_backup_created` event.
- `mod/JunimoServer/Services/Backup/BackupService.cs` — **delete** (no consumer in option A).
- `mod/JunimoServer/Services/Settings/ServerSettingsLoader.cs` — add `BackupNotificationsEnabled` field, default `false`.
- `mod/JunimoServer/Services/Diagnostics/ModEventLog.cs` — *no edit needed*; the existing `Emit` is generic. Verify by reading `ModEntry.cs:44-49`.

Option B (only if user picks it):
- All of the above, plus
- `mod/JunimoServer/Services/Backup/BackupService.cs` — rewrite as `Task<bool> CreateBackupForCurrentDayAsync(CancellationToken)`. Construct `HttpClient` internally with `BaseAddress` from settings. Use `ConfigureAwait(false)`.
- `mod/JunimoServer/Services/Settings/ServerSettingsLoader.cs` — `BackupEndpointUrl` field instead of (or alongside) the boolean.
- Sidecar / server-side `/backup` endpoint — out of scope; tracked by `cli-v4.md`.

## Reused patterns / utilities

- **`ModService` / `IModService` registration loop** — `mod/JunimoServer/ModEntry.cs:164-219`. Drop-in for `BackupScheduler`; no changes to `ModEntry` itself needed once the class implements the interface.
- **`ModEventLog.Emit`** for structured events — see usage in `ModEntry.cs:44-49` (`mod_phase` events).
- **`SDate.Now()`** for current in-game date — already used by the original `BackupService`.
- **`Constants.CurrentSaveFolderName`** for the save folder name (matches what SMAPI uses for its zip filenames).
- **`SettingsCommand` dispatcher** for runtime toggling (`mod/JunimoServer/Services/Commands/SettingsCommand.cs:308`) — only relevant if we wire a runtime toggle, which is out of scope for the initial restore. Per `.claude/plans/features/runtime-settings.md`, the established pattern is to add a setter on `ServerSettingsLoader` mirroring `SetVerboseLogging`. Defer.
- **`SteamAuthApiClient` async pattern** (`mod/JunimoServer/Services/AuthService/SteamAuthApiClient.cs:17-34`) — the cleanest in-tree example of an HttpClient-using service if option (B) lands.

## Verification plan

### Code-level sanity checks (run before considering done)

1. Build clean: `dotnet build mod/JunimoServer/JunimoServer.csproj`.
2. Grep that the dead-code state is fixed: `Grep "class BackupScheduler"` should show `: ModService`. `Grep "BackupScheduler"` in the rest of `mod/` is allowed to remain zero hits — DI registration is implicit via `LoadServices`.
3. Grep that `_httpClient` and `.Wait()` no longer appear in `Services/Backup/` (option A: `BackupService.cs` is gone; option B: `.Wait()` is gone but `HttpClient` is allowed).

### Runtime / integration checks (this is the gate that matters — `.claude/rules/runtime-post-conditions-are-gates.md`)

These post-conditions are runtime observations and must be exercised against a real run, not declared green from static review:

1. **Feature off (default).** Start a server with no `BackupNotificationsEnabled` in settings. Sleep through a day. Confirm no `save_backup_created` event in `infrastructure.jsonl`, no chat message about backup. (Default-off contract.)
2. **Feature on.** Set `"BackupNotificationsEnabled": true`, restart, sleep through a day. Confirm exactly one `save_backup_created` event in `infrastructure.jsonl` per `Saved` fire, with the correct day/season/year fields. Confirm the chat message appears once.
3. **Day 0 (`SaveCreated`).** Create a fresh save. Confirm the `SaveCreated` path also fires the event (the original `BackupScheduler` subscribed to both `Saved` and `SaveCreated` — preserve that).
4. **No game-thread block.** Time the save event end-to-end. Option A should add no measurable wait (event emit + chat message is microseconds). Option B should also add no measurable game-thread wait, even with a deliberately slow sidecar — verify by pointing `BackupEndpointUrl` at a deliberately-stalling HTTP server (e.g. `nc -l 5000` with no response) and confirming the game continues to tick. This is the *actual* check that the original "deadlock" framing was about.
5. **Failure path (option B only).** With `BackupEndpointUrl` pointing at a 5xx-returning endpoint, confirm a `save_backup_failed` event with `reason` is emitted, the chat message reports failure, the SMAPI log line is at `LogLevel.Warn` (not Error — see `.claude/rules/debugging.md`), and the game continues to tick.
6. **No regressions in adjacent tests.** Run the existing E2E suite filter that exercises save/day-transition (e.g. `make test FILTER=DayTransition`) to confirm the new hook doesn't perturb existing behavior. Note: this is verification of *no change*, not of the new feature — a green run here is necessary but not sufficient.

If any of (1)–(5) fails, the feature is not done — investigate, do not declare complete based on a clean build.

## Compatibility verification

Per `.claude/rules/plan-discipline.md`'s adversarial section, the feature touches the save flow, which is sensitive territory:

- **LAN vs Steam transports.** The new event subscription (`GameLoop.Saved`/`SaveCreated`) is transport-agnostic — both transports fire these SMAPI events on the host. No transport-specific behavior added.
- **`hasDedicatedHost = false` invariant** (`.claude/rules/host-automation.md`). The new code does not flip this and does not subscribe to anything that depends on `Game1.dedicatedServer.Tick()`. Safe.
- **Test TPS / `SERVER_TPS=15`.** The hook is event-driven (one fire per save), not tick-modulo, so reduced TPS does not change behavior. Safe.
- **Disconnect mid-save.** If a player disconnects during the `Saved` event, SMAPI still fires `Saved` for the host. The new hook is host-only by virtue of `Saved` firing on the host; no farmhand-side concern.
- **Other subscribers to `GameLoop.Saved`.** `Grep "GameLoop.Saved"` across `mod/` returns: `LobbyService` (handles editing-session backup recovery), `GameLoaderService` (server settings persistence), and a few others. The new subscription is additive — handler order is not load-bearing because we don't read or write any state another handler also touches. Verify by re-running the existing E2E suite.

## What is *not* in this plan

- The actual `/backup` sidecar listener (server-side or external service). Tracked by `cli-v4.md`.
- A live runtime toggle for `BackupNotificationsEnabled`. The pattern from `.claude/plans/features/runtime-settings.md` would apply but it's not needed for the initial restore. Operators can toggle the JSON file and restart.
- Migration from the old `BackupService` for existing operators — there is no existing operator-visible behavior, since the code never ran.
- Test coverage at the unit layer. Per `CLAUDE.md`, "Helpers are integration-tested, not unit-tested" — verification is via the runtime checks above against a real run, not isolated tests.

## Why this is a feature plan, not a bug fix

The audit framed this as "fix a deadlock." Per `.claude/rules/retry-is-evidence-of-root-cause.md` (and `verify-claims.md`), the audit's premise didn't survive verification: there is no live deadlock, no live blocking, no live save-time freeze — because the code does not run. The right framing is "this feature was abandoned mid-implementation; do we want it?" The user's answer is yes, with proper wiring and a fully-async chain. That makes it a feature plan, with the audit-doc correction handled separately.
