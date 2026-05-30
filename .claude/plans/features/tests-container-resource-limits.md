# Container Resource Limits & Variance-Reduction Levers — E2E Harness

## Context

The E2E test harness at `D:/Development/projects/stardew-dedicated-server/repos/server` runs Stardew Valley game processes inside Docker containers under parallel orchestration (`xunit.runner.json: maxParallelThreads: unlimited`, gated by per-host `serverSlots` — typically 3 locally). Today the three Testcontainers call sites — `ServerContainer.cs:272-279`, `GameClientContainer.cs:196-203`, `SharedSteamAuth.cs:127-135` — set only `CapAdd("SYS_TIME")` and ownership labels. **No memory cap, no PIDs cap, no CPU quota is applied.** A leaky test, a thread runaway, or a CPU-pathological test can push the host into reclaim/swap and produce real wall-clock jitter (seconds, not microseconds) that surfaces as flaky timeouts on *other* containers.

Originating discussion considered `--cpuset-cpus` for "micro-jitter reduction." That was rejected as the wrong tool (the kernel scheduler is already good at keeping warm threads on warm cores; pinning constrains start-phase elasticity and is a no-op on WSL2 vCPUs anyway). Instead this plan targets the **real** jitter sources: cross-container interference under memory/CPU contention, plus undocumented operator levers.

**Out of scope for this plan, deferred to a follow-up:**
- `DOTNET_gcServer=1` rollout — server GC's larger pre-collect heap footprint *amplifies* day-end OOM risk at a tight memory cap (`ServerOptimizer.cs:336` runs `GC.Collect(gen2, blocking)` at `OnDayEnding`; the burst that triggers the collect grows the heap larger under server GC). Bundle with this plan and you stack three new failure surfaces; do it separately after baseline.
- Sizing dashboard / automated peak-tracking tooling.

## Outcome

Operators get five new opt-in env vars to cap per-container resource usage. Defaults are sized from a one-time read of the most recent green `infrastructure.jsonl` (p95 × 1.5, rounded up to 256 MB). OOMKill diagnostics — which already exist end-to-end (`container_oom_killed` event emitted at server `:844`, client `:645`, steam-auth `:519`) — surface clear failure causes in the JSONL when a limit is hit. Note this diagnostic keys on container exit code 137 alone (not Docker `State.OOMKilled`), so it must be proven against a real WSL2/cgroup-v2 OOM before being relied on (see Risks). Documentation cross-references the existing variance levers (`serverSlots`, `SERVER_TPS`) rather than re-introducing knobs that already work.

---

## Resolution of the 7 points

| # | Point | Disposition | Default |
|---|---|---|---|
| 1 | `HostConfig.Memory` per container | **Ship** | Sized from p95 × 1.5 of last green run, committed in `.env.test.example` |
| 2 | `HostConfig.MemorySwap` (memory+swap total) | **Ship as opt-in**, default unset (Docker default = 2× Memory soft headroom) | unset |
| 3 | `HostConfig.PidsLimit` per container | **Ship** | server/client `1024`, steam-auth `256` |
| 4 | `HostConfig.NanoCPUs` per container | **Ship as opt-in**, documented experimental | unset (= unlimited) |
| 5 | `serverSlots` / `SDVD_MAX_SERVERS` doc cross-reference | **Doc-only** | n/a |
| 6 | `SERVER_TPS=60` verification | **Doc-only** (already correct: `Env.ServerTps` → applied in `ServerOptimizer`) | n/a |
| 7 | `DOTNET_gcServer=1` | **DEFER to a separate follow-up** | n/a |

**Why points 2, 4 are opt-in not default-on:** Combining a tight memory cap with a constrained swap total + a novel CPU quota in one rollout stacks three failure surfaces. Memory cap is the highest-value lever — ship it first under soft swap headroom, observe one green full-suite run, then operators can opt into tighter posture.

