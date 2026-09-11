# Final Plan: Invite code mirrors the live Galaxy lobby; connectivity state is legible; Galaxy recovers independently

Status: implemented on branch `fix/invite-code-live-lobby-resilience` (mod + tests build clean;
discord-bot TS tests pass). **Do NOT ship as-is.** Phase 1 (invite-code mirror) and Phase 2
(observability) are clean and final. Phase 3 (recovery) is correct but structurally flawed — it layers a
wall-clock supervisor next to the pre-existing Steam-reconnect re-login driver, coordinating through
shared flags (the F4/F6/F7 review fixes are the evidence). It must be consolidated into a single
poll-driven state machine before this ships. See
[`.claude/plans/refactor/galaxy-recovery-single-driver.md`](../refactor/galaxy-recovery-single-driver.md)
— execute that in a fresh session, then ship all three phases together.

## Implementation status (what landed vs. what still needs a test endpoint)

Landed (all three phases): single writer/withdraw mirroring the live lobby; scattered clears removed
(the Steam-flap incident fix); `InviteCodes` decoupled from the publish gate and `Gog` deleted;
`gogInviteCode` removed end to end (mod `/status`, test DTO + readiness gate, Discord type, docs
fixtures); connectivity block on `/status` and `/health` (body-only, `/health` stays 200); `steamSession`
signal + `steam_session_connect_failure` event; recovery supervisor with the null-vs-recovering arming
guard, grace + bounded backoff, and Gap-3b re-add on relogin give-up; thread-safe `RecreateSteamLobby`
(3e); auth-readiness poll + permanent-failure backoff (2e/3d); banner one-refresh fix; reasoned command
replies. Docs updated (`steam-auth.md`, `public-status.md`, widget contract).

E2E coverage added now (cheap, high value): S-code + connectivity-field assertions and the
`/health`-stays-200 body check on the shared Steam server (`FarmhandVisibilityTests`), reusing an
existing Steam server; no-G-code is guaranteed structurally (the code DTO has no GOG fallback). The
existing `GalaxyOutageReproTests` cover the flap-safety gate (code unchanged) and a real total-outage
recovery, which now routes through the supervisor.

**Deliberately NOT added (cut as low-value E2E, not a TODO):** a no-outage supervisor-recovery test and
the persistent-auth-failure (3d) test. Both would need heavy, brittle infrastructure for little marginal
coverage: the first requires a `/test/galaxy_lobby_down` endpoint that reflectively nulls a private
`GalaxySocket.lobby` field — a *synthetic* condition (no real Galaxy drop leaves Steam up) — plus an
exclusive Steam account and a ~30s grace wait; the second needs a sidecar dead-token simulation. The real
failure mode (total connectivity loss) is already covered end-to-end and now exercises the supervisor,
the flap-safety gate is covered, and the new public contract is covered by the cheap assertions above.
The supervisor's internal timing (grace/backoff/arming guard) is defensive resilience code validated by
review, not by a fragile 30s Steam E2E. The Steam+GOG real-client join remains a manual compatibility
gate (inherent — the harness never dials real SDR).

