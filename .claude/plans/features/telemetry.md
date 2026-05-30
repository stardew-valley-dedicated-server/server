# Server Telemetry System

## Context

Server operators run JunimoServer in isolated Docker containers with zero central visibility into fleet health. Critical bugs (musl deadlocks, Galaxy lobby failures, cabin NREs) are only discoverable by reading individual server logs. There's no way to know what hardware requirements are realistic, which mods cause problems, or whether a new release introduces regressions. This feature adds **opt-in, fully anonymous** telemetry that periodically sends metrics to a master server.

The telemetry receiver endpoint (`https://telemetry.junimohost.com/v1/report` by default) is **not yet deployed**. Until it is, `TELEMETRY_ENABLED` defaults to `false` and the receiver-side schema is a deferred TODO that gates rollout — operators can configure the env var but nothing is collected until the receiver exists.

---

## Security Model (First-Class Concern)

### Threat: PII leakage in error messages

Mod and game error messages frequently embed PII (player names, multiplayer IDs, Steam IDs, public IPs, file paths in stack traces, farm names). Concrete examples — re-audit before implementation, since file contents shift:

| PII Type | Currently visible at (re-verify) | Data |
|----------|----------------------------------|------|
| Player names + IDs | `mod/JunimoServer/Services/Api/ApiService.cs:1071, 1083-1090, 3027` | `farmer.Name ?? farmer.displayName`, `farmer.UniqueMultiplayerID` |
| Steam server ID | `mod/JunimoServer/Services/SteamGameServer/SteamGameServerService.cs:170, 184` | `_serverSteamId.m_SteamID` |
| Public IP | `mod/JunimoServer/Services/SteamGameServer/SteamGameServerService.cs:176` | server's public IP |
| Farm names | various status/banner code | `Game1.player.farmName` |

**Implementation step**: Before writing `PiiScrubber`, audit `Monitor.Log` / `_monitor.Log` calls across `mod/JunimoServer/` that include `farmer.`, `Steam`, `IP`, `UniqueMultiplayerID`, `farmName`, or that interpolate `Exception.ToString()`. The scrubber's correctness depends on covering every site present at the time of implementation — large mod files (`ApiService.cs`, `PasswordProtectionService.cs`, `AuthService.cs`) shift frequently, so the audit must be re-run, not copied from this table.

Since we capture errors via Harmony postfix on SMAPI's `Monitor.Log`, every one of these messages flows through our collector. The sanitization layer is therefore the most security-critical component.

### Defense-in-depth: 5-layer PII scrubbing pipeline

Error messages pass through ALL layers sequentially before storage in the ring buffer:

```
Raw error message from Monitor.Log
    │
    ├─ Layer 1: LogLevel gate — ONLY capture Error and Alert (skip Debug/Trace/Info/Warn)
    │           This alone eliminates ~80% of PII-bearing logs (most PII is at Debug/Info)
    │
    ├─ Layer 2: Structural extraction — extract ONLY exception type name + first line of message
    │           Discard: full stack traces, inner exceptions, file paths entirely
    │           Example: "System.NullReferenceException: Object reference not set..." →
    │                    type="NullReferenceException", message="Object reference not set..."
    │
    ├─ Layer 3: Regex scrubbing — applied to the extracted message:
    │           • File paths: C:\...\, /home/..., /data/... → <path>
    │           • IP addresses: \d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3} → <ip>
    │           • IPv6 addresses: [0-9a-fA-F:]{6,} → <ip>
    │           • Steam IDs: 17-digit numbers (7656119...) → <id>
    │           • Multiplayer IDs: any number ≥10 digits → <id>
    │           • URLs: https?://[^\s]+ → <url>
    │           • Quoted names: '...' and "..." containing word chars → <name>
    │           • Hex tokens: 32+ hex chars → <token>
    │
    ├─ Layer 4: Allowlist source filter — only capture errors from known sources:
    │           • "JunimoServer" (our mod)
    │           • "SMAPI" (framework)
    │           • "game" (Stardew Valley itself)
    │           Third-party mod errors are DROPPED (we have no control over their message format)
    │
    └─ Layer 5: Truncation — hard cap at 200 characters after scrubbing
                (shorter than 500 — less room for residual PII)
```

### Instance ID: Local-only, user-controlled

