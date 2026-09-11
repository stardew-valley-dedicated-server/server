# Consolidate Galaxy recovery into one poll-driven state machine

Status: proposed (not started). Execute in a fresh session, on top of the
`fix/invite-code-live-lobby-resilience` branch, BEFORE that work ships.

## Why (the smell, verified — not aesthetic)

The invite-code-resilience branch left the recovery half in a shape that is correct but not clean:
**two re-login drivers coexist and coordinate through shared mutable flags.** The Steam-reconnect
callback drives a re-login directly (`GalaxyAuthService.OnServerSteamIdReceived` →
`TryBeginGalaxyReSignInGated`), and a separate wall-clock supervisor (`PumpGalaxyRecovery`) drives the
same re-login machinery. Making the two not fight required three separate patches during review — a
stranding fix, a post-logon thrash fix, and a backoff-inflation fix — which is exactly the evidence that
two overlapping mechanisms were bolted together instead of consolidated. Recovery state also lives in ~6
booleans plus timestamps spread across four methods, so "which flag combination means what" must be
re-derived each time; that is why the three bugs hid until an adversarial pass.

**Consolidation is provably cleaner, not just different.** In a single poll-driven driver, the three
patches dissolve into two natural rules — *set the next-attempt clock when an attempt starts* (absorbs the
thrash and backoff-inflation fixes) and *do nothing while an attempt is in flight* (absorbs the rest).
They stop being special cases and fall out of one decision point. Maintainability improves because there
is one place that answers "should I re-establish the lobby right now?" and the states are named instead of
implied by flag tuples.