**Why point 7 is deferred:** Server GC reserves one heap segment per logical CPU and commits more eagerly between collections than workstation GC. On a 16-core host the steady-state heap is meaningfully larger, which makes the day-end burst at `ServerOptimizer.cs:317-341` hit a memory cap *before* `GC.Collect` runs to release. Bundle this with point 1 and the cap is harder to size correctly. The existing `ApiService.cs:3128-3130` `GC.CollectionCount` reads already surface via `gcRate` on the stats stream — sufficient instrumentation for a future A/B but not load-bearing for this plan.

---

## Sizing methodology (point 1)

Run this jq one-liner against the most recent green `infrastructure.jsonl` (path under `artifacts/<run-id>/infrastructure.jsonl`).

**Before running the jq, confirm two things against a real `infrastructure.jsonl` line** (`grep '"event":"instance_stats"' artifacts/<run-id>/infrastructure.jsonl | head -1`), because both the field key and the container-name prefixes are easy to get wrong:

1. **The serialized memory key.** The event payload comes from `InstanceStatsData` (`ContainerStatsCollector.cs:19`), whose `MemoryMb` member carries **no `[JsonPropertyName]`** — so the serialized key follows the event-bus `JsonSerializerOptions` naming policy (`SetupEventBus` / `Schema/Json`). It serializes camelCase (`memoryMb`) only if that policy is camelCase; under the default PascalCase policy the key is `MemoryMb` and the jq below returns nothing. Read the real line and use whichever key actually appears.
2. **The container-name prefixes.** The `.name` field carries the real container name (`ContainerName.TrimStart('/')`, `ContainerStatsCollector.cs:123`), which is `sdvd-server-{runId}` (`ServerContainer.cs:214`), `sdvd-test-client-{clientIndex}-{guid}` (`GameClientContainer.cs:161`), and `sdvd-steam-auth-{hostId}-{runId}` (`SharedSteamAuth.cs:111`). `server-{runId}` is only the *network alias* (`ServerContainer.cs:244`), not the container name — filtering on `server-`/`client-` would match nothing and silently yield `n=0`, falling back to the conservative seeds undetected.

```bash
# Server peaks (real container name prefix: sdvd-server-)
jq -r 'select(.event=="instance_stats" and (.name // ""|startswith("sdvd-server-"))) | .memoryMb' \
  artifacts/<run-id>/infrastructure.jsonl \
  | sort -n | awk 'BEGIN{c=0} {a[c++]=$1} END{print "n=",c,"p95=",a[int(c*0.95)],"max=",a[c-1]}'

# Client peaks (real container name prefix: sdvd-test-client-)
jq -r 'select(.event=="instance_stats" and (.name // ""|startswith("sdvd-test-client-"))) | .memoryMb' \
  artifacts/<run-id>/infrastructure.jsonl \
  | sort -n | awk 'BEGIN{c=0} {a[c++]=$1} END{print "n=",c,"p95=",a[int(c*0.95)],"max=",a[c-1]}'

# Steam-auth peaks (real container name prefix: sdvd-steam-auth-)
jq -r 'select(.event=="instance_stats" and (.name // ""|startswith("sdvd-steam-auth-"))) | .memoryMb' \
  artifacts/<run-id>/infrastructure.jsonl \
  | sort -n | awk 'BEGIN{c=0} {a[c++]=$1} END{print "n=",c,"p95=",a[int(c*0.95)],"max=",a[c-1]}'
```

If any of the three reports `n= 0`, the field key or the name prefix is wrong for this run — fix the jq before trusting the result. Default formula: `ceil(p95 × 1.5 / 256) × 256` MB. Commit the three resulting numbers in `.env.test.example`. The `instance_stats` event shape is `InstanceStatsData` (`ContainerStatsCollector.cs:19`); the memory field is its `MemoryMb` member. (`InfrastructureEventLog.cs:151` documents `container_oom_killed`, not `instance_stats`.)

If no green run is available, conservative seed values: server `2048`, client `1024`, steam-auth `512`. Replace with measured values on first successful run.

---

## Implementation steps

### Step 1 — Inline the limits at all three call sites

Per `simplest-solution.md`: the existing block is 5 lines, the added lines are ~6 lines, three call sites with diverging sizes. A `Helpers/ContainerResourceLimits.cs` extraction would add a config struct + a new file + types for a small block — heavier than duplication. **Inline at each site, with a short comment cross-referencing the other two.**