- Generated locally as `Guid.NewGuid()` on first run
- Persisted to `{ModDataPath}/telemetry-instance-id.txt` (survives container restarts via volume)
- Operator can override via `TELEMETRY_INSTANCE_ID` env var or delete the file to reset
- **No derivation** from hardware, IP, Steam account, or any external identifier
- Purpose: the operator can voluntarily share their ID for targeted debugging — this is the ONLY way to correlate reports to a specific server

### What is NEVER collected

| Data | Why excluded |
|------|-------------|
| Player names / farmer names | Direct PII |
| Farm names | Could be PII (people name farms after themselves) |
| IP addresses (server or player) | Network PII |
| Steam IDs, GOG IDs, multiplayer IDs | Account identifiers |
| Chat messages | User-generated content |
| Save file contents | Private game data |
| Invite codes | Access credentials |
| API keys or tokens | Secrets |
| File paths | Reveal OS usernames |
| Full stack traces | Contain file paths |
| Mod names/authors | UniqueId is sufficient and already public |
| Third-party mod error details | No control over their PII hygiene |

### What IS collected (all anonymous)

| Data | Example | Why safe |
|------|---------|----------|
| Instance ID | `a3f7c2e1-...` | Random GUID, no external linkage |
| Server version | `1.2.0` | Public info |
| SMAPI version | `4.5.1` | Public info |
| Game version | `1.6.15` | Public info |
| OS platform | `linux` | Not identifying |
| .NET runtime | `6.0.36` | Not identifying |
| TPS / FPS / tick ms | `58.2 / 30.1 / 16.5` | Performance numbers |
| Memory MB | `512.3` | Server metric |
| GC counts | `142, 12, 3` | Runtime metric |
| CPU % | `34.2` | Process CPU utilization |
| Network bytes up/down | `1048576 / 524288` | Cumulative, no IP/address info |
| Player count / max | `3 / 8` | Aggregate count only |
| Total game days | `84` | Game progression |
| Season / year | `fall / 2` | Game state |
| Game phase | `day` | Current phase (not PII) |
| Farm type key | `Standard` | Game config (not farm name) |
| Cabin strategy | `CabinStack` | Feature config |
| Password protected? | `true` | Boolean only (not the password) |
| Container CPU limit | `1.5` | Docker cgroup, not identifying |
| Container memory limit MB | `2048` | Docker cgroup, not identifying |
| Save duration (ms) | `1234` | Last save timing |
| Save file size (bytes) | `2097152` | Save directory size |
| Mod UniqueIDs + versions | `Pathoschild.Automate 2.0.0` | Already public on Nexus/GitHub |
| Scrubbed error fingerprints | `NullReferenceException:a8f3...` | Type + hash, no message text |
| Event counters | `dayTransitions: 3, joins: 5` | Aggregate counts |

---

## Architecture

```
TelemetryService (orchestration, timer, HTTP client)
    ├── TelemetryCollector (stateless metric gathering from Game1/GC/Process)
    ├── ErrorCollector (Harmony postfix on SMAPI Monitor.Log, 5-layer scrubbing, ring buffer)
    ├── PiiScrubber (static regex-based sanitization utility)
    └── TelemetryModels (all DTOs)
```

Auto-discovered via existing reflection-based DI in `ModEntry.cs`. Zero changes to existing service files.

---

## Files to Create

### `mod/JunimoServer/Services/Telemetry/TelemetryModels.cs`

All DTOs with `SchemaVersion = 1` for forward-compatible evolution:

