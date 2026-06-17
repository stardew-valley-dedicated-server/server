# Redact user PII at the source in production logs

## Context

A user must be able to copy their dedicated-server logs and paste them into Discord for support **without leaking their own (or their players') credentials and PII**. Today that's unsafe: SMAPI's stdout — which carries every mod `IMonitor.Log` — is captured to `/tmp/server-output.log` and surfaced via `docker compose logs`, and the docs explicitly tell operators to share that output (`docs/admins/troubleshooting.md:302`, `docs/community/getting-help.md:33`, `docs/community/reporting-bugs.md:24`). The steam-service sidecar's stdout leaks the same way via `docker compose logs steam-auth`. Neither path has any redaction.

This plan replaces an earlier draft that got two things wrong for this goal:
1. It was framed around the *test-harness* trigger (the `SharedSteamAuth` preflight throw), with production as a side note. The real target is production operator logs.
2. It reached for **masking as the default**. The better question is *do we need to print the sensitive value at all?* For most sites the account index / SteamID / farmer-slot already carries the operational meaning, so a masked value is strictly worse (leaks length-class, identifies nothing actionable).

The test-report path is already handled by `ReportRedactor.Scrub` (a downstream scrub of the published E2E report + CI setup-error logs) and is **out of scope** here — this plan is *source-level* redaction in the live production code paths, not a scrub pass.

**Decision order per emit site** (applied in the tables below):
1. **Drop** — does the surrounding context already carry a safe identifier (slot index, farmer id, account index)? Then remove the sensitive value.
2. **Substitute** — replace with a non-secret identifier already in scope.
3. **Mask** — only where the value itself is the diagnostic point and can't be substituted, via the existing `ChatRedaction` helpers.

User-confirmed calls baked in:
- **SteamIDs → mask with existing `ChatRedaction.MaskValue`** (no new `MaskSteamId` helper — keep it simple).
- **Invite codes → mask in *passive*/lifecycle logs, keep cleartext in the explicit `InviteCodeCommand`** (that command's purpose is to print the code on demand). Lobby IDs are pure session metadata with no operator read-need.

## Scope

Two production logging surfaces:
- **`mod/JunimoServer/`** (net6.0 SMAPI mod) — uses the existing `ChatRedaction` helpers (`mod/JunimoServer.Shared/ChatRedaction.cs`).
- **`tools/steam-service/`** (net10.0 standalone sidecar) — cannot reference `ChatRedaction` (net6.0 + game-DLL project, confirmed `JunimoServer.Shared.csproj:3,14`), so it gets a small duplicated redactor, following the **already-accepted** `ChatRedaction` ↔ `ReportRedactor` duplication precedent (both carry a "keep in sync" comment). Not introducing a new shared netstandard project — neither existing file built one despite wishing for it; `simplest-solution.md` / pattern-continuity says match the precedent.

## Existing infrastructure to reuse

`mod/JunimoServer.Shared/ChatRedaction.cs` (read in full):
- `MaskValue(string)` → `first***last`; `≤2 chars` → `***`. Use for SteamIDs, invite codes, names not already masked.
- `MaskIp(string)` → `***.***.***.last`. Already used at `ServerBanner.cs` / `SteamGameServerService.cs:187`.
- `MaskSecrets(string)` → redacts `!login <password>`. Already used in chat paths.

Canonical mask format across the repo is `first***last` / `***.***.***.last` — the new sidecar redactor must match it.

## Part A — `mod/JunimoServer/` emit sites

Verified sites (file:line confirmed by direct read). Treatment column is the decision.

| File:Line | Current emit | Sensitive field | Treatment |
| --- | --- | --- | --- |
| `Services/SteamGameServer/SteamGameServerNetServer.cs:273` | `Farmhand request from {SteamId.m_SteamID} for {farmerId}` | SteamID64 | **Mask** SteamID via `MaskValue`; keep `farmerId` |
| `…NetServer.cs:279` | `Accepted {SteamId.m_SteamID} as farmhand {farmerId}` | SteamID64 | **Mask** SteamID; keep slot |
| `…NetServer.cs:390` | `{steamId.m_SteamID} connecting via SDR` | SteamID64 | **Mask** |
| `…NetServer.cs:398` | `{steamId.m_SteamID} is banned` | SteamID64 | **Mask** |
| `…NetServer.cs:415` | `{steamId.m_SteamID} connected via SDR` | SteamID64 | **Mask** |
| `…NetServer.cs:439` | `{steamId.m_SteamID} disconnected` | SteamID64 | **Mask** |
| `Services/Roles/RoleService.cs:170` | `Auto-promoted player {playerId} (Steam ID: {steamId}) to admin` | SteamID64 | **Mask** the `steamId`; keep `playerId` |
| `Services/AuthService/AuthService.cs:1026` | `Galaxy invite code generated: {__result}` | invite code (live join secret) | **Mask** via `MaskValue` (passive lifecycle log) |
| `Services/AuthService/AuthService.cs:445` | `Steam lobby created via HTTP: {_steamLobbyId}` | lobby ID (session metadata) | **Mask** via `MaskValue` |
| `Services/AuthService/AuthService.cs:512` | `Galaxy lobby updated with SteamLobbyId: {_steamLobbyId}` | lobby ID | **Mask** |
| `Services/AuthService/AuthService.cs:580` | `Steam lobby recreated: {_steamLobbyId}` | lobby ID | **Mask** |
| `Services/Commands/ServerCommand.cs:106` | `  Invite Code: {inviteCode}` (info dump) | invite code | **Mask** — operator didn't ask for the code here, it's a status dump |
| `Services/Commands/SavesCommand.cs:113` | `  Farm Name:  {info.FarmName}` | farm name (may be real name) | **Mask** via `MaskValue` |
| `Services/Commands/SavesCommand.cs:118` | `  Players:    {string.Join(", ", info.PlayerNames)}` | player display names | **Mask** each name via `MaskValue` before join |
| `Services/Commands/SettingsCommand.cs:138` | `  Farm Name:        {config.FarmName}` | farm name | **Mask** |
| `Services/NetworkTweaks/NetworkTweaker.cs:272` | `[SaveFarmhand] Fixing isCustomized for '{Name}' ({UniqueMultiplayerID})` | player name | **Mask** the `Name` (UniqueMultiplayerID is an internal long, keep) |

**Kept cleartext (with reason):**
- `Services/Commands/InviteCodeCommand.cs:58` — `Invite code: {inviteCode}`. This command's sole purpose is to print the code on demand; masking defeats it. **No change.**
- `…NetServer.cs:286` — `FarmhandAccepted?.Invoke(…, m_SteamID.ToString())` is an *event-handler data flow*, not a log emit; consumed internally. **No change.**
- `Services/Api/ApiService.cs:3473,3557` — already `MaskValue(farmer.Name)`; the trailing `UniqueMultiplayerID` is an internal long (not a platform ID), low-sensitivity. **No change** (note in PR; revisit only if we decide internal IDs are sensitive).

Already-correct reference sites (no change, cited for consistency): `PasswordProtectionService.cs:310/457/796/847`, `CabinManagerService.cs:199/205`, `DesyncKicker.cs:115`, `ChatCommands.cs:133`, `SteamGameServerService.cs:187`.

## Part B — `tools/steam-service/` emit sites

Add `tools/steam-service/SecretRedactor.cs` — one `public static string Redact(string?)` matching `ChatRedaction.MaskValue`'s `first***last` format (top-level `SteamService` namespace, matches `Logger.cs` convention). Header comment names `mod/JunimoServer.Shared/ChatRedaction.cs` as the source-of-truth to keep in sync (mirrors the `ReportRedactor` precedent).

Log/console emits that reach the shareable container log:

| File:Line | Current emit | Treatment |
| --- | --- | --- |
| `SteamAuthService.cs:309` | `Session saved for {username}` | **Mask** `username` |
| `SteamAuthService.cs:464` | `Logging in with provided token for {username}...` | **Mask** |
| `SteamAuthService.cs:483` | `Found existing session for: {existingSession.Value.username}` (Console.WriteLine, interactive) | **Mask** |
| `SteamAuthService.cs:619` | `Authenticated as {_username}` | **Mask** |
| `SteamAuthService.cs:664` | `Logging in as {_username} with token (...)` | **Mask** |
| `Program.cs:153` | `No authentication method for {config.user}` | **Mask** `config.user` |
| `Program.cs:204` | `--- Account {idx}: {svc.Username} ---` (setup) | **Drop** the username — the `idx` already labels the account; or **substitute** to `A{idx}`. (Username adds nothing the index doesn't.) |
| `Program.cs:253` | `--- Account {idx}: {svc.Username} ---` (login) | **Drop**/substitute, same as above |

**HTTP-response JSON — not the log file, but one dead field to delete:**
- `Program.cs:461` — `/steam/ready` returns `username = svc.Username`. **Confirmed dead**: `SteamAuthClient.ReadyResponse.username` (`tests/test-client/Auth/SteamAuthClient.cs:130`) is deserialized but never read — `ClientAuthService.cs:314-315` reads only `.steam_id`/`.account`. **Delete the field** (per `simplest-solution.md`, don't redact dead infrastructure). Also remove the now-unused `username` property from `SteamAuthClient.ReadyResponse`.
- `Program.cs:434` (`/health`), `Program.cs:507` (`/steam/refresh-token`), `Program.cs:310` (`export-token`) — HTTP responses an operator fetches **deliberately** (and refresh-token/export-token already return the token itself). **Keep cleartext** — these are not log output and the caller asked for them.

**Internal data flows — must stay cleartext** (storage / comparison / login args): `SaveSession` JSON write (`:304`), `_username`/`username` assignments (`:449,461,536,555,615`), `LoadSession`, `MigrateOldSession` paths, the `Username` getter. Redaction is at the emit boundary only.

## No toggle

No `SDVD_REDACT_SECRETS` env var. With drop/substitute-first, most sites stop printing the secret entirely — there's nothing to "un-mask". An operator who genuinely needs a cleartext value reads `session.json` or the deliberate HTTP endpoints. A toggle is one more documented knob that can be misconfigured into leaking; omitting it is safer and simpler.

## Test-side note (out of scope, already covered)

`tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs:388` joins cleartext usernames into the preflight-conflict throw. That message reaches output only through the **test runner**, where `ReportRedactor` + `CollectKnownSecrets` (`TestRunner/Program.cs:755`) already masks known Steam usernames. **No change** — adding emit-site redaction there would duplicate coverage that exists. (Flagged so a reviewer doesn't think it was missed.)

## Critical files

- **New**: `tools/steam-service/SecretRedactor.cs` (~10 lines, `Redact` mirroring `MaskValue`).
- **Edit**: `mod/JunimoServer/Services/SteamGameServer/SteamGameServerNetServer.cs`, `Services/Roles/RoleService.cs`, `Services/AuthService/AuthService.cs`, `Services/Commands/{ServerCommand,SavesCommand,SettingsCommand}.cs`, `Services/NetworkTweaks/NetworkTweaker.cs` (all via `ChatRedaction.MaskValue`).
- **Edit**: `tools/steam-service/SteamAuthService.cs`, `tools/steam-service/Program.cs`.
- **Edit**: `tests/test-client/Auth/SteamAuthClient.cs` (remove dead `username` property after the field is deleted server-side).
- **Docs**: add a one-line "logs are redacted by default; do a final visual scan before sharing" reassurance to `docs/admins/troubleshooting.md` near the "collect logs" step and `docs/community/reporting-bugs.md`. (No new env var to document.)

## Verification

Per `runtime-post-conditions-are-gates.md` these are runtime gates, not static-review claims. The mod side requires a full E2E build; the sidecar side is cheaply checkable in isolation.

1. **Sidecar builds**: `dotnet build tools/steam-service/SteamService.csproj` green. **Mod builds**: `dotnet build mod/JunimoServer/JunimoServer.csproj` green.
2. **`SecretRedactor.Redact` shape**: confirm `Redact("jdoe")` → `"j***e"`, `Redact("ab")` → `"***"`, `Redact("")`/`Redact(null)` → unchanged — matches `MaskValue`.
3. **Sidecar log — runtime gate**: `make test FILTER=<a Steam test>` then read `{RunDir}/containers/steam-auth-shared/container.log`. Confirm `Session saved for`, `Authenticated as`, `Logging in as`, and `--- Account N: …` lines show **no cleartext username** (masked, or the username dropped in favor of `A{idx}`).
4. **Mod log — runtime gate**: in the same run, read a server `container.log`; confirm connection lines (`connecting via SDR`, `connected via SDR`, `disconnected`, `Accepted … as farmhand`) show **masked SteamIDs** (`7***4`), and any invite-code/lobby-id lifecycle line is masked. Confirm `InviteCodeCommand` output (if exercised) is still **cleartext**.
5. **Dead field deletion — runtime gate**: `curl` a steam-service `/steam/ready` (find via `docker ps --filter label=sdvd.test=true`); confirm the JSON has **no `username`** field; `account` + `steam_id` remain. Confirm the test suite still passes (proves `ReadyResponse.username` removal broke no consumer).
6. **No regression in conflict detection**: the `overlap = planned.Intersect(existing)` comparison in `SharedSteamAuth.cs` operates on cleartext (redaction is emit-only), so a deliberate `STEAM_USERNAME` collision must still throw.

If step 3 or 4 shows cleartext, a site was missed — re-grep `{[^}]*[Uu]sername|m_SteamID|inviteCode|FarmName|\.Name\}` across the two scopes for the missed interpolation.