`SDVD_*_MEMORYSWAP_MB` is Docker's combined **memory + swap total** (`HostConfig.MemorySwap`), not a separate swap allowance. Docker rejects container create with *"Minimum memoryswap limit should be larger than memory limit"* if the swap total is below the memory cap, so the modifier must guard `swapMb > 0 && swapMb < memMb` with a clear error rather than passing a value Docker will reject at runtime. (The committed seed defaults leave swap unset, so they pass.)

**`tests/JunimoServer.Tests/Containers/ServerContainer.cs:272-279`** — extend the existing modifier:

```csharp
.WithCreateParameterModifier(p =>
{
    p.HostConfig.CapAdd ??= new List<string>();
    p.HostConfig.CapAdd.Add("SYS_TIME");
    p.Labels ??= new Dictionary<string, string>();
    p.Labels["sdvd.test"] = "true";
    p.Labels["sdvd.run-id"] = runId;
    // Per-container resource limits — keep parity with GameClientContainer.cs
    // and SharedSteamAuth.cs. 0 = unset = unlimited (Docker convention).
    var memMb = ResourceLimitEnv.ServerMemoryMb;
    if (memMb > 0)
    {
        p.HostConfig.Memory = memMb * 1024L * 1024L;
        var swapMb = ResourceLimitEnv.ServerMemorySwapMb;
        // MemorySwap is the memory+swap TOTAL. Docker rejects swap-total < memory
        // at create; fail fast with a clear message instead.
        if (swapMb > 0 && swapMb < memMb)
            throw new InvalidOperationException(
                $"SDVD_SERVER_MEMORYSWAP_MB ({swapMb}) is the memory+swap total and " +
                $"must be >= SDVD_SERVER_MEMORY_MB ({memMb}).");
        if (swapMb > 0) p.HostConfig.MemorySwap = swapMb * 1024L * 1024L;
    }
    if (ResourceLimitEnv.ServerPidsLimit > 0)
        p.HostConfig.PidsLimit = ResourceLimitEnv.ServerPidsLimit;
    if (ResourceLimitEnv.ServerCpuQuota > 0)
        p.HostConfig.NanoCPUs = (long)(ResourceLimitEnv.ServerCpuQuota * 1_000_000_000L);
})
```

**`tests/JunimoServer.Tests/Containers/GameClientContainer.cs:196-203`** — same shape, reads `ResourceLimitEnv.Client*` fields (incl. the same swap-total `>= memMb` guard).

**`tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs:127-135`** — same shape, reads `ResourceLimitEnv.SteamAuth*` fields (incl. the same swap-total `>= memMb` guard). Preserves the existing `sdvd.host-id` label.

### Step 2 — Add `ResourceLimitEnv.cs` env-var reader

New file: `tests/JunimoServer.Tests/Helpers/ResourceLimitEnv.cs`. Mirrors the existing inline pattern (`RecordingPolicy.cs`, `DownloadValidationFixture.cs` — no central env class for SDVD_*). Lazy reads with safe defaults:

```csharp
public static class ResourceLimitEnv
{
    public static int ServerMemoryMb { get; } = ReadInt("SDVD_SERVER_MEMORY_MB", 0);
    public static int ClientMemoryMb { get; } = ReadInt("SDVD_CLIENT_MEMORY_MB", 0);
    public static int SteamAuthMemoryMb { get; } = ReadInt("SDVD_STEAMAUTH_MEMORY_MB", 0);

    // MemorySwap is the combined memory+swap TOTAL (Docker HostConfig.MemorySwap),
    // not a separate swap allowance; must be >= the matching MEMORY_MB (enforced
    // at the call site). 0 = unset = Docker default headroom.
    public static int ServerMemorySwapMb { get; } = ReadInt("SDVD_SERVER_MEMORYSWAP_MB", 0);
    public static int ClientMemorySwapMb { get; } = ReadInt("SDVD_CLIENT_MEMORYSWAP_MB", 0);
    public static int SteamAuthMemorySwapMb { get; } = ReadInt("SDVD_STEAMAUTH_MEMORYSWAP_MB", 0);

    public static long ServerPidsLimit { get; } = ReadLong("SDVD_SERVER_PIDS_LIMIT", 0);
    public static long ClientPidsLimit { get; } = ReadLong("SDVD_CLIENT_PIDS_LIMIT", 0);
    public static long SteamAuthPidsLimit { get; } = ReadLong("SDVD_STEAMAUTH_PIDS_LIMIT", 0);

    public static double ServerCpuQuota { get; } = ReadDouble("SDVD_SERVER_CPU_QUOTA", 0);
    public static double ClientCpuQuota { get; } = ReadDouble("SDVD_CLIENT_CPU_QUOTA", 0);

    private static int ReadInt(string k, int d)
        => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    private static long ReadLong(string k, long d)
        => long.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    private static double ReadDouble(string k, double d)
        => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
}
```