This plan supersedes the earlier draft. Several premises in that draft were verified false against the
decompiled sources and the mod; they are corrected in section 1. Cite files and symbols, not line
numbers (positions drift over a plan's shelf life).

---

## 1. Verified architecture

**The authoritative Galaxy lobby object.** The single source of truth is the in-memory `lobby` field
(`GalaxyID`) of the `GalaxySocket` owned by the `GalaxyNetServer` inside `Game1.server`'s `servers` list.
`GalaxySocket.Connected` is defined as `lobby != null`. When the lobby is entered, `onGalaxyLobbyEnter`
sets `lobby`; when it is lost, `onGalaxyLobbyLeft` sets `lobby = null` and arms a 20s `recreateTimer`.
"Active Galaxy lobby" in this plan means exactly: `GalaxyNetServer` present in `servers` **and** its
`GalaxySocket.lobby != null`.

**How the S-code is derived — and the draft's central error.** Vanilla `GalaxySocket.GetInviteCode()`
returns `"S" + Base36.Encode(lobby.GetRealID())`. **The game's own invite code is S-prefixed.** There is
no separate "G-code the game hands out." The mod's `GalaxySocket_GetInviteCode_Postfix` captures that
S-string into `GalaxyAuthService._galaxyInviteCode`, and `InviteCodes` strips the first character
(`Base`) and re-prefixes: `Gog = 'G' + Base`, `Steam = 'S' + Base`. So the field comment
"(G-prefixed)" and the docs line "the game hands out the G-prefixed form" are both wrong — every displayed
form is derived by the mod from the vanilla S-code. This does not change what the fix must do, but it
corrects the naming and the source-of-truth story throughout.

**`GetRealID()` is a local native call, not a network call.** It is a SWIG PINVOKE into `GalaxyID`
(`GalaxyID_GetRealID`) — a local bit operation, no Steam/GOG round-trip — but because it is a native call
it must run on the game thread. `Game1.server?.getInviteCode()` (`GameServer.getInviteCode` → first
non-null `server.getInviteCode()` → `GalaxyNetServer.getInviteCode` → `GalaxySocket.GetInviteCode`)
therefore returns the live S-code on the game thread and `null` when no lobby exists. This is the correct
mirror source, strictly better than the cached postfix value.

**What S/G prefixes mean to clients (verified in `SDKNetHelper` implementations).**
`GalaxyNetHelper.GetLobbyFromGalaxyInvite` accepts either `'S'` or `'G'`, decodes the Base36 body to the
same Galaxy lobby id, and ignores the prefix. The prefix only matters to a Steam client:
`SteamNetHelper.GetLobbyFromInviteCode` builds `HybridLobby(galaxyId, isSteam: inviteCode[0] == 'S')`.
- **`'S'` → Hybrid → `SteamNetClient(GalaxyID)` → `TryConnectGalaxy`**, which reads the `SteamLobbyId`
  lobby-data key from the Galaxy lobby and then dials Steam SDR. This **requires the `SteamLobbyId`
  stamp** to be present, or it fails with "Missing Steam lobby ID."
- **`'G'` → `GalaxyNetClient`**, which joins over Galaxy P2P and presents a **Galaxy** identity — a Steam
  player who used the G-code gets a farmhand their Steam identity never sees again.

**So the S-code is universal and the G-code is never safe to publish:**
- A **GOG client** given the S-code decodes it, ignores the `'S'`, and joins over Galaxy P2P — it does
  **not** depend on the `SteamLobbyId` stamp, so a GOG player can join the moment the lobby exists.
- A **Steam client** given the S-code joins over SDR but only after the `SteamLobbyId` stamp lands (a few
  seconds later); before that it retries.
- The **G-code** would break Steam clients (wrong identity). It must never be exposed.

**What invite-code existence proves — and does not.** A non-null S-code proves only that the Galaxy lobby
object exists and produced an id. It does **not** prove the Steam relay is joinable (that is the
`SteamLobbyId` stamp / `_steamLobbyPublished`), does not prove the Steam session is up, and does not prove
the lobby is *alive* — if the SDK fails to fire `onGalaxyLobbyLeft` on a silent outage, `lobby` stays
non-null and the code stays non-null while nobody can actually join (see invariant 5 and §5).

**Lifecycle paths that create/replace/destroy the lobby (verified):**
- **Initial hosting:** `SaveLoaded` → `Game1.server = new GameServer()` → `GalaxyNetHelper.CreateServer`
  → `GalaxyNetServer.initialize` → `GalaxySocket.CreateLobby`. On this server `CreateServer` returns null
  and the Galaxy server is late-added by `TryLateAddGalaxyServer` (from the Galaxy state-change listener)
  because Galaxy auth completes after the GameServer is built.
- **Reload / newgame:** `GameManagerService` calls `Game1.ExitToTitle()` (sets `Game1.server = null`),
  then the `multiplayerMode == 2` path re-runs `Multiplayer.StartServer()` → **new** `GameServer` → **new**
  Galaxy lobby → **new** S-code. The Steam GameServer session and `_steamLobbyId` persist across this
  (services stay up on `ReturnedToTitle`). So a reload legitimately changes the code, and there is a brief
  window where `Game1.server == null`.
- **Vanilla auto-recreate:** on a genuine lobby drop, `onGalaxyLobbyLeft` arms `recreateTimer` (20s);
  `GalaxyNetServer.receiveMessages` (pumped every game tick by `GameServer.receiveMessages`) calls
  `GalaxySocket.Receive`, which re-runs `tryCreateLobby()` once the timer elapses. This recreates the
  lobby **without any Steam reconnect** — but only while the `GalaxyNetServer` is still in `servers`.
- **Mod recovery:** `OnServerSteamIdReceived` reconnect branch → `TryBeginGalaxyReSignInGated` →
  `BeginGalaxyReSignIn` (off-thread ticket fetch) → `ConsumePendingGalaxyReSignIn` (game thread:
  `TryRemoveGalaxyServer` — **removes** the GalaxyNetServer — then `SignOut`+`SignInSteam`) →
  `PumpGalaxyReLogonWait` (re-adds via `TryLateAddGalaxyServer`, re-stamps). If re-login times out, the
  `GalaxyNetServer` is **gone from `servers`**, so vanilla's recreate can no longer run either.
- **Steam-session loss:** `OnSteamServersLost` clears Steam-lobby state — and today **also nulls
  `_galaxyInviteCode` and deletes the file**, which is the incident bug (a Steam-CM flap strands the code
  even though the Galaxy lobby never left).

**`IsGalaxyLobbyConnected()` can lie.** It reads `GalaxySocket.Connected` (`lobby != null`) reflectively.
On the observed failure modes (full network cut) `onGalaxyLobbyLeft` fires and it correctly reads false
(confirmed by `GalaxyOutageReproTests`). On a hypothetical silent SDK death where the leave callback never
fires, `lobby` stays non-null and it reads a stale `true`. The `IUser` liveness members
(`SignedIn`/`IsLoggedOn`) also stay stale-`true`, so there is **no verified reliable local signal** for
that case. We preserve that limitation honestly (§5) rather than inventing a probe.

**`/health` is load-bearing for orchestration.** The Docker `HEALTHCHECK` curls `/health` and treats
non-2xx as unhealthy; `restart: unless-stopped` then restarts the container, and the deploy job fails on
`docker compose ps | grep -qE "unhealthy|Exit|Restarting"`. Therefore `/health` **must stay 200** for an
alive server and must keep reporting `status` off game-thread liveness only. Connectivity detail is added
to the body, never to the status code.

---

## 2. Invariants the implementation must maintain

1. If the authoritative Galaxy lobby exists (`Game1.server?.getInviteCode()` is non-null), the exposed
   invite code is exactly that S-code, mirrored within ~1s, regardless of Steam-session or relay state.
2. Steam session loss (`OnSteamServersLost`) never withdraws the invite code and never deletes the file.
3. The code is withdrawn only when the Galaxy lobby is genuinely gone (`getInviteCode()` returns null:
   server torn down, lobby removed for re-auth, or shutdown).
4. `_steamLobbyPublished` (the `SteamLobbyId` stamp) governs `steamRelayReady` only. It never gates
   whether the code is displayed.
5. The G-code is never written to any surface: `/status`, `/health`, file, banner, commands, Discord,
   docs widget, or the test readiness gate.
6. One writer owns `_galaxyInviteCode` and the file. Every other former mutation site routes through it or
   through the single withdraw.
7. All Galaxy SDK reads/writes (`getInviteCode`, `setLobbyData`, `SignInSteam`, `SignOut`,
   `IsLoggedOn`) run on the game thread. HTTP threads read only the volatile mirror and volatile booleans.
8. The recovery supervisor never runs while `Game1.server == null`, no save is loaded, a reload/newgame is
   pending, or Galaxy was never initialized (LAN). It never tears down a healthy lobby.
9. `/health` returns 200 for an alive server; connectivity detail is body-only.
10. Steady-state healthy operation does no per-tick allocation on the game thread for invite-code upkeep.

---

## 3. State model

Five independent signals, each with a single writer, all readable from the HTTP snapshot. None is a proxy
for another.

| Signal | Type | Source of truth | Written on (game thread unless noted) |
|---|---|---|---|
| `inviteCode` | `string?` (S-code) | live lobby via `Game1.server?.getInviteCode()`, mirrored to `volatile string` | lobby up / re-stamp / down |
| `galaxyLobby` | `connected \| recovering \| down` | `IsGalaxyLobbyConnected()` + supervisor phase | pump tick (gated) |
| `steamSession` | `connected \| lost` | `SteamGameServerService` connect/lost callbacks → `volatile bool` | Steam callbacks |
| `steamRelayReady` | `bool` | `_steamLobbyPublished` | `UpdateGalaxyLobbyWithSteamLobbyId` / clears |
| `authReadiness` | `ok \| expiring \| unavailable` | sidecar `/health` poll + recovery escalation → `volatile` | low-freq poll + supervisor |

Legal combinations to note: `steamSession=lost` with `inviteCode` present and `galaxyLobby=connected` is
the flap case (must not withdraw the code). `inviteCode` present with `steamRelayReady=false` is the
normal few-second window after a lobby comes up (GOG can join, Steam is retrying). `galaxyLobby=down` with
`steamSession=connected` is the Galaxy-only outage the supervisor must self-heal.

---

## 4. Phase 1 — Invite-code correctness (the incident fix)

### 1a. One owner, driven by the live lobby

**File/class:** `GalaxyAuthService`.
**New method:** `SetInviteCodeFromLiveLobby()` — the sole writer of `_galaxyInviteCode` and the file.
- **Current behavior:** the code is written in three places (`GalaxySocket_GetInviteCode_Postfix`,
  `UpdateGalaxyLobbyWithSteamLobbyId`) and cleared in four (`OnSteamServersLost`, `TryRemoveGalaxyServer`,
  `SteamHelperShutdown_Prefix`, `UpdateGalaxyLobbyWithSteamLobbyId`), all mutating the field directly.
- **New behavior:** `SetInviteCodeFromLiveLobby()` runs on the game thread, reads
  `code = Game1.server?.getInviteCode()` (guarded by `_galaxyInitComplete`), and if `code` differs from
  the current mirror: assigns the volatile mirror, writes-or-deletes the file, prints the banner on the
  first non-null (Phase 2g), and emits the Phase 2b transition event. A sibling `WithdrawInviteCode()`
  sets the mirror null, deletes the file, emits `invite_code_withdrawn`.
- **Why:** collapses six mutation sites to one writer/clearer keyed on the actual lobby, eliminating the
  drift class where the field falls out of sync with the live lobby.
- **Edge cases:** `getInviteCode()` is null during the reload window (`Game1.server == null`) — that
  correctly withdraws, and the fresh lobby re-mirrors when it comes up. Native call → game thread only.

**Event hook (primary):** keep the Harmony postfix on `GalaxySocket.GetInviteCode` but route it through
the writer — it fires exactly when the live lobby produces/refreshes a code (lobby-enter, and the mod's
own re-stamp path calls `getInviteCode`). Replace its direct `_galaxyInviteCode = __result` with a call to
`SetInviteCodeFromLiveLobby()` (ignore `__result`; read the live value so there is one source).

**Degraded-only safety net.** In `SteamHelperUpdate_Prefix`'s existing game-thread pump (already gated on
`_galaxyInitComplete`), add one wall-clock-gated block (`DateTime.UtcNow`, ~5s) that runs **only when the
lobby state is uncertain** — `IsGalaxyLobbyConnected() != true`. It calls `SetInviteCodeFromLiveLobby()`
(a local read, no allocation while healthy because it doesn't run while healthy). This shares its single
wall-clock gate with the Phase 3 supervisor (they evaluate `IsGalaxyLobbyConnected()` once per gated tick
and both act on the result), so they never double-run or disagree. No per-second heartbeat; nothing runs
in the healthy steady state.

Call `SetInviteCodeFromLiveLobby()` at the explicit up-transitions: after `TryLateAddGalaxyServer`
succeeds, and after `UpdateGalaxyLobbyWithSteamLobbyId` stamps the relay. Call `WithdrawInviteCode()` at
the down-transitions: `TryRemoveGalaxyServer`, `SteamHelperShutdown_Prefix`, `ModEntry` startup clear.

### 1b. Remove the scattered clears

- **`OnSteamServersLost`:** delete `_galaxyInviteCode = null;` and `InviteCodeFile.Delete(_monitor);`
  (invariant 2 — the actual incident fix). Keep the Steam-lobby-state resets and the in-flight-relogin
  reset; those are correct.
- **`UpdateGalaxyLobbyWithSteamLobbyId`:** remove the temporary `InviteCodeFile.Delete` and the
  file-write/banner block. Its job shrinks to "clear `_steamLobbyPublished`, stamp `SteamLobbyId`, set
  `_steamLobbyPublished = true`," then call `SetInviteCodeFromLiveLobby()`.
- **`GalaxySocket_GetInviteCode_Postfix`:** route through the writer (above), do not mutate the field
  directly.
- **`TryRemoveGalaxyServer` / `SteamHelperShutdown_Prefix`:** route through `WithdrawInviteCode()`.

### 1c. Decouple display from `SteamLobbyPublished`

**File/class:** `InviteCodes`.
- Delete the `Gog` property entirely (invariant 5 — never derive or expose it).
- `Raw`/`Base`/mirror: the mirror now holds the live S-code; `Base` strips the leading `'S'`.
- `Steam => Base != null ? "S" + Base : null` — **drop the `SteamLobbyPublished` gate**. A lobby that
  exists shows its S-code; GOG joins immediately, Steam retries until the stamp lands.
- `Joinable => Steam`.
- Rewrite the class doc comment: the vanilla code is S-prefixed; the mod only ever exposes the S-form;
  `SteamLobbyPublished` is a separate relay-readiness signal, not a display gate.

### 1d. Never surface the G-code, including the readiness gate

- **`ApiService.ServerStatus`:** delete `GogInviteCode`; keep `SteamInviteCode = InviteCodes.Steam`. Fix
  the stale "Derive invite codes from file" comment in `HandleGetStatus` (it reads the static property,
  not the file). Fix the `SteamInviteCode` doc to say the code shows whenever a lobby exists (relay
  readiness is separate).
- **`ServerApiClient.ServerStatus` (test):** delete `GogInviteCode`; change
  `InviteCode => SteamInviteCode ?? GogInviteCode ?? ""` to `SteamInviteCode ?? ""`. This fixes
  `WaitForServerOnline(requireInviteCode: true)` (`WaitForServerOnlineCoreAsync`), which today gates on
  `status.InviteCode` and so passes on the ungated G-code before the S-code exists.
- **`tools/discord-bot/src/serverState.ts`:** drop `gogInviteCode` from the `ServerStatus` type and its
  doc comment. `joinableInviteCode` already reads only `steamInviteCode`.
- **Test/doc fixtures:** remove `gogInviteCode` from `dashboard.test.ts`, `serverState.test.ts`,
  `docs/.vitepress/statusStub.ts`, and the `docs/admins/operations/public-status.md` field list.

**End state after Phase 1:** whenever the Galaxy lobby is alive, every surface shows the S-code within
~1s regardless of Steam-session state; the flap can no longer strand the code; the G-code exists nowhere.

---

## 5. Phase 2 — Observability

### 2a. Connectivity block on `/status`

Add to `ServerStatus` (camelCased on the wire):
- `steamRelayReady: bool` = `GalaxyAuthService.SteamLobbyPublished`.
- `galaxyLobby: "connected" | "recovering" | "down"` — from the supervisor's current phase (§6).
- `steamSession: "connected" | "lost"` — from a new `volatile bool` on `SteamGameServerService` set in
  `OnSteamServersConnected` / `OnSteamServersDisconnected`.
- `authReadiness: "ok" | "expiring" | "unavailable"` (2e).
- `inviteCode` stays the S-code (null when no lobby). Mirror the same fields into the TS `ServerStatus`,
  the docs widget contract, and `public-status.md`.

### 2b. State-transition events

Emit through `Diagnostics.ModEventLog.Emit` (test-only transport, `Env.IsTest`-gated — fine, tests are
the only consumer, and E2E asserts via HTTP not events):
- `invite_code_available { codeShape }` and `invite_code_withdrawn { reason: no_lobby | shutdown }` from
  the single writer/withdraw.
- Keep the existing `auth_galaxy_*` events. Add `galaxy_lobby_recovering` / `galaxy_lobby_recovered` at
  the supervisor's phase transitions (§6). One line per real transition; nothing per tick.

### 2c. Discord

`tools/discord-bot`: when `inviteCode` is present but `steamRelayReady` is false, show the code with a
short note ("Steam relay reconnecting"). When there is no lobby, show the `galaxyLobby`/`steamSession`
reason instead of a bare "not yet available." Display-only; the bot already polls `/status`.

### 2d. `/health` covers joinability — body only, status unchanged

Add the same connectivity summary (`steamSession`, `galaxyLobby`, `steamRelayReady`, `inviteCodePresent`,
`authReadiness`) to the `/health` **body**. **Do not** change the HTTP status code or the `status`
field — those stay driven by game-thread tick liveness, or the Docker `HEALTHCHECK` + `restart:
unless-stopped` + the deploy `grep unhealthy` gate would restart an alive server on a transient Galaxy
blip. A monitor that wants "alive vs joinable" reads the new body fields.

### 2e. Auth-readiness signal

Add a low-frequency (~5 min, wall-clock, off the game thread with `ExecutionContext.SuppressFlow`) poll of
the sidecar `/health` via the existing `SteamAuthApiClient`, caching into a `volatile authReadiness`:
- `unavailable` — sidecar unreachable, or the polled account reports `logged_in == false` with
  `token_days_remaining <= 0` / an "expired" error (a permanent condition an operator must fix).
- `expiring` — `token_days_remaining` within the sidecar's 14-day warning window.
- `ok` otherwise.
Extend `HealthAccount` (in `SteamAuthApiClient`) with `token_expires_at` / `token_days_remaining` so the
mod can classify — the sidecar already emits them. Expose `authReadiness` on `/status` and `/health` body.

### 2f. Steam connect-failure event

`SteamGameServerService.OnSteamServersConnectFailure` only logs today. Add
`steam_session_connect_failure { result, stillRetrying }` (below Error — test-poison rule; the retry
itself is correct). Makes a never-succeeds boot visible in the diagnostic stream.

### 2g. Reasoned replies + banner fix

- **Banner:** `ServerBanner.Print` is once-per-process (`_hasPrinted`) and is called both by the code-up
  path and by `AlwaysOn.PrintBannerAfterDelay` (5s fallback). If the fallback wins first, the banner locks
  to "Invite Code: not yet available" forever. Fix: the fallback prints everything **except** the invite
  line; the invite line is printed once by the writer when the first non-null code lands (allow exactly
  one banner completion for the invite line). Keep IP masking.
- **Commands:** `InviteCodeCommand` / `ServerCommand` — when the code is null, state the reason from the
  connectivity fields ("Galaxy lobby connecting…", "Steam relay reconnecting…"). Switch the
  "server not running" console replies off `LogLevel.Error` (wrong level, and Error is server-side test
  poison) to `Warn`.

**End state after Phase 2:** every surface explains *why* a code is missing or non-working, including
"auth token expiring/dead," with no status-code regression on `/health`.

---

## 6. Phase 3 — Recovery resilience

### The recovery state machine

One supervisor, evaluated on the shared wall-clock gate inside `SteamHelperUpdate_Prefix`'s pump (the same
gate as the 1a safety net). Phases: `Healthy` → `Recovering(vanillaGrace)` → `Recovering(relogin)` →
back to `Healthy`, or → `AuthBlocked`.

