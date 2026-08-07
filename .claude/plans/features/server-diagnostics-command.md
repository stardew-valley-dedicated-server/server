# `diagnostics` — one-dump server-state collection tool

## Goal

One canonical command a maintainer can point users at, that collects the full
server state a maintainer needs — build identity, server logs, settings, installed
mods, players/farmhands/cabins, live diagnostics — into a single attachable file.

It is **not** framed as "file a bug." The realistic workflow: a user describes their
problem first (in a Discord support thread or a GitHub issue), *then* the maintainer
points them at this tool, and they attach its output. So the tool does **not** ask
"what's the matter" — the user has already said that in their own words. It only
prompts for the technical facts the server genuinely can't derive on its own
(client-side mods, affected player/platform, reproducibility). GitHub issues are one
destination; a Discord support thread — a back-and-forth, not a filed defect — is
the more common one.

Invoked as:

```sh
docker compose exec -it server diagnostics
```

## Shape and rationale

A standalone C# Spectre.Console `Exe` at `tools/diagnostics/`, an **exact sibling of
`tools/netdebug/`** (self-contained `linux-x64` single-file publish, launched by a
2-line bash wrapper). It runs as a **separate process**, not inside the mod.

Why a separate process (not a SMAPI console command): the mod's console reads its
input from a one-way FIFO (`docker/rootfs/startapp.sh:273` — `tail -f
/tmp/smapi-input | "${SMAPI_EXECUTABLE}"`), so a `helper.ConsoleCommands.Add`
callback cannot prompt-then-read turn-by-turn — it has no interactive TTY. A
separate process launched with `docker compose exec -it` owns a real TTY and can
run an arrow-key wizard. `netdebug` already proves this pattern in-tree.

The tool reaches the server's live state via the mod's **localhost HTTP API**,
exactly as any external client would — no new mod code, no new endpoint.

## Locked design decisions

- **Delivery:** write a single timestamped `.zip` to `/data/diagnostics/`, which is
  **bind-mounted to the host** (see change 11) so the file appears at
  `./diagnostics/state-<ts>.zip` on the host with no `docker cp` needed. Print the
  host path and tell the user to attach it to their existing thread/issue. (Without
  the bind mount `/data` lives in the container's ephemeral writable layer —
  unreachable from the host and lost on container recreate — so the mount is
  load-bearing for the whole delivery model, not a convenience.)
- **Wizard (technical gaps only):** when run on a TTY, prompt ONLY for facts the
  server can't derive — all optional/skippable, no problem-narrative question:
  1. Client-side mods? → which + versions (the server has zero visibility into what
     mods each *player* runs locally).
  2. Affected player (who they are on the server) + platform (Steam / GOG / OS) —
     the server can't attribute a symptom to a specific client's platform.
  3. Reproducibility (every time / once) and whether it started after a change (added
     a mod, updated, changed a setting) — timeline the logs may not span.
  Non-interactive (`exec` without `-it`) skips prompts and writes a short "technical
  details to include" template covering the same three gaps.