`0` is the universal "unset = unlimited" sentinel for all four Docker.DotNet fields (`Memory`, `MemorySwap`, `PidsLimit`, `NanoCPUs`).

### Step 3 — Document in `.env.test.example`

Insert after line 83 (`# SDVD_MAX_CONCURRENT_EXTRACTIONS=3`) and before line 86 (`# SERVER_TPS=60`). Sized values to be replaced after running the jq script against a green run; the block below shows the seed defaults if no run data is available:

```
# Per-container resource limits (Docker HostConfig). 0 / unset = no limit.
# Sized from p95 × 1.5 of the instance_stats memory field in last green run's
# infrastructure.jsonl. See plan: container resource-limits sizing methodology.
#
# Memory caps (MB). On overflow Docker kills the container with exit code 137,
# surfaced as container_oom_killed events in infrastructure.jsonl.
# SDVD_SERVER_MEMORY_MB=2048
# SDVD_CLIENT_MEMORY_MB=1024
# SDVD_STEAMAUTH_MEMORY_MB=512
#
# MemorySwap caps (MB) = combined memory+swap TOTAL, NOT a separate swap budget.
# Must be >= the matching SDVD_*_MEMORY_MB or Docker rejects the container at
# create ("Minimum memoryswap limit should be larger than memory limit").
# Unset = Docker default = 2× memory (soft headroom). Set equal to
# SDVD_*_MEMORY_MB to disable swap entirely (fail-fast posture).
# SDVD_SERVER_MEMORYSWAP_MB=
# SDVD_CLIENT_MEMORYSWAP_MB=
# SDVD_STEAMAUTH_MEMORYSWAP_MB=
#
# PIDs caps. Catches thread-leak runaways. Steady state for a server container
# is ~150 PIDs (game + ffmpeg + Xvnc + s6); 1024 leaves headroom. NOTE: PIDs
# headroom has no UI indicator (unlike memory, which surfaces memoryLimitMb).
# pidsCurrent is on the instance_stats stream, but to read live headroom against
# the cap, exec into the container: cat /sys/fs/cgroup/pids.current vs pids.max.
# SDVD_SERVER_PIDS_LIMIT=1024
# SDVD_CLIENT_PIDS_LIMIT=1024
# SDVD_STEAMAUTH_PIDS_LIMIT=256
#
# CPU quota (CFS, in CPUs — fractional allowed). EXPERIMENTAL — under parallel
# orchestration the aggregate quota can exceed host vCPU count and amplify
# flakes from CPU contention. Default unset = unlimited. Only enable when
# investigating CPU-pathological tests in isolation.
# SDVD_SERVER_CPU_QUOTA=2.0
# SDVD_CLIENT_CPU_QUOTA=1.0
```

### Step 4 — Cross-reference doc additions (points 5 & 6)

In the same `.env.test.example` resource-limits block, add a final paragraph cross-referencing existing variance levers so operators don't reinvent them:

```
# Related existing variance levers (no new code; reminders only):
#   - serverSlots / clientSlots per-host in SDVD_DOCKER_HOSTS — caps parallel
#     containers per daemon. Dropping serverSlots reduces cross-container
#     contention at the cost of parallelism. See .claude/rules/test-timing.md
#     and .claude/rules/test-broker-invariants.md.
#   - SERVER_TPS=60 (default) — read into Env.ServerTps and applied by
#     ServerOptimizer (Game1.TargetElapsedTime). Lowering reduces CPU burn per
#     server but slows in-game time progression in tests.
```