- `TelemetryReport` — top-level envelope: instance ID, timestamp, uptime, nested objects below (includes `ContainerInfo` at top level, nullable)
- `ServerIdentity` — server version, SMAPI version, game version, OS platform, runtime version, target TPS, rendering disabled, mod-incompat optims
- `PerformanceSnapshot` — TPS, FPS, avg tick ms, peak tick ms, memory MB, GC gen0/1/2, thread count, game thread wait ms, CPU %, network bytes up/down (cumulative from `BandwidthLogger`), save duration ms (last save), save file size bytes
- `GameStateSnapshot` — player count, max players, farm type key (NOT farm name), total game days, season, year, is paused, cabin strategy, password protected (bool), location count, game phase (enum: `loading`, `day`, `festival`, `sleeping`, `saving`, `paused`)
- `ContainerInfo` — CPU limit (cores), memory limit (MB), read from `/sys/fs/cgroup/` (cgroup v1 + v2 with fallback). Collected once at startup (limits don't change). `null` if not running in a container.
- `InstalledMod` — UniqueId, Version only
- `ErrorEntry` — timestamp, source (allowlisted), level, scrubbed message (200 char max), fingerprint (SHA256 of exception type + scrubbed first line)
- `EventCounters` — day transitions, player joins, player leaves, saves completed, crash recoveries, desync kicks

### `mod/JunimoServer/Services/Telemetry/PiiScrubber.cs`

Static utility class implementing the regex scrubbing pipeline (Layer 3):

- `Scrub(string message)` — applies all regex replacements in sequence
- Compiled `Regex` instances (static readonly) for performance
- Patterns: file paths, IPv4, IPv6, Steam IDs (17-digit), large numbers (10+ digits), URLs, quoted strings, hex tokens
- Unit-testable in isolation with known PII inputs

### `mod/JunimoServer/Services/Telemetry/ErrorCollector.cs`

- Static class — must be static because Harmony patches require static methods.
- `Initialize(Harmony harmony)` — installs postfix on SMAPI's logging surface.

  **Unverified prerequisite — resolve before implementation.** The closest in-tree precedent is `mod/JunimoServer/Util/SmapiLogConfig.cs`, but it only reads SMAPI's public static `ForceVerboseLogging` `HashSet<string>` via `Type.GetType("StardewModdingAPI.Framework.Monitor")`. It does **not** Harmony-patch the internal `Monitor.Log` method. Patching `Monitor.Log` requires resolving the internal method via reflection and binding a postfix to it — a materially different technique with no in-tree precedent.

  **TODO blocker before this plan can ship**: spike the actual technique. Options:
  - (a) Harmony-patch the internal `Monitor.Log` method directly via `AccessTools.Method(monitorType, "Log", new[] { typeof(string), typeof(LogLevel) })`. Verify the signature against the SMAPI version pointed to by `GAME_PATH`.
  - (b) Subscribe to a SMAPI-exposed event if one exists (e.g. `IMonitor.OnLogged` or similar). If none exists, (a) is the only path.
  - (c) Accept that error capture lands in a follow-up plan; ship telemetry without error collection in v1.

  Until this is verified with a working postfix on a real SMAPI install, the rest of the `ErrorCollector` design is contingent.

- Postfix implementation (assuming (a) lands):
  1. Layer 1: Check `level >= LogLevel.Error`, return immediately if not
  2. Layer 4: Check source against allowlist (`JunimoServer`, `SMAPI`, `game`), drop if unknown
  3. Layer 2: If message contains exception, extract type name + first line only; discard stack trace
  4. Layer 3: Run `PiiScrubber.Scrub()` on extracted message
  5. Layer 5: Truncate to 200 chars
  6. Generate fingerprint: `SHA256(exceptionType + ":" + scrubbedFirstLine)` — no message text in fingerprint, just a hash
  7. Enqueue to `ConcurrentQueue<ErrorEntry>` ring buffer (max 50 entries, oldest dropped when full)
- `DrainErrors()` — atomically returns and clears the buffer

### `mod/JunimoServer/Services/Telemetry/TelemetryCollector.cs`

Stateless utility:

- `CollectPerformance()` — reads `GC.GetTotalMemory()`, `GC.CollectionCount()`, `Process.GetCurrentProcess().Threads.Count`, CPU % (via `Process.TotalProcessorTime` delta / wall clock delta), network bytes up/down from `Game1.server.BandwidthLogger.TotalBitsUp` / `TotalBitsDown` (verified at `decompiled/sdv-1.6.15-24356/StardewValley/Network/BandwidthLogger.cs:46,48` — both are `double`). Reads must run on the game thread. Independent of ApiService (no coupling to private fields).
- `CollectGameState(PersistentOptions, ServerSettingsLoader)` — reads `Game1.getOnlineFarmers().Count()`, `Game1.GetFarmTypeKey()`, `Game1.dayOfMonth`, `Game1.currentSeason`, `Game1.year`, `Game1.locations.Count`, game phase (derived from `Game1.timeOfDay`, `Game1.CurrentEvent`, `Game1.activeClickableMenu`, `Game1.netWorldState.Value.IsPaused`). **Does NOT read** `Game1.player.farmName` or any player names. Must be called on game thread.
- `CollectSaveMetrics()` — last save duration (ms) measured via `Saving`/`Saved` event timestamps, save file size from `Constants.SavesPath`/`Constants.SaveFolderName` via `DirectoryInfo.EnumerateFiles().Sum(Length)`. Measured in the `Saved` event handler.
- `CollectContainerInfo()` — reads Docker cgroup limits at startup (once): CPU from `/sys/fs/cgroup/cpu.max` (v2) or `/sys/fs/cgroup/cpu/cpu.cfs_quota_us` + `cpu.cfs_period_us` (v1), memory from `/sys/fs/cgroup/memory.max` (v2) or `/sys/fs/cgroup/memory/memory.limit_in_bytes` (v1). Returns `null` if files don't exist (not containerized).
- `CollectMods(IModHelper)` — `helper.ModRegistry.GetAll()` mapped to `{UniqueId, Version}` only. Collected once at startup.
- `CollectServerIdentity(IModHelper)` — mod manifest version, `Constants.ApiVersion`, `Game1.version`, `Environment.OSVersion.Platform`, `Environment.Version`, config flags from `Env.*`.

### `mod/JunimoServer/Services/Telemetry/TelemetryService.cs`

ModService subclass:

**Constructor** `(IModHelper, IMonitor, Harmony, ServerSettingsLoader, PersistentOptions)`:
- All dependencies already registered in DI
- If `!Env.TelemetryEnabled`: no Harmony patches, `Entry()` logs and returns
- If enabled: `ErrorCollector.Initialize(harmony)`

**Entry()**:
1. Early return if disabled (no events, no timer, no HttpClient)
2. Load or generate instance ID:
   - `Env.TelemetryInstanceId` (env var override) → else read from `{Helper.DirectoryPath}/telemetry-instance-id.txt` → else `Guid.NewGuid()`, persist, log once
3. Collect mod list (cached for lifetime)
4. Subscribe to events:
   - `OneSecondUpdateTicked` — cache game state snapshot + game phase at telemetry interval
   - `DayStarted` — increment day transition counter
   - `Saving` — record save start timestamp
   - `Saved` — record save duration (now - saving timestamp), measure save file size, increment save counter
   - `Multiplayer.PeerConnected`/`PeerDisconnected` — join/leave counters
   - `ReturnedToTitle` — crash recovery counter (only if unexpected)
5. Start `System.Threading.Timer` at `Env.TelemetryIntervalSeconds`

**SendReportAsync()** (timer callback, threadpool):
1. Build report from cached game state + `CollectPerformance()` + `DrainErrors()` + event counters
2. Serialize via `Newtonsoft.Json`
3. POST to `Env.TelemetryEndpoint` (`Content-Type: application/json`, `X-Telemetry-Schema: 1`, 10s timeout)
4. Honor `nextIntervalHint` from response (clamped [60, 3600])
5. **Entire method in try/catch that swallows all exceptions** (Debug log only)
6. Failed sends: errors NOT re-queued (prevents unbounded memory growth)

**Resilience**:
- Static `HttpClient` (no socket exhaustion)
- 10s timeout, all exceptions swallowed — never impacts game server
- Game state cached from game thread, never accessed from timer thread directly
- Ring buffer capped at 50 entries (~10KB)

**Logging constraint** (per `.claude/rules/debugging.md`): the telemetry pipeline **must never log at `LogLevel.Error` or `LogLevel.Alert`**. The test runner's `ServerContainer` regex-matches every mod log line against `\b(ERROR|FATAL)\b` and cancels the test on a match (`tests/JunimoServer.Tests/Containers/ServerContainer.cs:629`). A telemetry-internal failure (HTTP timeout, JSON encode error, schema mismatch) logged at Error would self-trigger E2E test cancellation — and since telemetry is enabled by env var, an operator running tests with telemetry enabled would see unexplained cancellations. Use `LogLevel.Warn` for internal warnings; `LogLevel.Debug`/`Trace` for routine events; swallow-and-count for transient errors.

There is also a self-feedback risk: `ErrorCollector` postfixes `Monitor.Log`, so any telemetry-internal `Warn` line is itself observed by the collector. That's fine for `Warn` (Layer 1 gate filters Warn out). But if a future maintainer changes the gate to include `Warn`, the collector would observe its own warnings — call this out in `ErrorCollector` and `TelemetryService` as a co-located comment.

---

## Files to Modify

### `mod/JunimoServer/Env.cs`
Add `#region Telemetry`:
- `TelemetryEnabled` — `ParseBool("TELEMETRY_ENABLED", false)`
- `TelemetryEndpoint` — `GetEnvironmentVariable("TELEMETRY_ENDPOINT") ?? "https://telemetry.junimohost.com/v1/report"`
- `TelemetryIntervalSeconds` — `Math.Max(60, ParseInt("TELEMETRY_INTERVAL_SECONDS", 300))`
- `TelemetryInstanceId` — `GetEnvironmentVariable("TELEMETRY_INSTANCE_ID") ?? ""`

(All four env vars are introduced by this plan; the consumer is `mod/JunimoServer/Services/Telemetry/`. Exempt from `verify-documented-config-is-consumed.md` provided the consumer lands in the same change. Default for `TelemetryEnabled` stays `false` until the receiver endpoint is deployed.)

### `.env.example`
Add documented telemetry section with commented-out defaults.

### `docker-compose.yml`
Pass `TELEMETRY_ENABLED`, `TELEMETRY_ENDPOINT`, `TELEMETRY_INTERVAL_SECONDS` to server service.

---

## Master Server API

```
POST /v1/report
Content-Type: application/json
X-Telemetry-Schema: 1

→ 200 { "accepted": true, "nextIntervalHint": 300 }
→ 400 bad schema (client logs once, retries at normal interval)
→ 429 rate limited (client doubles interval temporarily)
→ 503 unavailable (client retries at normal interval)
```

Rate limit: max 1 report per 30 seconds per InstanceId. Schema version for backward-compatible evolution.

---

## What the Data Enables

- **Fleet dashboard**: Active servers, total players, TPS/memory percentiles, trend lines per release
- **Mod ecosystem**: Top installed mods, co-occurrence, correlation with error rates / degraded TPS
- **Error triage**: Fingerprint frequency, error-to-config correlation, new error detection per release, fleet-wide spike alerting
- **Hardware guidance**: Memory/TPS/CPU percentiles for sizing (e.g., "P95 servers use <800MB with 4 players at 1.5 CPU cores"). Container limits vs actual usage reveals over/under-provisioning.
- **Save health**: Save duration trends (growing save times indicate bloat), save size distribution, correlation between save size and day transition lag
- **Network profiling**: Bandwidth per player, network cost by game phase (festivals generate more traffic), helps size network requirements
- **Phase-aware analysis**: Performance breakdown by game phase — identify that festivals cause 2x tick duration, or that saving blocks the game thread for 3 seconds on large farms
- **Release planning**: Feature adoption, config distribution, uptime/stability trends

---

## Key Design Decisions

1. **5-layer PII scrubbing** — 40+ log sites with embedded PII. Defense-in-depth ensures residual PII is caught even if one layer fails.
2. **Source allowlist** — Third-party mods can log anything. Excluding them removes an entire PII risk category.
3. **Fingerprint = hash only** — `SHA256(type + scrubbedLine)` is opaque. Dedup works without inspecting message text.
4. **Instance ID persisted locally** — survives restarts, operator-controlled, no external derivation. Only correlatable if operator voluntarily shares it.
5. **No stack traces** — contain file paths with OS usernames. Exception type + first line is sufficient for classification.
6. **Farm type key, not farm name** — `GetFarmTypeKey()` returns "Standard" etc., not user-chosen names.
7. **Separate service** — clean separation from ApiService, independent disable, zero impact on existing code.

---

## Verification

1. `TELEMETRY_ENABLED=true`, `TELEMETRY_ENDPOINT=http://localhost:9999/v1/report`
2. Run HTTP echo server, start game server, wait for first report
3. **Security**: inspect payload — no player names, farm names, IPs, Steam IDs, file paths
4. Trigger known PII-bearing error, verify scrubbing in next report
5. `TELEMETRY_ENABLED=false` — verify zero HTTP requests
6. Kill echo server — verify game server unaffected
7. Verify `telemetry-instance-id.txt` created on first run, reused on restart