**Arming guard (invariant 8).** The supervisor evaluates only when: `_galaxyInitComplete`,
`Game1.server != null`, a save is loaded, and **not** `GameManagerService.IsNewGamePending`. This closes
the reload/newgame window the draft missed — during `ExitToTitle` the server is briefly null and
`IsGalaxyLobbyConnected()` returns null (treated as dead); without this guard the supervisor would fire a
re-login into an in-progress reload.

**Triggers and grace.** When armed and `IsGalaxyLobbyConnected() != true`:
- Enter `Recovering`; set `galaxyLobby = "recovering"`; emit `galaxy_lobby_recovering` once.
- If a `GalaxyNetServer` is still in `servers`, **let vanilla's own 20s `recreateTimer` try first** — wait
  a grace window of ~30s wall-clock (> the 20s vanilla timer, so one full vanilla attempt gets a chance).
- If still down after grace, escalate to `TryBeginGalaxyReSignInGated("supervisor")`. The gate already
  SKIPs when the lobby is connected, so a rebuild can never sever live clients. Retry on **bounded
  wall-clock backoff** (~30s → 60s → 120s cap), not tick counts.
- On recovery (`IsGalaxyLobbyConnected() == true`), return to `Healthy`, set `galaxyLobby = "connected"`,
  emit `galaxy_lobby_recovered`.