- **Build identity:** report shows the mod/image version (already carries the
  preview counter, e.g. `1.5.0-preview.42`) **and** a newly baked-in short git SHA.
  No separate "image version" field — the version string already encodes the
  channel/counter; only the SHA is net-new. See
  [`build-version-identity`](#background-versioning) below.
- **Format:** `report.md` (readable server-state summary + the technical answers) +
  raw `server-output.log` + `SMAPI-latest.txt` + `SMAPI-crash.txt` (if present), all
  zipped.
- **Data source:** localhost HTTP API + disk. No new mod endpoint.

## File-by-file changes

### 1. CREATE `tools/diagnostics/Diagnostics.csproj`

Mirror `tools/netdebug/NetDebug.csproj` (`net10.0`, `OutputType=Exe`,
`Nullable=enable`) minus `DnsClient`. `System.IO.Compression`/`ZipFile` is in the
shared framework — no PackageReference. Output assembly name defaults to
`Diagnostics` → published exe `/output/diagnostics/Diagnostics` (wrapper + COPY
depend on this).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Spectre.Console" Version="0.57.0" />
  </ItemGroup>
</Project>
```

All projects inherit `TreatWarningsAsErrors=true` + `GenerateDocumentationFile=true`
with CS1591 suppressed (`CodeStyle.props:8-18`), so public members need no XML docs
— same as netdebug. Program.cs must be warning-clean (no unused usings, no unhandled
nullable warnings) or the build fails.

### 2. CREATE `tools/diagnostics/Program.cs`

Single file, netdebug style (`AnsiConsole.MarkupLine`, `Rule`, `Table`, `Panel`,
`Status().StartAsync`).

**Config (env):** `API_PORT` (default `8080`), `API_KEY` (default `""`),
`API_ENABLED` (default `true`), `SDVD_GIT_SHA` (default `"unknown"`),
`SMAPI_VERSION` (default `"unknown"`); `baseUrl = http://127.0.0.1:{API_PORT}`.

**Interactivity gate:** `bool interactive = !Console.IsInputRedirected &&
!Console.IsOutputRedirected;` (with `-it` both streams are a PTY → true → wizard
runs; without `-it` both are pipes → false → template written. The `exec`-through
bash wrapper preserves the PTY — `exec` replaces the process, fds inherited.)

**HTTP collect:** one `HttpClient` (10s timeout), header `Authorization: Bearer
{API_KEY}` when the key is non-empty (the API expects `Bearer`, NOT `x-api-key` —
`ApiService.cs:1820,1827,1832`). A local resilient `TryGet(path)` (mirrors the mod's
`TryRead` shape, `ApiService.cs:3219`): returns raw JSON on success or `null` on any
failure, appends the path to `failedSections`, never throws. GET `/status`,
`/diagnostics/state`, `/settings`, `/players`, `/farmhands`, `/cabins`. Only
`/diagnostics/state` is public (`ApiService.cs:2082-2088`); the rest need the key —
sending Bearer on all is harmless. Timing is not a concern: 5 of 6 endpoints are
lock-free snapshot reads that return instantly even if the game thread is hung, and
`/diagnostics/state` self-bounds to ~3s (`RunOnGameThreadAsync` 3s budget,
`ApiService.cs:3060`) — well inside the 10s client timeout.

**API-disabled detection:** when `API_ENABLED=false` the mod never starts the
listener (`ApiService.cs:1043-1047`) so all 6 GETs are connection-refused. In that
case emit ONE explicit "HTTP API disabled (API_ENABLED=false) — live-state sections
skipped" line into the report instead of 6 anonymous `failedSections` entries. The
tool still zips logs + mods + crash log.

**Version:** parse `/status` with `JsonDocument`, read `serverVersion` (camelCase of
`ServerStatus.ServerVersion`, `ApiService.cs:54`; the API serializes with Newtonsoft
+ `CamelCasePropertyNamesContractResolver`, `ApiService.cs:1007-1011`); `"unknown"`
if absent. SMAPI version from the `SMAPI_VERSION` env var. Game version is **not**
exposed by any collected source → print `unknown`.

**Wizard (interactive only) — technical gaps, all optional:**
- `SelectionPrompt` "Do you use client-side mods?" Yes/No → if Yes, `TextPrompt`
  "Which ones (name + version)?".
- `TextPrompt` "Which player is affected (your name on the server), and on what
  platform (Steam / GOG / OS)?" — allow blank.
- `SelectionPrompt` "Does it happen every time or just once?" (Every time / Once /
  Not sure) → `TextPrompt` "Did it start after a change (mod added, update, setting)?
  (optional)".

No "describe your problem" prompt — the user has already stated that in the thread
they came from. When not interactive, skip prompts and emit a `## Technical details
to include` template block with the same three gaps left blank.

**Disk collect:**
- SMAPI console log `/tmp/server-output.log` (`startapp.sh:255`) → zip as
  `server-output.log`. This is the `script(1)` PTY typescript (`startapp.sh:273`),
  so it carries ANSI color escapes but captures early boot output. ALSO collect
  SMAPI's canonical structured log `ErrorLogs/SMAPI-latest.txt` (same config root as
  the crash log) when present → zip as `SMAPI-latest.txt`; it's cleaner and is what
  SMAPI's own bug-report guidance asks for. Keep both.