### Step 5 — No changes to `ContainerStatsCollector` or pipeline

Per `runner-ui-pipeline-plumbing.md`: do NOT add `pidsLimit` / `cpuQuota` fields to `InstanceStatsData`. The existing `MemoryLimitMb` (already wired end-to-end, populated at `ContainerStatsCollector.cs:279-281`) is the headroom indicator and surfaces unchanged. `container_oom_killed` is already wired and emitted at server `:844`, client `:645`, steam-auth `:519`; UI plumbing through that event is pre-existing and assumed working.

---

## Risks called out in the plan (operator-facing)

1. **Day-end OOM-kill is the worst case.** `ServerOptimizer.cs:317-341` runs `GC.Collect(gen2, blocking)` at `OnDayEnding`; the memory burst that triggers it can exceed a tight cap *before* the collect releases. The sizing formula (p95 × 1.5) already accounts for this with margin, but if `container_oom_killed` events appear in a normal run, raise the relevant `SDVD_*_MEMORY_MB` by 25%.
2. **Aggregate CPU quota can exceed host vCPU count.** A 16-core host with `serverSlots=3` and 4 clients per server, all at `CPU_QUOTA=2.0`, sums to 24+ vCPU demand. The quota is a hard cap per container regardless of host idle capacity. Use only in isolation experiments.
3. **`MEMORYSWAP_MB` = memory cap plus an under-sized memory cap fails fast and loud.** Intended posture for ferreting out leaks; not a default. Setting the swap total below the memory cap is a misconfig Docker rejects at create — the Step-1 guard turns that into a clear error.
4. **`PidsLimit` clobbering legitimate concurrency.** Defaults assume modern Stardew + SMAPI + JunimoServer + ffmpeg + Xvnc + s6 thread counts (~150 steady-state — measured nowhere in-tree, so treat as an estimate). Mod changes that add threads (e.g. per-peer reader threads on a large lobby) may approach the cap; raise if `Resource temporarily unavailable` appears in container logs. PIDs headroom is not surfaced in the UI; to observe it, exec `cat /sys/fs/cgroup/pids.current` vs `pids.max` in a live container.
5. **`container_oom_killed` keys on exit code 137 alone, not Docker `State.OOMKilled` — the "attributable cause" promise is unproven under cgroup-v2/WSL2.** The gate is `if (exitCode == 137)` (`ServerContainer.cs:842`, `GameClientContainer.cs:643`, `SharedSteamAuth.cs:517`; the event is emitted just below at `:844`/`:645`/`:519`). Two failure modes have not been proven and must be checked before relying on the diagnostic:
   - **(a) The game is not container PID 1 — s6 is.** A `memory.max` kill of the game process inside the container may be restarted or reaped by s6 without the *container* exiting 137, so a genuine cap breach may produce no 137 and no event. The "attributable cause, not generic timeout" promise fails in that case.
   - **(b) Teardown SIGKILL also yields 137 — false positive.** `DisposeAsync` force-removes containers (`SharedSteamAuth.cs:501`, `GameClientContainer.cs:627` via `DockerOps.ForceRemoveContainerAsync`); a SIGKILL during normal teardown also surfaces as exit 137, which the gate would misreport as an OOM kill.
   Action: prove the 137 path against a real WSL2/cgroup-v2 OOM (G4) before trusting the event, and prefer cross-checking Docker `State.OOMKilled` from `docker inspect` if the 137-only signal proves unreliable.

---

## Critical files

