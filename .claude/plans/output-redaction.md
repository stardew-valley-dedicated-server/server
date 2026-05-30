# Redact Steam credentials in logs (default-on, env-disable)

## Context

When the test harness boots on a remote runner, `SharedSteamAuth.AssertNoSteamAccountConflictAsync` probes existing containers for Steam-account collisions. On collision it throws an `InvalidOperationException` whose message contains the **full Steam username(s)** — that exception text lands in test output and CI logs, where it's freely copy-pasted into bug reports, PRs, and Slack. Same exposure exists in the steam-service container's logs (and in its `/steam/ready` HTTP response), which run inside the test harness on remote runners _and_ on operators' production machines.

User goal: any user must be able to **copy a log file or test-failure dump anywhere without leaking credentials**. Redaction is **on by default**, controlled by a single env var (`SDVD_REDACT_SECRETS`), and can be disabled per-run when an operator legitimately needs the cleartext (e.g. debugging a specific account locally).

In scope (verified leaks of Steam usernames in user-visible output):

- `tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs:386-393` — preflight conflict throw (the immediate trigger).
- `tools/steam-service/SteamAuthService.cs:307,462,481,617,662` — `Logger.Log` / `Console.WriteLine` of `username` / `_username`.
- `tools/steam-service/Program.cs:196,245` — `Logger.Log` of `svc.Username` during account setup/login.
- `tools/steam-service/Program.cs:453` — `username = svc.Username` in `/steam/ready` JSON. **Confirmed unread**: `tests/test-client/Auth/SteamAuthClient.cs:130` deserializes the field but no code in the repo reads it (`grep readyResponse.username` and `\.username` in `tests/test-client` returned nothing). Per `simplest-solution.md`, this is dead infrastructure — **delete the field**, don't redact.

Confirmed _not_ leaks (no change needed):

- `mod/JunimoServer/` — production mod never logs Steam usernames; the `!login <password>` chat path is already redacted by `ChatController.MaskSecrets`.
- `tests/test-client/Auth/ClientAuthService.cs:315` — logs `account {index}` (e.g. `account 0`), never the username.
- `session.json` (refresh-token at-rest) — file-system concern; out of scope for log redaction.
- IP addresses — no plaintext IP leaks in logs (only `tools/netdebug/` handles IPs and already has `RedactPublicIp`).

Critical correctness constraint: `SharedSteamAuth.cs:443` (`ExtractUsernamesFromEnv`) and `SharedSteamAuth.cs:380-381` (`overlap = planned.Intersect(existing)`) compare usernames internally — these comparisons **must use the cleartext value**. Redaction happens only at the emit site, never at the storage/comparison layer. Same shape as `MaskSecrets` in `tests/test-client/GameControl/ChatController.cs:115` (existing pattern in this codebase).

## Design

### The redaction helper

**Signature**: `public static string Redact(string? value)` — returns non-null. Callers can interpolate without null-checks.

**Format** (per user choice): first char + 8 fixed asterisks + last char. Fixed asterisk count hides the original length, which is itself a small leak. Edge cases:

- `null` / empty → return `""`.
- `len == 1` → `"*"`.
- `len == 2` → `"**"`.
- `len >= 3` → `first + "********" + last`. (e.g. `"jdoe"` → `"j********e"`, `"my_test_account"` → `"m********t"`.)

**Toggle**: env var `SDVD_REDACT_SECRETS`.

- **Default (unset, empty, or any value other than the explicit-disable list)**: redact (safe-by-default).
- Explicit-disable values (case-insensitive): `false`, `0`, `no`, `off`. Anything else (including `true`, `1`, `yes`, garbage) → redact.

Asymmetric parsing (only "explicit disable" disables) is the safe direction: a typo or unfamiliar value never accidentally leaks.

The toggle is read once per process at first reference and cached in a `static readonly bool` (no per-call env lookup overhead). Matches the existing pattern in `tests/JunimoServer.Tests/Helpers/DockerImageBuilder.cs:37` (`Equals("true", StringComparison.OrdinalIgnoreCase)`).