- Crash log: probe first-existing of
  `/config/xdg/config/StardewValley/ErrorLogs/SMAPI-crash.txt` (the real path —
  `XDG_CONFIG_HOME=/config/xdg/config` from the jlesage base image; the saves volume
  mounts there, `docker-compose.yml:19`; `startapp.sh:188` names the file), plus a
  `SMAPI-crash.txt` glob under that root as a fallback. Found → zip as
  `SMAPI-crash.txt`; else note "no crash log present". (Do NOT probe `/root/.config`
  — `XDG_CONFIG_HOME` is set, so `$HOME/.config` is never the resolved path.)
- Mods: enumerate `/data/Mods/**/manifest.json` **recursively**
  (`SMAPI_MODS_PATH=/data/Mods`, `Dockerfile:152`), parse
  `Name`/`UniqueID`/`Version`/`Author`, tolerating missing fields. Recursion is
  required: SMAPI's bundled mods (ConsoleCommands, SaveBackup, ErrorHandler, …) are
  copied to `/data/Mods/smapi/<Mod>/` (`startapp.sh:194-196`), below a `*` glob — a
  non-recursive glob would silently omit them, and "which server mods are loaded" is
  a core datum. Recursing mirrors SMAPI's own scan.

**`report.md` sections:** Build identity (version, git SHA, SMAPI, game=unknown,
`failedSections`/API-disabled note) · Server settings (pretty `/settings`) ·
Installed server mods (table) · Players/Farmhands/Cabins (summarized) · Live
diagnostics (pretty `/diagnostics/state`) · Reported details (the wizard answers) or
the `## Technical details to include` template.

**Zip:** `Directory.CreateDirectory("/data/diagnostics")`; `timestamp =
DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ssZ")`; `zipPath =
/data/diagnostics/state-{timestamp}.zip`. Build with `ZipArchive`: write `report.md`
via `CreateEntry`, add logs via `CreateEntryFromFile`. Print the host path (green
panel) — `./diagnostics/state-<ts>.zip` — plus "Attach this file to your support
thread or GitHub issue." On non-interactive runs, also note the template inside
needs completing. If the bind mount is absent (bare deploy), also print the
`docker compose cp sdvd-server:{zipPath} .` fallback.

Single action — no `args` dispatch (optional `--help` for parity, keep minimal).

### 3. CREATE `docker/rootfs/opt/base/bin/diagnostics`

```bash
#!/bin/bash
exec /opt/diagnostics/Diagnostics "$@"
```

Covered by `chmod +x /opt/base/bin/*` (`Dockerfile:235`) — no separate chmod.

### 4. EDIT `docker/Dockerfile` — build stage (after netdebug at `:93`, in `mod-builder`)

```dockerfile
# Build diagnostics tool
COPY ./tools/diagnostics /src/diagnostics
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish /src/diagnostics \
    --configuration Release \
    --self-contained true \
    --runtime linux-x64 \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o /output/diagnostics
```

`PublishTrimmed=true` kept (matches netdebug); the trimming risk on
`System.IO.Compression` is covered by the end-to-end post-condition (see risks). If
that test throws a missing-type, flip THIS stage to `PublishTrimmed=false`.

### 5. EDIT `docker/Dockerfile` — COPY into final image (after `:218`)

```dockerfile
COPY --from=mod-builder /output/diagnostics /opt/diagnostics
```

### 6. EDIT `docker/Dockerfile` — thread `SDVD_GIT_SHA` (final `server` stage)

After `ARG TMUX_VERSION=3.7b` (`:131`):

