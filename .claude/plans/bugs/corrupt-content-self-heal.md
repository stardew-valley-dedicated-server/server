# Boot-time validation: self-heal corrupt game content

**Status:** validation
**Priority:** 2 (medium)
**GitHub Issue(s):** none
**Area:** steam-service, server
**Related:** none
**Observed:** production report (`Failed to spawn NPC 'Vincent'` / invalid XNB); recurs every boot until repaired by hand
**Next step:** design sign-off on trigger shape B and the four open decisions below

## Symptom

Production reported:

```text
[01:39:58 ERROR game] Failed to spawn NPC 'Vincent'.
Microsoft.Xna.Framework.Content.ContentLoadException: Asset does not appear to be a valid XNB file.
```

`"Asset does not appear to be a valid XNB file"` is the XNB **parse** error: the file exists, but its contents are invalid (for example, truncation, a partial write, or bitrot).

A missing file produces `"content file was not found"` instead and is handled cleanly by the game's manifest guard (`LocalizedContentManager.DoesAssetExist`), which is kept in sync by `PruneContentManifest`.

Therefore this incident class is **corrupt-on-disk content**, not missing content. Without repair, the same corrupt file can fail every boot.

## Root cause

The repair mechanism already exists.

`DownloadGameAsync` (`tools/steam-service/SteamAuthService.cs`) always chunk-hash-validates existing files:

* the `"Always validate files to detect corruption/deletion"` pass runs on every download;
* invalid chunks are re-downloaded.

The chunk-hash check itself (`ChunkValidator`) is unit-tested in `tests/SteamService.Tests`; CI's pre-suite `steam-auth download` into an empty volume exercises only the fresh-download path — the existing-file repair branch in `DownloadGameAsync` (missing or corrupt files among an otherwise-complete install) has no automated test.

The missing piece is the **boot-time trigger**.

The server entrypoint (`init_stardew` in `docker/rootfs/startapp.sh`) currently early-returns when the game executable exists and otherwise only polls for files to appear. It never validates existing files.

Downloads therefore happen only through explicit `make setup` / `SteamService.dll download`.

The server entrypoint also has no Steam session. The steam-auth sidecar owns the logged-in account needed to obtain the depot manifest and validate/repair the content. Therefore the automatic trigger must live in, or go through, the sidecar.

## Fix

There are two plausible trigger shapes.

### A. Validate inside `serve` startup, before binding HTTP

This is the simplest implementation. The compose dependency:

`server` → `steam-auth: service_healthy`

would naturally hold the server back while validation runs.

The problem is the steam-auth image's healthcheck (`HEALTHCHECK` in `tools/steam-service/Dockerfile`): `GET /health` allows roughly 10s of start period plus 3 × 30s retries before the container is considered unhealthy.

A full chunk-hash pass over roughly 500 MB can exceed that budget on a slow disk. A blocking validation pass would therefore race the healthcheck and could cause the dependent server container to fail even though repair itself is healthy.

It also makes every `serve` start pay the validation cost, including test sidecars that may not need it.

### B. Background validation + readiness gate — preferred

`serve` binds HTTP first so the healthcheck remains responsive, then starts the validate/repair pass as a background task and exposes its state.

The state could be exposed through a dedicated endpoint such as:

`GET /game/validate-status`

or as a field on an existing endpoint.

Do **not** make `/health` trigger validation or login. `/health` is deliberately a pure status probe (the `/health` handler in `tools/steam-service/Program.cs`) and should remain cheap and side-effect-free.

`init_stardew` then gains a **wait-for-validated** step before its current early-return. A restart with corrupt-but-present files therefore blocks the game from starting until validation/repair has completed.

The gate is a correctness requirement, not merely startup ordering: validation can rewrite files in place, so the game must not begin reading the shared game volume concurrently with repair.

The resulting sequence is:

```text
steam-auth starts
    ↓
HTTP /health becomes healthy
    ↓
serve starts validation/repair in background
    ↓
init_stardew waits for validation state
    ↓
validation completes successfully
    ↓
server starts/uses game content
```

## Verification

TODO: add an E2E test that exercises the actual boot path. Steam allows one live login per account, so the test must not log in on its own; instead it borrows what the broker already holds:

