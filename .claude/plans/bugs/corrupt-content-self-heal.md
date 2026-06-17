# Boot-time validation: self-heal corrupt game content

Status: open, needs design sign-off. An operator runbook already covers manual
repair (see sidenote at the bottom); this plan is the remaining automatic fix.

## Incident

Production error report:

```
[01:39:58 ERROR game] Failed to spawn NPC 'Vincent'.
Microsoft.Xna.Framework.Content.ContentLoadException: Asset does not appear to be a valid XNB file.
```

"Not a valid XNB" is the XNB *parse* error: the file exists with bad bytes
(truncated/partial write/bitrot). A *missing* file throws "content file was not
found" instead and is handled cleanly by the game's manifest guard
(`LocalizedContentManager.DoesAssetExist`, kept in sync by `PruneContentManifest`).
So this incident class is corruption-on-disk, and it recurs every boot until the
file is repaired.

## Why it persists

- The repair logic already exists: `DownloadGameAsync`
  (`tools/steam-service/SteamAuthService.cs:1322`) always chunk-hash-validates
  existing files ("Always validate files to detect corruption/deletion", line 1434)
  and re-downloads bad chunks (line 1568). Proven by
  `DownloadValidationTests.CorruptedFile_IsDetectedAndRepaired` (asserts "chunks
  need repair" → XNB header restored).
- But nothing triggers it at boot. The server entrypoint
  (`docker/rootfs/startapp.sh:106-145`, `init_stardew`) early-returns if the game
  executable exists and otherwise only *polls* for files to appear — it never
  validates. Downloads run only on explicit `make setup` /
  `SteamService.dll download`.
- The entrypoint has no Steam session; only the steam-auth sidecar does (validation
  needs a logged-in account to fetch the depot manifest). So the trigger must live
  in or go through the sidecar.

## Design

Two candidate trigger shapes:

**A. Validate inside `serve` startup, before binding HTTP.** Simplest code, and the
compose dependency (`server` has `depends_on: steam-auth: condition: service_healthy`)
would naturally hold the server back. Problem: the image healthcheck
(`tools/steam-service/Dockerfile:29` → `GET /health`) allows roughly
start-period 10s + 3×30s retries before the container is marked unhealthy, and
compose then fails the dependent service. Chunk-hashing ~500 MB can exceed that
budget on slow disks, so a blocking pass races the healthcheck. Also forces every
`serve` start (including test sidecars) to pay the cost.

**B. Background pass + readiness gate (preferred).** `serve` binds HTTP first
(healthcheck unaffected), then runs the validate/repair pass as a background task
and exposes its state (e.g. `GET /game/validate-status`, or a field on an existing
endpoint — but note `/health` is deliberately a pure status probe that must not
trigger logins, `Program.cs:469`). `init_stardew` gains a wait-for-validated step
*before* its early-return, so a restart with corrupt-but-present files blocks until
repair completes. The gate matters for correctness, not just ordering: repair
rewrites files in-place, so the game must not be reading them concurrently.

Decisions to settle at sign-off:

1. **Cost bound.** Measure the chunk-hash pass (~500 MB game dir) on the production
   VPS. If it's seconds, run unconditionally; if tens of seconds+, consider an env
   gate (e.g. `VALIDATE_ON_BOOT`, default on in `docker-compose.yml`) — and per
   `verify-documented-config-is-consumed`, only document the knob once wired.
2. **Test/CI sidecars.** E2E infrastructure starts steam-auth containers per run
   (`tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs`); decide whether they
   skip the pass (env gate off) or the per-boot cost is acceptable.
3. **Login-failure policy.** If account 0 can't log in (expired token, Steam down),
   warn and release the gate so the server still boots — possibly with corrupt
   files, falling back to the runbook. Never block boot indefinitely on Steam.
4. **Scope.** `download` also fetches the Steamworks SDK depot (`Program.cs:395`,
   `DownloadAllAsync`); decide whether the boot pass validates both depots or only
   the game depot (the incident class is game content).

## Verification

- Extend `DownloadValidationTests` (its fixture already does corrupt-then-repair
  against a standalone steam-auth container): corrupt an XNB in the shared volume,
  restart, assert the file is repaired and the server proceeds past `init_stardew`.
- Exercise the login-failure path: validate-on-boot with no usable session must
  log a warning and still release the server gate.
- Confirm the steam-auth healthcheck stays green throughout a full validation pass.

## Related files

| File | Role |
| --- | --- |
| `tools/steam-service/SteamAuthService.cs:1322` | `DownloadGameAsync` — always-on chunk validation (1434), repair (1568) |
| `tools/steam-service/SteamAuthService.cs:1104` | `PruneContentManifest` — manifest/filesystem sync |
| `tools/steam-service/Program.cs:365` | `serve` → `RunHttpServerAsync`; existing endpoints incl. pure-probe `/health` |
| `docker/rootfs/startapp.sh:106-145` | `init_stardew` — early-return + wait-loop, never validates; gate goes here |
| `docker-compose.yml` | `server` depends on steam-auth `service_healthy`; shared `game-data:/data/game` volume |
| `tools/steam-service/Dockerfile:29` | healthcheck budget that constrains design A |
| `tests/JunimoServer.Tests/DownloadValidationTests.cs` | proves the repair path (Skip-gated, needs `make setup`) |

## Sidenote: operator runbook (shipped)

Manual repair is documented in `docs/community/faq.md` ("Asset does not appear to
be a valid XNB file", line 102): re-run the download (`make setup` /
`dotnet SteamService.dll download`; `FORCE_REDOWNLOAD=1` skips validation and
re-fetches everything). Reference only — it's the fallback when the boot pass
can't run.

(A texture-strip + 1×1-placeholder interceptor was evaluated and dropped for this
incident: its `File.Exists` gate can't catch corrupt-but-present files, and two of
its strip patterns would crash the server. Don't revisit.)