```dockerfile
ARG SDVD_GIT_SHA=unknown
```

Extend the single multi-line `ENV` block (`:133-152`) — add a trailing `\` after
`SMAPI_MODS_PATH=/data/Mods` (currently no trailing backslash at `:152`) then:

```dockerfile
    SMAPI_MODS_PATH=/data/Mods \
    # Git commit the image was built from (diagnostics tool reads this)
    SDVD_GIT_SHA=${SDVD_GIT_SHA}
```

A full-line `#` comment inside the `\`-continued `ENV` parses — the existing block
already has them at `:144,149`. The `server` stage (`FROM ... AS server`, `:128`) is
the final runtime stage, so this ARG/ENV is visible to the running tool. Default
`unknown` when the build-arg isn't passed.

### 7. EDIT `.github/workflows/build-preview.yml` — build-arg on the inline server build (`:153-168`)

Add to the `Build and push Docker image` step:

```yaml
          build-args: |
            SDVD_GIT_SHA=${{ github.sha }}
```

(`${{ github.sha }}` is already used at `:186`.)

### 8. EDIT `.github/workflows/build-release.yml` — build-arg on the inline server build (`:66-81`)

Same `build-args: SDVD_GIT_SHA=${{ github.sha }}` addition. This workflow triggers on
`push` to master gated by release-please (`:4-6`), so `github.sha` is the pushed
(release) commit — non-empty and correct.

> The shared `.github/workflows/build-image.yml` is **not** modified — it builds
> only `sdvd/steam-service` and `sdvd/discord-bot`, not the server image. The server
> image is built by the inline steps above in both workflows.

### 9. EDIT `docs/admins/operations/commands.md` — document `diagnostics`

Add a "Collecting server diagnostics" section (after the CLI section) covering the
`docker compose exec -it server diagnostics` invocation, the host output path
(`./diagnostics/state-<ts>.zip`, via the change-11 bind mount), what it bundles, the
technical-gaps wizard, and the non-`-it` template fallback. Frame it as "when a
maintainer asks for it, run this and attach the file to your thread/issue" — not "to
report a bug." Add a quick-reference row.

### 10. EDIT `Makefile` — `make diagnostics` (sibling of `make cli` at `:121-123`)

```makefile
# Collect a server-state diagnostics bundle (wizard + zip on the host under ./diagnostics)
diagnostics:
	@docker compose exec -it server diagnostics
```

Plus a help line after the `make cli` entry (`:281`):

```makefile
	@echo "  make diagnostics - Collect a server-state diagnostics zip (host ./diagnostics/)"
```

With the change-11 bind mount, `make diagnostics` alone lands the zip on the host at
`./diagnostics/` — no separate `docker cp`.

### 11. EDIT `docker-compose.yml` AND `docker/modern/docker-compose.yml` — bind-mount the output dir

Add to the `server` service `volumes:` list in BOTH compose files:

```yaml
      - ./diagnostics:/data/diagnostics
```

**Load-bearing**, not convenience: `/data` is not otherwise a volume/bind mount
(`docker-compose.yml:17-20` mounts only `/data/game`, `/config/.../StardewValley`,
`./.local-container/settings:/data/settings`), so without this the zip lands in the
container's ephemeral writable layer — unreachable from the host, wiped on container
recreate. With it, the report appears at `./diagnostics/state-<ts>.zip` on the host.
(A bare production deploy not inheriting this compose file needs the mount or the
`docker compose cp sdvd-server:/data/diagnostics/<file> .` the tool prints.)

## Scope notes (verified)