| File | Change |
|---|---|
| `tests/JunimoServer.Tests/Containers/ServerContainer.cs` | Extend modifier block at `:272-279` (incl. swap-total `>= memMb` guard) |
| `tests/JunimoServer.Tests/Containers/GameClientContainer.cs` | Extend modifier block at `:196-203` (incl. swap-total `>= memMb` guard) |
| `tests/JunimoServer.Tests/Containers/SharedSteamAuth.cs` | Extend modifier block at `:127-135` (preserve `sdvd.host-id` label; incl. swap-total `>= memMb` guard) |
| `tests/JunimoServer.Tests/Helpers/ResourceLimitEnv.cs` | **New** — env-var reader, mirrors inline pattern from `RecordingPolicy.cs` |
| `.env.test.example` | Insert resource-limits block after `:83` (`SDVD_MAX_CONCURRENT_EXTRACTIONS`) and before `:86` (`SERVER_TPS`) |

No edits to: `ContainerStatsCollector.cs`, `SetupEventBus`, `SetupPipeServer`, `TestRunState`, test-ui — per `runner-ui-pipeline-plumbing.md`.

---

## Verification (runtime gates, per `runtime-post-conditions-are-gates.md`)

All four must pass before declaring done.

**G1 — Limits are applied to a running container (static):**
```powershell
# After starting a test that has Memory/PIDs set
docker inspect <container-id> | jq '.HostConfig | {Memory, MemorySwap, PidsLimit, NanoCpus}'
# Expect Memory / PidsLimit to match SDVD_* values; NanoCpus=0 if quota unset
```

**G2 — Limit is visible to the .NET process inside the container (runtime):**
```powershell
docker exec <container-id> cat /sys/fs/cgroup/memory.max  # bytes, matches SDVD_*_MEMORY_MB*1024*1024
docker exec <container-id> cat /sys/fs/cgroup/pids.max    # matches SDVD_*_PIDS_LIMIT
```

**G3 — Full suite passes with defaults applied (negative regression check):**
```powershell
# Run with the committed defaults, compare summary.json:
make test
# Required: passed/failed parity with the immediately preceding green run.
# Required: zero container_oom_killed events:
jq 'select(.event=="container_oom_killed")' artifacts/<run-id>/infrastructure.jsonl
# (expect empty)
```

**G4 — Deliberately-undersized limit surfaces a genuine cap-breach OOM (positive failure check):**

This gate proves the `exitCode==137 ⇒ container_oom_killed` path actually fires on a *real* cgroup-v2/WSL2 memory-cap breach, and is not satisfied by the teardown-SIGKILL false-positive path (Risk #5). Two prerequisites before this gate is meaningful:

1. **Name a confirmed-existing test class with a confirmed >256 MB server peak.** Do not assume one. First run the Sizing-methodology jq against a green run to find a class whose server container's measured peak exceeds the undersized cap you set; if `FILTER` matches no test, jq returns empty and G4 passes *vacuously*. (`CropSaverTests` exists but runs at `SERVER_TPS=15` and has no measured >256 MB peak — do not use it for this gate without first confirming its peak.)
2. **Distinguish the cap-breach kill from the teardown kill.** A teardown SIGKILL also yields exit 137, so "the event appears" is not sufficient. Confirm the 137 was caused by the cap breach: the OOM must occur *during the test body* (before `DisposeAsync` runs), and `docker inspect <container>` taken at the moment of failure should show `State.OOMKilled == true` (or the container's exit must precede the force-remove). If the only 137 in the run coincides with teardown, the gate has NOT demonstrated OOM detection.

```powershell
# Force a server OOM by setting a too-low cap on a confirmed memory-heavy test
$env:SDVD_SERVER_MEMORY_MB = "256"
make test FILTER=<confirmed-heavy-test-class>
# Required: container_oom_killed event appears with role=server AND the kill is
# attributable to the cap breach (State.OOMKilled true / exit precedes teardown),
# not a teardown SIGKILL.
jq 'select(.event=="container_oom_killed" and .role=="server")' artifacts/<run-id>/infrastructure.jsonl
# (expect >=1 hit; test fails with attributable cause, not generic timeout)
```

Per `plan-discipline.md` adversarial pre-verification: this change is at the cgroup layer below the game process. It does not interact with LAN vs Steam transport, lobby/unauthenticated players, FPS caps, disconnect mid-operation, or stuck-latch recovery — those all run inside the container, above the cgroup boundary. The only mod-layer interaction is the existing day-end `GC.Collect` burst against the memory cap, addressed by the sizing formula.