**What consolidation will NOT do:** make it simple. The Galaxy SDK imposes irreducible complexity — login
is async, logon must be polled (no callback), the SDK fires no auth callback on a total outage
(`.claude/rules/host-automation.md`-adjacent; the invite-code plan's limitation 3c), and every Galaxy call
is game-thread-only. Any correct design must model these. The goal is to remove the *accidental*
complexity (competing drivers, flag archaeology), not the essential complexity.

## Scope

**In scope (consolidate):**
- The Galaxy re-login trigger + gate + async fetch + logon-wait + server remove/re-add, and the wall-clock
  supervisor, into ONE game-thread driver with explicit states and a single per-tick decision point.
- The Steam-reconnect Galaxy branch becomes an *input event* to that driver, not a parallel path.

**Explicitly OUT of scope (do not touch / do not re-solve):**
- The invite-code mirror (`SetInviteCodeFromLiveLobby` / `WithdrawInviteCode` / `_galaxyInviteCode`) — it
  is already the clean single-writer design; keep it verbatim. The driver calls it; it does not change.
- The Steam *lobby* lifecycle (`CreateSteamLobbyViaHttpAsync`, `RecreateSteamLobby`,
  `UpdateGalaxyLobbyWithSteamLobbyId`, `_steamLobbyId`, `_steamLobbyPublished`, `_steamSessionGeneration`,
  the generation guard). Steam-lobby recreate stays the reconnect callback's own concern.
- The closed-source SDK no-auth-callback limitation — the driver stays poll-primary because the SDK cannot
  be trusted to fire `onGalaxyLobbyLeft`; do not switch to a purely event-driven model.
- The physical extraction of `GalaxyAuthService` into smaller classes — that is
  `.claude/plans/refactor/split-authservice-godclass.md`'s job, which already analyzed that recovery
  reaches back into Harmony-patch statics. This plan consolidates the *logic* (one driver, explicit
  states); it does NOT require a new DI'd class. Default to a cohesive region or a `private static`
  nested state machine inside `GalaxyAuthService`, and hand the class carve-out to the split plan.

## Verified current-state map (the machinery to fold into one driver)

Symbols in `mod/JunimoServer/Services/AuthService/AuthService.cs` unless noted. Confirm each exists before
editing (cite the tree, not memory).

Re-login machinery (to consolidate):
- `OnServerSteamIdReceived` — reconnect branch calls `CreateSteamLobbyViaHttpAsync()` (keep) then
  `TryBeginGalaxyReSignInGated("reconnect")` (replace with a driver input).
- `TryBeginGalaxyReSignInGated(trigger)` — the connected-check gate that decides re-login vs skip.
- `BeginGalaxyReSignIn` — off-thread ticket fetch (`Task.Run`) → arms `_pendingReSignInTicket(+Length)` +
  `_pendingGalaxyReSignIn`; guarded by `_galaxyReSignInInFlight` / `_pendingGalaxyReSignIn` /
  `_galaxyAwaitingReLogon`.
- `ConsumePendingGalaxyReSignIn` — game thread: `TryRemoveGalaxyServer`, `SignOut`, `SignInSteam`, arms
  `_galaxyAwaitingReLogon`; sets `_galaxyRecovering` / `_galaxyLobbyState` + the settle-window hold.
- `PumpGalaxyReLogonWait` — polls `GalaxyInstance.User().IsLoggedOn()`; on success re-adds via
  `TryLateAddGalaxyServer` + `UpdateGalaxyLobbyWithSteamLobbyId`, emits `auth_galaxy_recovered`; on
  timeout (`GalaxyReLogonTimeoutTicks`) re-adds the server (Gap-3b).
- `PumpGalaxyRecovery` — the wall-clock supervisor (`_galaxyRecovering`, `_nextReloginAttemptUtc`,
  `_reloginBackoffSeconds`, `_lastRecoveryEvalUtc`, `SupervisorGraceWindow`, backoff constants); emits
  `galaxy_lobby_recovering` / `galaxy_lobby_recovered`.
- `IsGalaxyLobbyConnected()` — the reflective probe (`GalaxySocket.Connected` = `lobby != null`), returns
  `true` / `false` / `null`.
- `TryRemoveGalaxyServer` / `TryLateAddGalaxyServer` — GalaxyNetServer lifecycle.
- Vanilla `GalaxySocket.recreateTimer` (20s) — a third recovery mechanism the driver must defer to during
  grace (do not fight it; the invite-code plan's compatibility section covers this).

Wiring: `SteamHelperUpdate_Prefix` calls, per tick (all game thread):
`ConsumePendingGalaxyReSignIn` → `PumpGalaxyReLogonWait` → `PumpGalaxyRecovery` → `PumpAuthReadinessPoll`.

Keep as-is (the driver reads/calls these, does not restructure them):
- Invite code: `SetInviteCodeFromLiveLobby`, `WithdrawInviteCode`, `_galaxyInviteCode`.
- Observability: `_galaxyLobbyState`, `_authReadiness`, `PumpAuthReadinessPoll`, and the `/status`,
  `/health` fields in `ApiService` — but source `galaxyLobby` from the driver's state (below).

## Target design: one poll-driven state machine

One driver, game-thread only, evaluated on the existing ~5s wall-clock gate from the pump, plus immediate
event inputs. Recovery state is an explicit enum, not flag tuples.

**States** (`GalaxyRecoveryState`):
- `Idle` — Galaxy not initialized, or no save loaded / reload teardown (not actionable).
- `Healthy` — lobby connected (`IsGalaxyLobbyConnected() == true`).
- `Waiting` — lobby down; holding until `nextAttemptUtc` (covers both the grace window that lets vanilla's
  20s recreate try first, and the backoff between attempts).
- `Relogin` — a re-login is in flight: ticket fetch, or `SignInSteam` submitted and awaiting logon. (The
  existing `_galaxyReSignInInFlight` / `_pendingGalaxyReSignIn` / `_pendingReSignInTicket` /
  `_galaxyAwaitingReLogon` become the *internal representation* of this one state; keep the async/poll
  mechanics, just relabel their meaning as sub-steps of `Relogin`.)
- `AuthBlocked` — auth classified permanently unavailable (dead/expired token via `_authReadiness ==
  "unavailable"`); same as `Waiting` but with the slow cap. Optional as a distinct state; may be modeled
  as `Waiting` with a larger backoff cap. Prefer the fewest states that stay readable.

**Inputs (events), all game thread:**
- `Tick(now)` — the wall-clock-gated eval; the primary driver.
- `SteamReconnected()` — from `OnServerSteamIdReceived`'s reconnect branch: set `nextAttemptUtc = now`
  (connectivity returned, so retry on the next eval without waiting out remaining grace/backoff) and run
  one immediate `Tick(now)`. Does NOT call re-login directly. This is THE consolidation — the reconnect
  path stops being a second driver and becomes a nudge.
- `TicketFetched()` / `LogonSettled(success|timeout)` — the existing off-thread ticket completion and the
  logon poll become internal transitions of `Relogin`, not separate pump methods.

**The single decision (`Tick`), replacing `PumpGalaxyRecovery` + `TryBeginGalaxyReSignInGated` +
`ConsumePendingGalaxyReSignIn`'s scheduling + `PumpGalaxyReLogonWait`'s scheduling):**

```
Tick(now):
  if not armed (_galaxyInitComplete false, or Game1.server == null, or reload/newgame pending):
     state = Idle; return

  # advance any in-flight re-login sub-steps first (consume pending ticket, poll IsLoggedOn)
  if state == Relogin:
     pumpReloginSubSteps(now)   # SignInSteam submit; on logon → re-add + re-stamp → Healthy check;
                                # on timeout → re-add server, fall through to reschedule
     if still Relogin: return   # in flight → do nothing else (absorbs F7: no escalation/backoff growth)

  connected = IsGalaxyLobbyConnected()   # true / false / null

  if connected == true:
     onHealthy()                # reset backoff, reconcile code if mirror null, clear recovering,
                                # galaxyLobby = connected, emit galaxy_lobby_recovered on transition
     return

  if connected == null and state != <recovering-ish>:
     return                     # boot / reload — not actionable (the arming guard)

  # lobby down
  enterOrStayWaiting()          # withdraw dead code via SetInviteCodeFromLiveLobby; galaxyLobby=recovering;
                                # on first entry: nextAttemptUtc = now + grace; backoff = initial;
                                # emit galaxy_lobby_recovering once

  if now < nextAttemptUtc: return   # grace (let vanilla recreate) or backoff wait

  # time to attempt: connected-check gate is implicit (we are here only because connected != true)
  startRelogin(now)             # BeginGalaxyReSignIn; state = Relogin
  nextAttemptUtc = now + backoff   # set at attempt START — absorbs F6 (post-logon window) AND F4
  backoff = min(backoff*2, cap)    # cap larger when _authReadiness == "unavailable"
```

**Why F4/F6/F7 dissolve here:**
- **F7 (backoff inflation):** the `if state == Relogin ... return` guard means the driver never
  re-evaluates escalation while an attempt is in flight; backoff grows exactly once per `startRelogin`,
  never per eval.
- **F6 (post-logon-pre-lobby-enter thrash):** `nextAttemptUtc` is set at attempt *start*, so after logon
  succeeds and `CreateLobby` is re-creating the lobby (~1-2s), the driver won't attempt again until the
  full backoff has elapsed — the window is covered by construction, no special case.
- **F4 (reconnect-path stranding + stale state):** there is only ONE driver, so "server removed then
  `SignInSteam` throws" is just a failed `Relogin` sub-step that returns the driver to `Waiting`; no
  second driver to strand it, no `_galaxyRecovering`-vs-null coordination needed.

**Steam reconnect keeps its Steam-lobby job.** `OnServerSteamIdReceived`'s reconnect branch still calls
`CreateSteamLobbyViaHttpAsync()` (Steam lobby is Steam's concern) and then calls `SteamReconnected()` on
the driver instead of `TryBeginGalaxyReSignInGated("reconnect")`.

## Thread model (unchanged boundaries, one owner)

| Operation | Thread | Note |
|---|---|---|
| `Tick` / all state transitions / `IsGalaxyLobbyConnected` / SignInSteam / IsLoggedOn / server add-remove | Game thread | driven by `SteamHelperUpdate_Prefix` pump; `SteamReconnected` also game thread (Steam callback runs in `GameServer.RunCallbacks`) |
| Ticket fetch (`BeginGalaxyReSignIn`) | Background `Task.Run` | result marshaled back via `_pendingReSignInTicket` + a pending flag, consumed on the next `Tick` |
| Mirror read (`/status`, `/health`) | HTTP thread | volatile reads only; `galaxyLobby` now sourced from the driver's state, still a volatile string |

No new cross-thread surface. The driver is a game-thread object; off-thread work stays exactly where it is
(ticket fetch), signaled through the existing pending fields.

## Invariants to preserve (regression gates)

- **Flap must never sever connected clients:** the attempt path is reachable only when `connected != true`
  (the gate is implicit in the `Tick` flow); a healthy lobby is never rebuilt.
  (`GalaxyOutageReproTests.GalaxyReloginGate_WhileHealthy_SkipsAndKeepsClientConnected` is the gate.)
- **Steam flap never withdraws the code:** `OnSteamServersLost` still must not touch `_galaxyInviteCode`;
  the driver withdraws only when the lobby is genuinely down.
- **Code decoupled from publish; G-code never exposed** — unchanged (`InviteCodes`).
- **`/health` stays HTTP 200** for an alive server — unchanged; `galaxyLobby` in the body now comes from
  the driver state.
- **Recovers independently of a Steam reconnect** — the driver's `Tick` escalates on its own timer;
  `SteamReconnected` is only an accelerator.
- **Never strand transport-less** — a failed/timed-out `Relogin` re-adds the server and returns to
  `Waiting`; the driver keeps attempting on backoff.
- **Events preserved:** keep emitting `auth_galaxy_relogin_attempt` / `auth_galaxy_relogin_skipped` /
  `auth_galaxy_recovered` (the outage test asserts these) and `galaxy_lobby_recovering` /
  `galaxy_lobby_recovered`. Map the new transitions onto the same event names; do not rename.

## Irreducible complexity to keep (do not "simplify" away)

- Async `SignInSteam` + `IsLoggedOn()` polling with a bounded give-up — keep.
- The `_steamSessionGeneration` guard on off-thread work — keep.
- Poll-primary (not callback-driven) because the SDK may not fire `onGalaxyLobbyLeft` — keep; the driver
  reads `IsGalaxyLobbyConnected()` each tick.
- Deferring to vanilla's 20s `recreateTimer` during the grace window — keep (it is often the cheapest
  fixer and avoids an unnecessary re-login).

## Anti-over-engineering guardrails (right-size)

- Model states as a single `enum` + a `switch`/if-ladder in one method. Do NOT introduce a generic FSM
  framework, a transition table object, or per-state classes — that trades one form of over-structure for
  another (`.claude/rules/universal/simplest-solution.md`).
- Do NOT extract a new DI'd service in this plan; a cohesive region or one `private static` nested type is
  the target. Class extraction is the god-class split's job.
- Do NOT add new observable state the UI does not consume; reuse `_galaxyLobbyState` / `_authReadiness`.
- Reuse the existing pending-fields for the off-thread hop; do not add a second marshaling mechanism.

## Tests / verification

- Keep `GalaxyOutageReproTests` (both methods) green — they are the flap-safety and total-outage
  regression gates and must not be modified except where an assertion references a removed symbol.
- Keep the `FarmhandVisibilityTests` connectivity + S-code + `/health`-200 assertions green.
- Build the mod + E2E test project clean (0/0); run the discord-bot unit suite (unaffected, confirm).
- Runtime gate (`.claude/rules/universal/runtime-post-conditions-are-gates.md`): run
  `make test FILTER=GalaxyOutageRepro` and read `containers/server-*/container.log` to confirm the
  recovery path fired (per `passing-test-isnt-proof-the-scenario-ran.md`), not just that the test is green.
- No new E2E endpoint is required; the invite-code plan deliberately cut the synthetic
  `/test/galaxy_lobby_down` test as low-value, and consolidation does not change that decision.

## Adversarial pre-check (cross-step invalidation, do before ExitPlanMode)

- Confirm `OnServerSteamIdReceived` runs on the game thread (Steam callback via `GameServer.RunCallbacks`
  in the pump) so `SteamReconnected()` can transition state directly — cite the call chain.
- Confirm the `SteamHelperUpdate_Prefix` patch is registered unconditionally (it is, in the
  `GalaxyAuthService` ctor) so the driver's `Tick` always runs — `harmony-patch-reachability.md`.
- Confirm no other caller invokes `TryBeginGalaxyReSignInGated` besides the reconnect branch, the
  supervisor, and the test endpoint (`TriggerGalaxyReSignInForTest`), so folding it leaves no orphan
  caller. The test endpoint must still reach the gate — route it through the driver's attempt entry.
- Confirm the three in-flight flags are all bounded (each is cleared within a bounded time) so the
  `Relogin` state can never wedge — this held for the current code; re-verify after the fold.
- Confirm `_galaxyLobbyState` has exactly one owner after the fold (the driver); the writer's
  `= "connected"` on a code write should become the driver's `onHealthy`, or stay as a documented
  second writer only if it closes the boot-window gap the invite-code plan's F3 addressed — decide and
  make it single-owner or explicitly two-owner-with-reason, not accidental.

## Implementation order

1. Introduce the `GalaxyRecoveryState` enum + the single `Tick` method (initially calling the existing
   sub-routines) so the structure lands before behavior moves.
2. Fold `TryBeginGalaxyReSignInGated`'s gate + `PumpGalaxyRecovery`'s scheduling into `Tick`; delete the
   supervisor method. Verify `GalaxyOutageRepro` green.
3. Turn `OnServerSteamIdReceived`'s Galaxy branch into `SteamReconnected()`; delete the direct
   `TryBeginGalaxyReSignInGated("reconnect")` call. Verify green.
4. Relabel `ConsumePendingGalaxyReSignIn` + `PumpGalaxyReLogonWait` as the `Relogin` sub-steps invoked from
   `Tick`; delete their separate pump wiring. Verify green.
5. Remove the now-dead F4/F6/F7 coordination fields/lines that the single driver made unnecessary; confirm
   each removal against the `Tick` logic. Verify green + read the run artifact.
6. Update `ApiService` to source `galaxyLobby` from the driver's state if the field's owner changed.
7. Delete this plan and update the invite-code plan's status note when the consolidation lands
   (`plan-discipline.md`).

## Acceptance criteria

- One method answers "should I re-establish the lobby now?"; there is no second re-login driver and no
  direct re-login call from `OnServerSteamIdReceived`.
- Recovery state is an explicit enum; no behavior depends on reasoning about a tuple of ~6 booleans.
- The F4/F6/F7 coordination lines are gone, their guarantees now implied by the `Tick` flow (attempt-start
  scheduling + in-flight guard), and re-verified against the flap and outage tests.
- `GalaxyOutageReproTests` + `FarmhandVisibilityTests` green; mod + tests build 0/0; run artifact confirms
  the recovery path fired.
- The invite-code mirror, Steam-lobby lifecycle, and the SDK limitation handling are untouched.
- No FSM framework, no speculative new service, no new observable state or marshaling mechanism.
