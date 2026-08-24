# Boot-time validation: self-heal corrupt game content

**Status:** Open; needs design sign-off. An operator runbook already covers manual repair (see sidenote below). This plan covers the remaining automatic repair path.

## Incident

Production reported:

```text
[01:39:58 ERROR game] Failed to spawn NPC 'Vincent'.
Microsoft.Xna.Framework.Content.ContentLoadException: Asset does not appear to be a valid XNB file.
```

`"Asset does not appear to be a valid XNB file"` is the XNB **parse** error: the file exists, but its contents are invalid (for example, truncation, a partial write, or bitrot).

A missing file produces `"content file was not found"` instead and is handled cleanly by the game's manifest guard (`LocalizedContentManager.DoesAssetExist`), which is kept in sync by `PruneContentManifest`.

Therefore this incident class is **corrupt-on-disk content**, not missing content. Without repair, the same corrupt file can fail every boot.

## Why it persists

The repair mechanism already exists.

`DownloadGameAsync` (`tools/steam-service/SteamAuthService.cs:1322`) always chunk-hash-validates existing files:

* `"Always validate files to detect corruption/deletion"` at line 1434;
* invalid chunks are re-downloaded at line 1568.

This behavior is covered by `DownloadValidationTests.CorruptedFile_IsDetectedAndRepaired`, which asserts that corrupted chunks are detected and that the XNB header is restored.

The missing piece is the **boot-time trigger**.

The server entrypoint (`docker/rootfs/startapp.sh:106-145`, `init_stardew`) currently early-returns when the game executable exists and otherwise only polls for files to appear. It never validates existing files.

Downloads therefore happen only through explicit `make setup` / `SteamService.dll download`.

The server entrypoint also has no Steam session. The steam-auth sidecar owns the logged-in account needed to obtain the depot manifest and validate/repair the content. Therefore the automatic trigger must live in, or go through, the sidecar.

## Design

There are two plausible trigger shapes.

### A. Validate inside `serve` startup, before binding HTTP

This is the simplest implementation. The compose dependency:

`server` → `steam-auth: service_healthy`

would naturally hold the server back while validation runs.

The problem is the steam-auth image's healthcheck (`tools/steam-service/Dockerfile:29`): `GET /health` allows roughly 10s of start period plus 3 × 30s retries before the container is considered unhealthy.

A full chunk-hash pass over roughly 500 MB can exceed that budget on a slow disk. A blocking validation pass would therefore race the healthcheck and could cause the dependent server container to fail even though repair itself is healthy.

It also makes every `serve` start pay the validation cost, including test sidecars that may not need it.

### B. Background validation + readiness gate — preferred

`serve` binds HTTP first so the healthcheck remains responsive, then starts the validate/repair pass as a background task and exposes its state.

The state could be exposed through a dedicated endpoint such as:

`GET /game/validate-status`

or as a field on an existing endpoint.

Do **not** make `/health` trigger validation or login. `/health` is deliberately a pure status probe (`Program.cs:469`) and should remain cheap and side-effect-free.

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

## Decisions required at sign-off

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

### 4. Validation scope

`download` also fetches the Steamworks SDK depot (`Program.cs:395`, `DownloadAllAsync`).

Decide whether boot-time validation should cover:

* both depots; or
* only the game depot.

The incident class being addressed is corrupt game content, so validating only the game depot may be the narrower and cheaper choice.

## Verification

Extend `DownloadValidationTests` — its fixture already performs corrupt-then-repair against a standalone steam-auth container — to exercise the actual boot path:

1. corrupt an XNB in the shared game volume;
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

## Related files

| File                                                  | Role                                                                                    |
| ----------------------------------------------------- | --------------------------------------------------------------------------------------- |
| `tools/steam-service/SteamAuthService.cs:1322`        | `DownloadGameAsync` — existing chunk validation (1434) and repair (1568)                |
| `tools/steam-service/SteamAuthService.cs:1104`        | `PruneContentManifest` — manifest/filesystem synchronization                            |
| `tools/steam-service/Program.cs:365`                  | `serve` → `RunHttpServerAsync`; existing endpoints including pure-probe `/health`       |
| `docker/rootfs/startapp.sh:106-145`                   | `init_stardew` — current early-return/wait loop; boot validation gate belongs here      |
| `docker-compose.yml`                                  | `server` depends on `steam-auth: service_healthy`; shared `game-data:/data/game` volume |
| `tools/steam-service/Dockerfile:29`                   | steam-auth healthcheck and its startup budget                                           |
| `tests/JunimoServer.Tests/DownloadValidationTests.cs` | Existing corrupt-then-repair coverage (Skip-gated; requires `make setup`)               |

## Sidenote: operator runbook (shipped)

Manual repair is already documented in `docs/community/faq.md` (`"Asset does not appear to be a valid XNB file"`, line 102).

The documented recovery is to rerun the download:

* `make setup`
* `dotnet SteamService.dll download`

`FORCE_REDOWNLOAD=1` skips validation and re-fetches everything.

This is the fallback when boot-time validation cannot run, particularly when Steam authentication is unavailable.

A texture-strip + 1×1-placeholder interceptor was evaluated and rejected for this incident. Its `File.Exists` gate cannot detect corrupt-but-present files, and two of its strip patterns would crash the server.

Do not revisit that approach.
