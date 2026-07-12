# Investigate: cut E2E wall-clock via more remote Docker capacity (burst fleet)

## Goal

Cut full-suite wall-clock by adding remote Docker host capacity, because the single VPS (`vps-1`) can't carry more parallelism. Decide **wide (several medium hosts) vs fat (one large host)**, sized and costed for **burst** use (spin up per run, destroy after — NOT always-on monthly), under the hard **amd64-only** constraint.

This is an investigation + decision plan, not yet an implementation plan. It ends with a recommended config and a cheapest-first sequence; the Terraform/teardown build is the follow-up.

## Measured ground truth (from `TestResults/runs/`, not estimates)

All numbers below are from real run artifacts in this repo — provenance matters here because earlier sizing leaned on padded plan estimates (see "Provenance" below).

- **Production test host today:** `vps-1` — **10 vCPU / 32 GB / x86_64**, Debian 12, Docker 29.1.5 (from `docker_preflight` in every run's `infrastructure.parent.jsonl`).
- **Current slot config** (`.env.test`, `SDVD_DOCKER_HOSTS`): single host `vps-1`, **`serverSlots:3, clientSlots:6`**.
- **Green full-suite run** `2026-06-20T02-13-56Z_fbab351` (127 passed, 7 skipped, 0 failed):
  - `durationMs` = **1,216,116 ms ≈ 20.3 min wall-clock** ← the metric to cut.
  - `activeDurationTotalMs` = **2,564,695 ms ≈ 42.7 min** of test work.
  - **Achieved parallelism ≈ 42.7 / 20.3 ≈ 2.1×** on a 10-core/32 GB host at `serverSlots:3`.
  - `queueDurationTotalMs` = **69,196,010 ms ≈ 1,153 min** — xUnit dispatch-wait, NOT a broker bottleneck (per `test-timing.md`). Confirms the suite is massively dispatch-parallel and **slot-starved**: huge latent parallelism waiting on slots.
- **No per-container memory data exists** in any `TestResults/` run — zero `instance_stats` events (stats collection was off). So the true per-container working set is unmeasured; the only RAM fact we have is "the whole suite + ~peak concurrent containers fits in 32 GB green."

### What the data says about the bottleneck

Only ~2.1× parallelism on a 10-core box at `serverSlots:3` means the host was **not CPU/RAM-bound — it was slot-/boot-bound**. The wall-clock floor is **per-test server boot (~41s, from `provision-up-front-when-startup-exceeds-serviceable-tail.md`)**, gated **per-daemon** by `serverSlots` + the per-host `StartLimiter`, plus per-host reuse-cache contention (`test-broker-invariants.md`: capacity and reuse cache are **per-host by structure**).

## Decision: wide beats fat — for THIS suite

A fatter single host does not fix a per-daemon serialization floor:

1. **Boot parallelism is gated per-daemon.** A bigger host boots more servers in parallel only up to its slot cap; you're already not CPU-bound at `serverSlots:3`, so more cores alone won't raise the achieved 2.1× much. More *daemons* = more independent `StartLimiter`s and `serverSlots` gates → linear boot-parallelism gain.
2. **Reuse caches + capacity gates are per-host.** Tests sharing a config hash queue behind one server on one host. N hosts = N independent capacity gates + N independent reuse caches + N StartLimiters — N× the contention relief. One fat host is still *one* of each.
3. **The harness is built for horizontal fan-out and it's free.** `SDVD_DOCKER_HOSTS` already does Hamilton allocation across hosts; `HostPool.Place` load-balances; adding hosts is **pure JSON config, zero code**.
4. **Burst makes wide ≈ same cost as fat** (see costs) because hourly billing rounds up to 1 full hour per host per run regardless of the 20-min suite, and 3 medium hosts ≈ the hourly rate of 1 large host.

**The only point in favor of fat:** each **Steam-capable** host needs **≥2 `STEAM_ACCOUNTS`** (slicing rule, `remote-host-setup.md`). More hosts running Steam tests = more accounts. Solvable by buying more accounts or pinning the Steam subset to one host.

### Host-size floor: medium, not small

A single test co-places its server + up to ~4-6 clients **on the same host** (server and its clients are co-placed). A 2-core/4 GB host (e.g. Hetzner CX22) can't hold a server + several game-client instances, so tests won't place. **Floor is ~8 vCPU / 16 GB (Hetzner CX42 / CPX41 class).** Don't go smaller per host.

## The amd64 constraint (carried from prior research, verified)

Game images are amd64-pinned (linux-x64 .NET, amd64 ffmpeg). QEMU ~85% slower and unsupported for .NET; native ARM needs a full arm64 rebuild. **This disqualifies the cheapest tiers** — Hetzner **CAX** (Ampere ARM), Oracle Ampere A1, AWS Graviton are all ARM. Apple-Silicon-via-Rosetta is the only ARM exception (separate Mac-runner plan). Hetzner **CX** (Intel/AMD, no vendor choice) and **CPX** (AMD EPYC) are amd64; both CX and CPX are **shared vCPU** (CCX is the dedicated line).

## Cost — burst mode (destroy-after-run)

Hetzner bills **hourly, rounded up to a full hour, capped at the monthly price**. A 20-min suite = 1 billed hour per host per run. Post-15-Jun-2026 prices (EUR, re-verify before committing — Hetzner changed prices twice in 2026):

| Burst config | Per run (1 hr each, rounded up) | 100 runs/mo |
|---|---|---|
| 1× CPX51 (16/32, fat) | ~€0.10 | ~€10 |
| 1× CPX41 (8/16) | ~€0.045 | ~€4.50 |
| **3× CX42 (8/16, wide) — RECOMMENDED** | 3 × ~€0.027 = **~€0.082** | **~€8.20** |
| 4× CX42 | ~€0.11 | ~€11 |

Wide ≈ same spend as fat under burst, and wide wins wall-clock. Target: 3× independent capacity gates → aim ~3× wall-clock cut (20 min → ~7-8 min), bounded by the longest single test chain.

## The burst trap (the real engineering work)

**You must `terraform destroy`, NOT stop** — a stopped/powered-off Hetzner server keeps billing until deleted (verified, Hetzner billing FAQ). A burst fleet is cheap ONLY if teardown is guaranteed even on failure:

- Terraform `destroy` in a CI `always()` / post-job step (runs even when tests fail).
- **A safety reaper** (cron or max-age tag) that deletes any fleet host older than ~2 h, to catch the case where the CI runner itself dies mid-run and never reaches teardown.

The provisioning is easy; **guaranteed teardown is the part that must not be skipped** (a leaked host silently accrues the full monthly cap).

## Recommendation

**3× Hetzner CX42 (8 vCPU / 16 GB, amd64), spun up per run via Terraform, destroyed after — ~€8/month at 100 runs.** Each host `serverSlots:2-3, clientSlots:4-6`. Wide beats fat for wall-clock at ~the same burst cost; medium is the size floor for co-placed server+clients.

Caveats: Steam tests need ≥2 accounts per Steam-capable host (≥6 for 3 hosts, or pin Steam to one host); fresh-account fleet cap ~10-25 is fine for 3-4 hosts (no quota request needed).

## Sequence (cheapest-first)

1. **FREE experiment — tune the existing host before spending.** `vps-1` is at `serverSlots:3` and achieved only 2.1× parallelism on 10 cores. Bump `serverSlots` (e.g. 3 → 5-6, `clientSlots` proportionally) in `.env.test`, run the full suite once, compare `durationMs` and the achieved-parallelism ratio. This isolates how much of the 20 min is config vs genuinely needing more hosts — a one-line change, one run, €0. **Watch for:** RAM pressure (no `instance_stats` today, so enable `SDVD_TEST_STATS=docker+game` on this run to finally measure per-container footprint and confirm 32 GB isn't the new ceiling at higher slot counts).
2. **THEN build the burst fleet** if step 1 shows the single host is genuinely capped: Terraform module (3× CX42, Cloud-Init Docker install, SSH key injection), `always()` destroy + max-age reaper, and a script rendering the provisioned IPs into `SDVD_DOCKER_HOSTS` (with inline key, CI pattern). Compare tuned-single vs 3-wide wall-clock.
3. **Measure, don't assume the 3× cut.** The wall-clock floor is the longest single test chain; if one slow test class dominates, more hosts won't help past that — surface it from `activeDurationTotalMs` per test before/after.

## Open data gaps

- **Per-container memory is unmeasured** (no `instance_stats` in any recorded run). Run step-1's experiment with `SDVD_TEST_STATS=docker+game` to get real per-role footprint via the resource-limits plan's sizing jq — needed to confirm host RAM floor and whether 32 GB caps the slot bump.
- **Hetzner per-tier prices changed twice in 2026** (1 Apr, 15 Jun); re-verify on `hetzner.com/cloud/regular-performance` before committing a budget.

## Provenance note (why this plan trusts some numbers and not others)

- **Trusted (measured, this repo):** `vps-1` = 10C/32GB/x86_64; current `serverSlots:3 clientSlots:6`; 20.3-min wall-clock; 2.1× achieved parallelism; ~41s boot. All from `TestResults/runs/` artifacts.
- **NOT trusted as requirements (padded estimates):** "~2 GB/server, ~1 GB/client, ~512 MB steam-auth" are conservative `p95 × 1.5` *caps* from the unimplemented `tests-container-resource-limits.md` plan — never measured here (zero `instance_stats`). True working set is likely lower. "~1 vCPU each" had no source and is withdrawn.
- **Carried from prior verified research:** amd64-disqualifies-ARM; Hetzner CX/CPX amd64 + shared-vCPU + hourly-billed + delete-not-stop; fleet cap ~10-25. See memory `test-runner-vps-host-options.md`.