- **Modern image (`docker/modern/`) build is NOT touched** — it builds/copies only
  `dll-patcher`, never netdebug (`docker/modern/Dockerfile:177-186,317`), so the
  diagnostics tool is intentionally absent from the modern image build. (Its
  `docker-compose.yml` still gets the change-11 bind mount for parity, harmless if
  the binary isn't present — but the tool only ships in the glibc image.)
- **No new mod code / no new HTTP endpoint** — the tool consumes existing API routes.

## Open risks

1. **`PublishTrimmed=true` + `System.IO.Compression`.** Kept `true` (matches
   netdebug, smaller binary). `ZipArchive`/`ZipFile` usage is direct/static so the
   trimmer should retain it, but a trimmer-removed-type failure would surface only at
   runtime when a user reaches the zip step (build stays green). **Mitigation:
   post-condition 1 runs the tool END-TO-END and asserts a valid zip — NOT just
   `--help`, which never touches the compression path.** If it throws a missing-type,
   flip this one build stage to `PublishTrimmed=false`.
2. **Game version** isn't in any collected source → report prints `unknown`. Future:
   parse the "running Stardew Valley X.Y.Z" line from the SMAPI log header.
3. **E2E / local `:local` images report `SDVD_GIT_SHA=unknown`.** `make build-server`
   (`Makefile:57-71`) passes only `--build-arg BUILD_CONFIGURATION`, so locally- and
   E2E-built images never get the SHA — only preview/release CI images do (changes
   7–8). Acceptable; if a local pin is wanted, add `--build-arg
   SDVD_GIT_SHA=$(git rev-parse --short HEAD)` to the Makefile. Post-condition 6 is
   scoped to preview/release images.

## Resolved during adversarial review (previously open)

- **Crash-log path** — confirmed `/config/xdg/config/StardewValley/ErrorLogs/SMAPI-crash.txt`
  (XDG from the jlesage base image; `startapp.sh:188` names the file). Dead
  `/root/.config` candidate dropped.
- **Delivery / host reachability** — was a blocker (zip stranded in the container's
  writable layer); resolved by the change-11 bind mount.
- **Mods enumeration** — was incomplete (one-level glob missed SMAPI-bundled mods);
  resolved by recursing.
- **HTTP timing on a hung server** — not a risk: snapshot-backed reads + 3s
  `/diagnostics/state` budget, inside the 10s client timeout.

## Post-conditions to check after implementation

1. **End-to-end zip test (also the trimming gate).** `dotnet publish
   tools/diagnostics -c Release -r linux-x64 --self-contained
   -p:PublishSingleFile=true -p:PublishTrimmed=true -o /tmp/out` succeeds with no
   trim errors; then RUN the produced binary far enough to actually build a zip (not
   just `--help`) and assert the `.zip` exists and opens.
2. `make build-server` succeeds; the build stage + both COPYs land.
3. `make up`, wait healthy, `docker compose exec -it server diagnostics` → the
   technical-gaps wizard prompts; the zip appears **on the host** at
   `./diagnostics/state-<ts>.zip` with no `docker cp`.
4. Unzip the host file: it contains `report.md` + `server-output.log` +
   `SMAPI-latest.txt` (+ `SMAPI-crash.txt` only if a crash occurred). Confirm the
   crash-log path resolved, and that the mods table includes SMAPI-bundled mods (from
   `/data/Mods/smapi/*/`), not just JunimoServer.
5. `docker compose exec server diagnostics` (no `-it`) → wizard skipped, `report.md`
   contains the `## Technical details to include` template.
6. On a **preview/release** CI-built image (not `:local`), build identity shows the
   real `serverVersion` and a non-`unknown` `SDVD_GIT_SHA`.
7. With `API_ENABLED=false`, the report shows the explicit "HTTP API disabled" line
   (not 6 anonymous failures) and still contains logs + mods.

## Background: versioning

The running mod version is `ModRegistry.Get("JunimoHost.Server").Manifest.Version`
(surfaced as `/status` `serverVersion`). On **preview** builds CI rewrites
`manifest.json`/`.csproj` to `{next}-preview.{counter}` *before* the Docker build
(`build-preview.yml:123-133`) and tags the image identically — so the version string
already distinguishes preview builds by counter. It does **not** pin the exact
commit; no git SHA is baked in today (`github.sha` appears only in the CI build
summary). The `SDVD_GIT_SHA` build-arg (changes 6–8) closes that gap.