This removes the dependence on a Steam reconnect: a Galaxy-only outage or a failed/timed-out re-login
(`PumpGalaxyReLogonWait` give-up branch) now self-heals on the supervisor's own timer.

### 3b. Never strand the server with no Galaxy transport

`ConsumePendingGalaxyReSignIn` removes the `GalaxyNetServer` before re-login; if re-login times out
(`PumpGalaxyReLogonWait` give-up), nothing re-adds it, so **vanilla's recreate can't run either** (no
socket to pump). Fix: on the give-up branch, re-add a fresh `GalaxyNetServer` via `TryLateAddGalaxyServer`
(leaving a present-but-disconnected server vanilla's `recreateTimer` can drive), rather than leaving an
empty servers list. The supervisor's next backoff attempt then has something to work with.

### 3c. Silent Galaxy death — accepted, visible, not faked

If the SDK never fires `onGalaxyLobbyLeft` on a total outage, `lobby` stays non-null,
`IsGalaxyLobbyConnected()` reads `true`, and neither vanilla nor the supervisor arms — the server keeps
advertising a dead code. There is no verified reliable local signal (`IUser` liveness stays stale-`true`).
Per the consistency policy this is the accepted "code shown but may not connect" case. **Do not build a
speculative heartbeat/liveness probe.** Instead: (i) `galaxyLobby` on `/status` makes the state visible;
(ii) leave a concrete TODO in `GalaxyAuthService` to determine empirically (from the existing
`_galaxyAuthLostCount` / `_galaxyStateChangeCount` diagnostics) whether the SDK re-fires the leave
callback before designing any probe.

### 3d. Permanent auth failure vs transient blip

An expired/invalid Steam refresh token is not transient, but today the mod retries it like an outage
(`BeginGalaxyReSignIn`'s fetch fails at Trace; `PumpGalaxyReLogonWait` gives up and "waits for the next
reconnect," which for a dead token never comes). The supervisor keeps retrying on backoff, but after it
classifies the condition as permanent — via the 2e sidecar signal (`logged_in == false` with
`token_days_remaining <= 0`, or an "expired" ticket error) — it sets `authReadiness = "unavailable"`,
emits a structured event, and **stops the tight relogin loop** (drops to a slow keep-checking cadence so a
re-`setup` is still picked up). This surfaces the dead-token condition an operator must fix rather than
retrying into the void.

### 3e. Steam-lobby recreation is thread-safe

`RecreateSteamLobby` (called from `SetSteamLobbyPrivacy` / `SetSteamLobbyData` catch blocks, both inside
`Task.Run`) calls `UpdateGalaxyLobbyWithSteamLobbyId()` **directly off the game thread** — an unsafe Galaxy
SDK call. Fix: after committing the new `_steamLobbyId`, set `_pendingGalaxyLobbyUpdate = true` and let the
game-thread pump apply it (the same deferral the create path uses; the pump is active throughout). While
here: `IsLobbyLostError` is a substring match on `"NoMatch"` (fragile) — leave a TODO to move the sidecar
to a structured error code (out of scope to change the sidecar contract in this PR). Retire
`.claude/plans/bugs/recreate-steam-lobby-thread-safe-galaxy-update.md` when this lands.

**End state after Phase 3:** a Galaxy-only failure self-heals on its own timer; a failed re-login can't
leave the server permanently transport-less; a dead token is surfaced instead of retried forever; the
Steam-lobby recreate path is thread-safe; the one genuinely undetectable case is visible and documented.

---

## 7. Threading model

| Operation | Thread | Reason/constraint |
|---|---|---|
| `Game1.server?.getInviteCode()` (`SetInviteCodeFromLiveLobby`) | Game thread | Native `GetRealID` PINVOKE; reads `Game1.server` |
| Mirror write (`_galaxyInviteCode`, volatile) | Game thread | Written only inside the single writer |
| Mirror read (`/status`, `/health`, banner) | HTTP thread | `volatile` read only; never calls the SDK |
| `InviteCodeFile.Write/Delete` | Game thread (writer) | Filesystem write co-located with the mirror write; `/tmp` |
| `_steamLobbyPublished` r/w | Game thread write, HTTP read | already `volatile` |
| `steamSession` bool r/w | Steam callback write, HTTP read | new `volatile`; set in connect/lost callbacks |
| `UpdateGalaxyLobbyWithSteamLobbyId` (`setLobbyData`) | Game thread via `_pendingGalaxyLobbyUpdate` pump | Galaxy SDK not thread-safe (3e) |
| Supervisor evaluation + `TryBeginGalaxyReSignInGated` | Game thread (`SteamHelperUpdate_Prefix` pump) | reads `Game1.server`, drives SDK |
| Ticket fetch (`BeginGalaxyReSignIn`) | Background `Task.Run` | blocking sidecar HTTP; result consumed on game thread |
| Sidecar `/health` auth poll (2e) | Background `Task.Run` + `SuppressFlow` | blocking HTTP; long-lived; caches to `volatile authReadiness` |
| `authReadiness` read | HTTP thread | `volatile` read |

Wall-clock gates (`DateTime.UtcNow`) are used for the safety net, supervisor grace/backoff, and auth poll,
so cadence is TPS-independent (correct at `SERVER_TPS=5`).

---

## 8. Failure scenarios

- **Normal startup:** lobby comes up → postfix → writer mirrors S-code (GOG-joinable). Stamp lands →
  `steamRelayReady=true` (Steam-joinable), banner prints the invite line once. `galaxyLobby=connected`.
- **Steam-CM flap, Galaxy survives:** `OnSteamServersLost` no longer touches the code (invariant 2);
  `steamSession=lost`, `steamRelayReady=false` momentarily; code stays visible; supervisor sees
  `IsGalaxyLobbyConnected()==true` and does nothing. Connected clients unaffected.
- **Steam reconnect after flap:** `OnServerSteamIdReceived` reconnect branch re-stamps
  (`_pendingGalaxyLobbyUpdate` → pump); `TryBeginGalaxyReSignInGated` SKIPs (lobby alive);
  `steamSession=connected`, `steamRelayReady=true`. Code never changed.
- **Galaxy lobby genuinely disappears, Steam up:** `onGalaxyLobbyLeft` fires → mirror withdrawn on the
  safety-net tick → `galaxyLobby=recovering`. Vanilla recreate (20s) usually restores it within grace →
  `galaxy_lobby_recovered`, new/same code re-mirrored. No Steam event needed.
- **Galaxy re-login succeeds (after grace):** supervisor escalates → `PumpGalaxyReLogonWait` re-adds the
  server + re-stamps → `galaxyLobby=connected`, code re-mirrored.
- **Galaxy re-login times out:** give-up branch re-adds a present-but-disconnected `GalaxyNetServer` (3b);
  supervisor retries on backoff; vanilla recreate can also run again. Server never left transport-less.
- **Persistent invalid/expired auth:** 2e classifies `authReadiness=unavailable`; supervisor drops to
  slow keep-checking; `/status` and `/health` body and Discord show "auth unavailable"; a re-`setup` is
  still picked up. No infinite tight retry.
- **Steam lobby recreation (`NoMatch`):** `RecreateSteamLobby` sets `_pendingGalaxyLobbyUpdate`; pump
  re-stamps on the game thread (3e). Code unaffected; `steamRelayReady` blips false for one tick.
- **Silent Galaxy SDK death:** undetectable locally; `inviteCode` stays shown (GOG may still work),
  `galaxyLobby=connected` (honest to what we can observe). Documented limitation (3c); no fake probe.
- **Server shutdown during recovery:** `SteamHelperShutdown_Prefix` withdraws the code and resets state;
  supervisor guard (server null / not loaded) stops it firing.
- **LAN-only (no `STEAM_AUTH_URL`):** `PerformDeferredInitialization` early-returns, `_galaxyInitComplete`
  stays false, `getInviteCode()` is null; writer, safety net, supervisor, and auth poll all no-op.
  `requireInviteCode:false` on the harness side (already keyed on `WithSteam`).
- **`SERVER_TPS=5`:** all gates are wall-clock; cadence unaffected. Healthy steady state does no per-tick
  invite-code work.

---

## 9. Tests

Prioritize E2E via the HTTP API snapshot; assert via `/status`/`/health`, never mod events. Confirm the
scenario actually ran by reading the container log (a green assertion is not proof the flap skipped the
re-login).

- **Steam + S-code exposure** (extend `FarmhandVisibilityTests` pattern): `/status.inviteCode` starts with
  `'S'` and is non-null once the lobby exists.
- **GOG + S-code:** the harness already redirects Hybrid joins to `GalaxyNetClient`; assert a client joins
  on the S-code (existing `Connect.WithRetryAsync`).
- **Steam flap (regression, Phase 1):** in `GalaxyOutageReproTests`, after a Steam-CM flap where the
  Galaxy lobby survives, assert `/status.inviteCode` stays non-null and the CLI file is present (today it
  is stranded null). Read `containers/server-*/container.log` to confirm the flap skipped the re-login.
- **Galaxy-only outage (Phase 3):** simulate lobby-down-with-Steam-up (test endpoint) and assert the
  supervisor recovers `galaxyLobby` to `connected` without a Steam reconnect.
- **Failed Galaxy re-login (3b):** assert the give-up branch does not leave an empty servers list (a fresh
  `GalaxyNetServer` is present afterward), and recovery still completes on a later attempt.
- **Persistent auth failure (3d):** simulate dead-token / sidecar-down; assert
  `/status.authReadiness == "unavailable"` and the structured event fires, and the relogin loop backs off
  rather than tight-looping.
- **No G-code exposure:** assert `/status` has no `gogInviteCode` field and `inviteCode`/`steamInviteCode`
  never starts with `'G'`.
- **Readiness gating (regression):** confirm `WaitForServerOnline(requireInviteCode:true)` no longer
  passes on a G-code — it waits for the S-code.
- **`/status` connectivity fields:** assert `steamRelayReady=false` during the pre-stamp window and the
  fields flip as expected.
- **`/health` status-code invariant:** assert `/health` returns 200 while the game thread is alive even
  when `galaxyLobby != connected` (the Docker/deploy gate must not restart an alive server).
- **Low TPS:** the outage/recovery tests run at `SERVER_TPS=5` (harness default).
- **LAN mode:** a non-Steam server exposes no invite code and the supervisor/poll no-op.
- **Shutdown/recovery race:** `/newgame` or `/reload` during a `recovering` phase does not fire a re-login
  into the reload (supervisor guard).

**Manual compatibility gate (not automatable):** a real Steam client and a real GOG client each joining
the same live S-code (SDR path is never dialed in the harness). Document as a manual gate.

---

## 10. Documentation changes

- `docs/developers/architecture/steam-auth.md` — rewrite "How Invite Codes Work" (the vanilla code is
  S-prefixed; the mod exposes only the S-form; `SteamLobbyId` stamp = relay readiness, not a display
  gate). Correct any "G-prefixed form the game hands out" wording. Document the new `authReadiness`,
  `galaxyLobby`, `steamSession`, `steamRelayReady` fields.
- `docs/admins/operations/public-status.md` and the docs status-widget contract — drop `gogInviteCode`,
  add the connectivity fields.
- `tools/discord-bot/src/serverState.ts` — `ServerStatus` type + doc comment (drop `gogInviteCode`, add
  the connectivity fields).
- OpenAPI/XML-doc comments on `ApiService.ServerStatus` and the `/health` model (regenerated from source
  via the `docs/assets/openapi.json` extract). Correct the `_galaxyInviteCode` field comment in
  `GalaxyAuthService` (it is the S-code mirror, not "G-prefixed").
- `docs/.vitepress/statusStub.ts`, `dashboard.test.ts`, `serverState.test.ts` fixtures.

---

## 11. Files and methods to change

**Mod/server:**
- `GalaxyAuthService` — new `SetInviteCodeFromLiveLobby()` / `WithdrawInviteCode()`; route postfix,
  up/down transitions through them; strip clears from `OnSteamServersLost` and
  `UpdateGalaxyLobbyWithSteamLobbyId`; supervisor + shared wall-clock gate in `SteamHelperUpdate_Prefix`;
  3b re-add on relogin give-up; 3d auth classification; `RecreateSteamLobby` → `_pendingGalaxyLobbyUpdate`
  (3e); 2e sidecar-health poll + `authReadiness`; fix field comment.
- `InviteCodes` — delete `Gog`; drop the publish gate from `Steam`; rewrite class doc.
- `SteamGameServerService` — `volatile steamSession` bool in connect/lost callbacks; 2f connect-failure
  event.
- `ServerBanner` / `AlwaysOn.PrintBannerAfterDelay` — invite line printed by the writer, not the fallback.
- `InviteCodeCommand` / `ServerCommand` — reasoned replies; drop `LogLevel.Error`.
- `SteamAuthApiClient` — extend `HealthAccount` with token fields.

**API:** `ApiService.ServerStatus` (drop `GogInviteCode`, add connectivity fields), `HandleGetStatus`
(comment fix), `HealthResponse` + `HandleGetHealth` (body-only connectivity fields, status code
unchanged), the TS `ServerStatus`.

**Discord:** `serverState.ts` type; `dashboard.ts` / `index.ts` reason display.

**Tests:** `ServerApiClient` (drop `GogInviteCode`, `InviteCode` → Steam only); `GalaxyOutageReproTests`
(flap regression + Galaxy-only + failed-relogin + auth-failure); readiness-gate test; `/status` +
`/health` field tests; fixtures.

**Docs:** as §10.

**Retire:** `.claude/plans/bugs/recreate-steam-lobby-thread-safe-galaxy-update.md` (folded into 3e);
this plan when its code lands. Note the new supervisor/writer surface for
`.claude/plans/refactor/split-authservice-godclass.md` (do the split after this ships).

---

## 12. Implementation order

1. **Phase 1 first, as the incident fix:** single writer/withdraw, strip the clears, decouple `Steam`
   display, drop `Gog`/`GogInviteCode` end to end, fix `ServerApiClient.InviteCode`. Land with the
   flap-regression and readiness-gate tests green. This alone stops the incident and is independently
   shippable/reviewable.
2. **Phase 2** on top: connectivity fields on `/status` and `/health` (body-only), `steamSession` bool,
   transition events, banner fix, reasoned replies, auth-readiness poll, connect-failure event, Discord
   display.
3. **Phase 3** last: supervisor + shared gate (with the arming guard), 3b re-add, 3d auth escalation, 3e
   thread-safe recreate. Land with the Galaxy-only, failed-relogin, and auth-failure tests, and the
   `/health` status-code invariant test.
4. Docs updated in lockstep with each phase; retire the folded plan when 3e lands.

Each phase leaves the tree green and the invariants (§2) intact, so intermediate commits are testable.

---

## 13. Final acceptance criteria

- [ ] A Steam-CM flap with the Galaxy lobby surviving leaves `/status.inviteCode` non-null and the CLI
      file present; the container log confirms the re-login was skipped.
- [ ] Every exposed code starts with `'S'`; no `gogInviteCode` field exists anywhere; no surface can show
      a `'G'` code.
- [ ] `/status` reports `inviteCode`, `galaxyLobby`, `steamSession`, `steamRelayReady`, `authReadiness`;
      `/health` carries the same in its body and still returns 200 while the game thread is alive.
- [ ] `WaitForServerOnline(requireInviteCode:true)` waits for the S-code, never passing on a G-code.
- [ ] A Galaxy-only outage recovers `galaxyLobby` to `connected` with no Steam reconnect; a failed
      re-login never leaves an empty servers list.
- [ ] A dead/expired token surfaces `authReadiness=unavailable` + event and backs off instead of
      tight-looping.
- [ ] `RecreateSteamLobby` applies the Galaxy update only via the game-thread pump.
- [ ] The banner never locks to "not yet available" once a code exists.
- [ ] Steady-state healthy operation does no per-tick game-thread allocation for invite-code upkeep; all
      recovery cadences are wall-clock and correct at `SERVER_TPS=5`; LAN no-ops.
- [ ] Docs, OpenAPI, TS type, and the corrected `_galaxyInviteCode` comment all describe the S-code model.
- [ ] `recreate-steam-lobby-thread-safe-galaxy-update.md` is retired; the god-class split plan notes the
      new surface.