### Why one helper duplicated in two projects, not a shared assembly

The two emit-site projects target different runtimes — `JunimoServer.Tests` and `SteamService` are both net10.0, but `JunimoServer.Shared` is net6.0 (game-bound), so it can't host a helper consumed by net10.0 callers. The helper is ~25 lines including the env-toggle. Per `simplest-solution.md`, two identical small files beat introducing an unprecedented cross-project `<Compile Include>` (no such pattern exists today — confirmed via `grep '<Compile Include=' *.csproj`). The duplication is a single static class with one public method; drift risk is negligible.

**Files added**:

- `tests/JunimoServer.Tests/Helpers/SecretRedactor.cs` — namespace `JunimoServer.Tests.Helpers`.
- `tools/steam-service/SecretRedactor.cs` — top-level (matches `Logger.cs` convention).

Both expose `public static string Redact(string? value)` and read `SDVD_REDACT_SECRETS` once at startup.

### Edits to call sites

**`tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs`**

At line 385 (and 391's repeat), redact each username before joining:

```csharp
var overlapList = string.Join(", ", overlap.Select(SecretRedactor.Redact));
```

The error message is otherwise unchanged — the operator still sees container name, image, host id, and the full operator-action sentence ("remove [...] from the test's STEAM_ACCOUNTS env"). With redaction off, behavior is identical to today.

**`tools/steam-service/SteamAuthService.cs`** — wrap each interpolated username:

| Line | Before                                                                                  | After                                                                 |
| ---- | --------------------------------------------------------------------------------------- | --------------------------------------------------------------------- |
| 307  | `$"{_logPrefix} Session saved for {username}"`                                          | `$"{_logPrefix} Session saved for {SecretRedactor.Redact(username)}"` |
| 462  | `$"{_logPrefix} Logging in with provided token for {username}..."`                      | `$"...for {SecretRedactor.Redact(username)}..."`                      |
| 481  | `$"Found existing session for: {existingSession.Value.username}"`                       | `$"...: {SecretRedactor.Redact(existingSession.Value.username)}"`     |
| 617  | `$"{_logPrefix} Authenticated as {_username}"`                                          | `$"...as {SecretRedactor.Redact(_username)}"`                         |
| 662  | `$"{_logPrefix} Logging in as {_username} with token ({refreshToken.Length} chars)..."` | `$"...as {SecretRedactor.Redact(_username)} with token..."`           |

**`tools/steam-service/Program.cs`** — same pattern:

| Line    | Before                                                                              | After                                                             |
| ------- | ----------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| 196     | `$"[SteamService] --- Account {idx}: {svc.Username} ---"`                           | `$"... {idx}: {SecretRedactor.Redact(svc.Username)} ---"`         |
| 245     | (identical to 196)                                                                  | (identical)                                                       |
| 145     | `$"[SteamService] A{svc.AccountIndex}: No authentication method for {config.user}"` | `$"...for {SecretRedactor.Redact(config.user)}"`                  |
| **453** | `username = svc.Username,` (in `/steam/ready` JSON)                                 | **delete the field entirely** — confirmed unread by any consumer. |

**`tools/steam-service/Program.cs:100`** is already safe ("Duplicate Steam usernames in STEAM_ACCOUNTS" — no actual usernames emitted). No change.

### What stays cleartext

- `_username` field internally, env-var values, `session.json` contents, `LoginConfig` arguments, `Username` getter on `SteamAuthService` — all internal data flows.
- Config in `.env`, `docker-compose.yml`, GitHub Actions `secrets.STEAM_USERNAME` — these are config files, not logs.
- The `username` returned by `LoadSession()` and `GetSavedSession()` (used internally for relogin).
- `steam_id` in the `/steam/ready` JSON response. SteamID64 is a publicly-resolvable identifier (Steam profile lookups), not a credential. Keep as-is. Same for the `account` integer slot index.

The contract: **redaction at the emit boundary, cleartext everywhere else**.

### Caught exception messages (defensive note)

Lines like `Logger.Log($"... {ex.Message}")` (e.g. `SteamAuthService.cs:325`, `Program.cs:460`) could in principle leak a username if SteamKit2 embeds one in its exception text. This is unverified and unlikely (SteamKit's exceptions are typed protocol errors, not formatted user strings). Out of scope for this change — but if a future log dump shows a username inside an exception message, redact at that emit site too rather than reaching for a global log filter. The principle is unchanged: redact at the boundary, not in transit.

### Documentation

Add a short row to `.env.test.example` (after the existing test-config block) and `.env.example` (after the credential block) so operators discover the toggle:

```
# Redact Steam usernames in logs (default: true).
# Set to false only when debugging a specific account; logs may then contain credentials.
# SDVD_REDACT_SECRETS=true
```

Per `verify-documented-config-is-consumed.md`, the env var has consumers (the two `SecretRedactor.cs` files) before the doc lines are added.

## Critical files

- `tests/JunimoServer.Tests/Helpers/SecretRedactor.cs` (new, ~25 lines)
- `tools/steam-service/SecretRedactor.cs` (new, ~25 lines, identical body)
- `tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs` (edit line 385)
- `tools/steam-service/SteamAuthService.cs` (edit lines 307, 462, 481, 617, 662)
- `tools/steam-service/Program.cs` (edit lines 145, 196, 245; **delete** line 453)
- `.env.test.example`, `.env.example` (add doc line)

## Verification

Per `runtime-post-conditions-are-gates.md`, these are runtime checks, not static-review claims:

1. **Build**: `dotnet build tools/steam-service/SteamService.csproj` and `dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` both green.

2. **Unit-shape check (interactive REPL or scratch test)**: confirm `SecretRedactor.Redact("jdoe")` → `"j********e"`, `Redact("ab")` → `"**"`, `Redact("")` → `""`, `Redact(null)` → `null`/`""`. Confirm `SDVD_REDACT_SECRETS=false` makes `Redact("jdoe")` → `"jdoe"`.

3. **Preflight throw — runtime gate**: trigger the conflict path. Easiest repro: start a non-test `sdvd/steam-service` container with `STEAM_USERNAME=conflicttest` on the local Docker host, then `make test FILTER=<any class that uses Steam>`. The `InvalidOperationException` message in test output must contain `[c********t]`, **not** `[conflicttest]`. Then re-run with `SDVD_REDACT_SECRETS=false` and confirm the cleartext appears.

4. **steam-service container logs — runtime gate**: `make test FILTER=...` then read `{RunDir}/containers/steam-auth-shared/container.log` (path defined in `tests/JunimoServer.Tests/Containers/ContainerLogFile.cs:23` via `TestArtifacts.GetContainerDir`). Confirm any `Authenticated as`, `Session saved for`, `--- Account N: ...`, or `Logging in as` lines show the redacted form. With `SDVD_REDACT_SECRETS=false`, same lines should show cleartext.

5. **HTTP `/steam/ready` field deletion — runtime gate**: after `make test FILTER=...`, find an active steam-service container with `docker ps --filter label=sdvd.test=true` and `curl` its `/steam/ready` endpoint, OR enable a `_monitor.Log` of the raw response in `ClientAuthService.cs:298-315` for one run. Confirm the JSON has no `username` field. `account` (integer) and `steam_id` (SteamID64) remain.

6. **No regressions in conflict detection**: the conflict-comparison logic (`overlap = planned.Intersect(existing)`) compares cleartext. Adding a deliberate `STEAM_USERNAME=foo` collision must still produce a throw with redaction enabled.

If step 3 or 4 shows cleartext, the redaction site was missed — re-grep with `Grep "{_username}|{username}|{svc\.Username}|{config\.user}"` across `tools/steam-service` and `tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs` to find the missed interpolation.