* lease a client account from `SteamAccountAllocator` like any `[TestServer(WithSteam=true)]` test;
* ask the host's existing `SharedSteamAuth` sidecar to run the download/repair against a scratch copy of the game volume with that account (new sidecar HTTP endpoint wrapping the `download` command with a target dir and slice-local account index — none exists yet);
* release the lease afterwards.

No account is reserved; the cost is one client lease for ~2-3 min per run, dominated by the full-file chunk hash over the ~500 MB install (the branch under test is "validate everything, redownload what is bad").

Steps:

1. corrupt an XNB in the scratch game volume;
2. restart the relevant services;
3. allow boot-time validation to run;
4. assert that the file is repaired;
5. assert that the server proceeds past `init_stardew`.

Also verify the failure policy:

* run validate-on-boot with no usable Steam session;
* assert that validation logs a warning;
* assert that the validation gate is released;
* assert that the server still proceeds to boot.

Finally, confirm that the steam-auth healthcheck remains healthy throughout a full validation pass. This is particularly important for design B because validation intentionally runs after HTTP becomes available.

## Open decisions

### 1. Cost bound

Measure the chunk-hash pass over the production ~500 MB game directory on the production VPS.

* If it completes in seconds, run validation unconditionally on boot.
* If it takes tens of seconds or more, consider an environment gate such as `VALIDATE_ON_BOOT`, defaulting to enabled in `docker-compose.yml`.

Per `verify-claims`, do not document or rely on the knob until it is actually wired.

### 2. Test/CI sidecars

E2E infrastructure starts steam-auth containers per run (`tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs`).

Decide whether those containers should:

* disable the boot pass through an environment setting; or
* accept the per-boot validation cost.

This should be an explicit decision rather than an accidental side effect of the production implementation.

### 3. Login failure policy

If account 0 cannot log in — for example, because of an expired token or Steam being unavailable — validation cannot obtain the depot manifest.

The server must **not block boot indefinitely waiting for Steam**.

Instead:

1. log a warning;
2. release the validation gate;
3. allow the server to boot, even though corrupt content may remain;
4. fall back to the existing operator runbook.

This preserves availability while making the automatic repair best-effort when Steam itself is unavailable.

Non-login failures (malformed manifest, disk I/O error, cancellation, unhandled task exception) need the same treatment: the background task must always reach a terminal state that releases the gate, so `init_stardew` cannot wait indefinitely. Decide per failure class whether it releases the gate (boot best-effort) or fails the server, and cover a non-login exception in the tests.

### 4. Validation scope

`download` also fetches the Steamworks SDK depot (`DownloadAllAsync` in `tools/steam-service/Program.cs`).

Decide whether boot-time validation should cover:

* both depots; or
* only the game depot.

The incident class being addressed is corrupt game content, so validating only the game depot may be the narrower and cheaper choice.

## Related files

| File                                                  | Role                                                                                    |
| ----------------------------------------------------- | --------------------------------------------------------------------------------------- |
| `tools/steam-service/SteamAuthService.cs`             | `DownloadGameAsync` — existing chunk validation and repair                              |
| `tools/steam-service/SteamAuthService.cs`             | `PruneContentManifest` — manifest/filesystem synchronization                            |
| `tools/steam-service/Program.cs`                      | `serve` → `RunHttpServerAsync`; existing endpoints including pure-probe `/health`       |
| `docker/rootfs/startapp.sh`                           | `init_stardew` — current early-return/wait loop; boot validation gate belongs here      |
| `docker-compose.yml`                                  | `server` depends on `steam-auth: service_healthy`; shared `game-data:/data/game` volume |
| `tools/steam-service/Dockerfile`                      | steam-auth `HEALTHCHECK` and its startup budget                                         |
| `tests/SteamService.Tests/ChunkValidatorTests.cs`      | Unit coverage of the chunk-hash check                                                   |

## Sidenote: operator runbook (shipped)

Manual repair is already documented in `docs/community/faq.md` under the `"Asset does not appear to be a valid XNB file"` entry.

The documented recovery is to rerun the download:

* `make setup`
* `dotnet SteamService.dll download`

`FORCE_REDOWNLOAD=1` skips validation and re-fetches everything.

This is the fallback when boot-time validation cannot run, particularly when Steam authentication is unavailable.

A texture-strip + 1×1-placeholder interceptor was evaluated and rejected for this incident. Its `File.Exists` gate cannot detect corrupt-but-present files, and two of its strip patterns would crash the server.

Do not revisit that approach.
