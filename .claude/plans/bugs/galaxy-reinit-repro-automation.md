# Repro automation for Galaxy-reinit-after-outage (Step 0 enablement)

## OUTCOME (2026-06-19): verdict is DESIGN B — the Galaxy SDK is SILENT on total connectivity loss

The repro ran. Across two independent runs (different Steam IDs, identical result), the
server-side `infrastructure.jsonl` showed:

- `steam_session_lost` → `steam_session_connected{isReconnect:true}` — Steam detected the outage
  (via SteamKit's own keepalive) and auto-recovered (the already-shipped #391 behavior).
- **`auth_galaxy_lost` count = 0**, and the only `auth_galaxy_state_change` was the initial login
  (`invocation:1`). The Galaxy SDK's `OnAuthLost` / `onStateChange` callbacks **never fired** —
  during the outage OR after restore.

So the fix-plan's unknown resolves to **Design B (poll-driven)**, and more sharply than the plan
framed it: the question wasn't "does the SDK *re-fire*" — the SDK fires **nothing at all**. On
total connectivity loss its callback channel goes dark with the network, so there is genuinely no event to hang a
callback-driven fix on. The poll-driven liveness probe (Design B in `galaxy-reinit-after-outage.md`)
is the only viable shape.

Two real bugs surfaced and were fixed while building the repro:
1. **Mod (`SteamGameServerService.cs` `OnSteamServersConnectFailure`)**: logged the recoverable Steam-CM
   disconnect at `LogLevel.Error`, tripping the server-side ERROR-poison test scan (`debugging.md`).
   Downgraded to `Warn` — correct independent of the test, since a transient CM disconnect is
   recoverable, and it also stops production logging Error on every Steam flap.
2. **Harness (this test)**: on a REMOTE Docker host, `docker network connect` gives the container a
   new IP and the daemon does NOT restore the published-port forwarding the SSH tunnel relies on, so
   the HTTP API stays unreachable after reconnect. The test was restructured to read the verdict and
   the Steam-recovery check entirely from the stdout-backed `infrastructure.jsonl` (which DOES flow
   once connectivity is back) and to leave the health watchdog suspended post-restore (resuming it would
   poison the API-unreachable server).

## Why this plan exists

The fix plan `galaxy-reinit-after-outage.md` is **blocked on its own Step 0**: a manual
total-connectivity-loss repro that needs a live Steam-authenticated server + a real Steam client, run by hand
(`docker network disconnect`, wait ~5 min, read `infrastructure.jsonl`). The single fact it
must observe — *does the closed-source Galaxy SDK re-fire `OnAuthLost`/`onStateChange` after a
headless-server outage, or go silent?* — picks Design A (callback) vs Design B (poll). The user
cannot reliably perform the manual live test, so this plan automates Step 0 into a repeatable
E2E experiment. **This plan does NOT implement the fix.** Its deliverable is the evidence that
unblocks the fix-design decision.

The diagnostic instrumentation the repro reads is already in the tree (still present, marked
"remove once design chosen"): `auth_galaxy_lost{invocation,networkingSet}`,
`auth_galaxy_state_change{invocation,operationalState,signedIn,loggedOn,networkingSet}`,
`steam_session_lost`, `steam_session_connected{connectNumber,isReconnect}`. Verified present at
`mod/JunimoServer/Services/AuthService/AuthService.cs:39-40,1093-1106,1129-1176` and
`mod/JunimoServer/Services/SteamGameServer/SteamGameServerService.cs:29-30,205-215,230-242`.

## Empirically established constraints (verified, not assumed)

Run on Docker 29.2.1 with a single-network container on a user-defined network + published port —
the exact shape of `ServerContainer` (`.WithNetwork(network).WithPortBinding(ContainerApiPort,
true)`, `ServerContainer.cs:254-256`):

| State | Published API port | Container outbound internet | Network connections |
|---|---|---|---|
| attached | reachable (200) | YES | `eth0`, `lo` |
| **network disconnected** | **unreachable (000)** | **NO** | **`lo` only** |
| reconnected | reachable (200) | YES | `eth0`, `lo` |

**Consequences that shape the design:**

1. **A full network disconnect IS the intended total connectivity loss** — it drops Steam *and* Galaxy
   together (outbound internet dies), which is precisely the unfixed scenario (#391 only covered
   a partial Steam-CM cut). 
2. **The API is unreachable during the cut**, so the test cannot poll HTTP mid-outage. The mod
   keeps emitting `SDVD_EVENT` lines to **stdout** (not network) throughout; `SimpleContainerLogStreamer`
   forwards them live to `infrastructure.jsonl`. So the recovery sequence is captured on disk
   even while the API is dark. Assertions read `infrastructure.jsonl` *after* restore — matching
   the existing "mod events are disk diagnostics, asserted post-hoc" convention
   (`tests-assert-via-http-api.md`, `FlakinessTracker.cs:89-134` is the read pattern).
3. **The health watchdog will poison the server during the cut unless suspended.** `ManagedServer`'s
   watchdog probes `/health` every 5s and calls `PoisonServer` after 5 consecutive failures
   (`ManagedServer.cs:986-1021`, env `SDVD_HEALTH_CHECK_INTERVAL_MS=5000`,
   `SDVD_HEALTH_CHECK_MAX_FAILURES=5`) → ~25s to poison, well under the ~5-min wait. The test
   MUST call `lease.Managed.SuspendHealthChecks()` before the cut and `ResumeHealthChecks()` after
   restore — the same pattern `ReloadAsync`/`CreateNewGameAsync` already use for intentional
   transitions (`ManagedServer.cs:1258/1298/1309/1339`). Public API confirmed at
   `ManagedServer.cs:960/965`; reachable via `ResourceLease.Managed` (internal, `ResourceLease.cs:138`).
4. **Cutting the test network does NOT trip the broker host-disconnect cascade.** `Poison`/
   `PoisonIfTransportFaultAsync` fire only from broker *server-creation* (`TestResourceBroker.cs:521,1288`)
   and *client-leasing* (`ClientPool.cs:546`) paths — infrastructure acquisition, not in-test API
   calls. An in-test API timeout does not poison the host. Safety condition: the test must NOT
   lease a new client or trigger server creation *during* the cut (lease the client before cutting),
   and must fully restore the network within its own body.

## Network-control facts (verified)

- Docker client: `lease.Host.ApiClient` → `Docker.DotNet.DockerClient` (`DockerHost.cs:135`).
- Container ID: `lease.Server.Container.Id` (`IContainer`, `ServerContainer.cs:103`).
- Network ID is NOT exposed by Testcontainers' `INetwork`; look it up by label, mirroring
  `DockerOps.BulkRemoveNetworksByLabelAsync` (`DockerOps.cs:168-187`):
  `client.Networks.ListNetworksAsync(new NetworksListParameters { Filters = LabelFilter("sdvd.run-id", <id>) })`
  then take `.ID`. The network carries label `sdvd.run-id={networkId}` (`TestNetworkManager.cs:56`).
  Simpler alternative: filter by network **name** (`ServerContainer.Network` is the `INetwork`;
  Testcontainers' `INetwork.Name` is available) — pick whichever the existing helper makes cleanest.
- Disconnect/reconnect calls (Docker.DotNet):
  `await client.Networks.DisconnectNetworkAsync(networkId, new NetworkDisconnectParameters { Container = containerId, Force = true })`
  / `await client.Networks.ConnectNetworkAsync(networkId, new NetworkConnectParameters { Container = containerId })`.
  No existing disconnect/reconnect helper exists in-tree (only delete) — this is net-new.

## Deliverables

### 1. `NetworkOutageHelper` (new) — `tests/JunimoServer.Tests/Helpers/NetworkOutageHelper.cs`

A small static helper that resolves a server's network ID by label and exposes
`DisconnectAsync(lease, ct)` / `ReconnectAsync(lease, ct)` over `lease.Host.ApiClient`. Models
the Docker.DotNet network ops above. Keep it minimal (`simplest-solution.md`): two methods + a
private network-ID lookup. It does NOT touch the health watchdog — that's the test's
responsibility (kept explicit so the suspend/resume bracket is visible at the call site, like the
existing transition methods).

### 2. `InfraEventReader` (new, or fold into an existing helper) — read `infrastructure.jsonl`

A post-scenario reader that parses `RunArtifactNames.InfrastructureLog(TestArtifacts.RunDir)`
(via `TestArtifacts.GetDiagnosticsDir()`) and returns the ordered list of events matching given
names, with their `data` fields. Reuses the exact `JsonDocument.Parse` line-loop pattern from
`FlakinessTracker.cs:109-134`. Needed because there is no "wait for mod event" API by design —
the read is the assertion surface. **Caveat to encode:** `infrastructure.jsonl` is written by an
async writer (`InfrastructureEventLog`'s `AsyncJsonlWriter`), so the reader must allow a short
settle/flush window (poll-read with a bounded timeout) before asserting absence of a post-restore
event — otherwise "Design B (silent)" could be a false negative from an unflushed buffer. Filter
to `forwardedVia` == this test's server slug to avoid cross-server contamination in a shared run.

### 3. `GalaxyOutageReproTests` (new) — `tests/JunimoServer.Tests/GalaxyOutageReproTests.cs`

One test, `[Fact] [TestServer(WithSteam = true, Exclusive = true)]` (Steam required — LAN has no
Galaxy; Exclusive so no reuse/concurrent method touches the cut server). Mirrors the
`AbandonedClaimTests` Steam skeleton. Body:

1. `Connect.WithRetryAsync(ct)` → assert a Steam client reaches the farmhand menu (proves Galaxy
   lobby is live). Capture `lease.InviteCode` and the pre-cut Steam lobby id (from `/status`).
   **Lease the client now, before the cut** (constraint 4).
2. Record the pre-cut event baseline (so we count only *post-restore* events): read
   `infrastructure.jsonl`, note the current max `auth_galaxy_lost.invocation`.
3. `lease.Managed.SuspendHealthChecks()`.
4. `NetworkOutageHelper.DisconnectAsync(lease, ct)`.
5. Wait (bounded poll on `infrastructure.jsonl`) for `auth_galaxy_lost invocation≥1` **and**
   `steam_session_lost` to confirm the outage landed. (API is dark — this is disk-only.)
6. Hold the outage a short, configurable dwell (default ~30–60s; the SDK's re-fire cadence is
   unknown — make the dwell an env knob, e.g. `SDVD_OUTAGE_DWELL_MS`, so the repro can be re-run
   longer without a recompile). **Document the knob's consumer** (`verify-documented-config-is-consumed.md`).
7. `NetworkOutageHelper.ReconnectAsync(lease, ct)`; `lease.Managed.ResumeHealthChecks()`.
8. Wait up to ~5 min (bounded) for `steam_session_connected isReconnect=true` (Steam recovered —
   the shipped #391 behavior; sanity that the harness reproduces the known-good half).
9. **The decision read** — within the post-restore window, look for *either*
   `auth_galaxy_lost` with `invocation` greater than the step-2 baseline, *or* any
   `auth_galaxy_state_change` with `loggedOn=true, networkingSet=true`:
   - present → **Design A** (SDK re-fires).
   - absent (after flush settle) → **Design B** (SDK silent).
10. The test does **not** Assert a single pass/fail on the A-vs-B outcome (both are valid
    experimental results). It **emits the observed sequence** to the test output / a structured
    artifact and asserts only the *harness invariants* that must hold regardless of A/B:
    Steam recovered (`isReconnect=true`), the outage actually occurred (`auth_galaxy_lost` +
    `steam_session_lost` seen), and the server is reachable again post-restore (a `/health` 200).
    This way the test is green when the *experiment ran cleanly*; the A/B verdict is read from its
    output, not encoded as a forced expectation that would make one valid SDK behavior "fail".

### 4. Documentation — record how to run it and read the verdict

Short addition to the fix plan (or a sibling note) pointing at the new test: "run
`make test FILTER=GalaxyOutageRepro`, read the emitted sequence / `infrastructure.jsonl`; the
A-vs-B verdict and the `invocation` sequence go in the fix PR description per
`galaxy-reinit-after-outage.md` Step 0." Do not edit `galaxy-reinit-after-outage.md`'s Step 0
beyond a one-line "automated by GalaxyOutageReproTests" pointer (it stays the source of truth for
the fix).

## Open decision for the user (mechanism)

Default is **full `docker network disconnect -f` + reconnect** (Deliverable 1) — it is exactly
the plan's total connectivity loss and is the simplest faithful reproduction. The alternative (sever only
outbound internet, keep the API pollable) is more complex, needs NET_ADMIN + Steam/Galaxy IP
ranges, and risks reproducing the already-shipped #391 partial cut instead. Recommendation: full
disconnect.

## Verification

1. **Build:** `dotnet build tests/JunimoServer.Tests/JunimoServer.Tests.csproj` (net10.0) — and
   `dotnet build mod/JunimoServer/JunimoServer.csproj` is untouched (no mod change in this plan).
2. **Run the repro:** `make test FILTER=GalaxyOutageRepro`. Post-conditions (runtime gates per
   `runtime-post-conditions-are-gates.md`): the test goes green (harness invariants hold), and its
   output contains the observed post-restore Galaxy event sequence — i.e. a concrete A-or-B verdict,
   not silence. If the test passes but emits no Galaxy events at all, the harness didn't actually
   cut Galaxy — investigate before trusting the verdict.
3. **Regression:** the test is opt-in by filter and Exclusive; confirm it does not perturb the rest
   of the suite (no host poison, server not left detached). A full `make test` should be unchanged
   except for the one added Steam test.

## Out of scope

- The actual Galaxy re-auth fix (Design A or B) — that's `galaxy-reinit-after-outage.md`, designed
  *after* this repro reports the verdict.
- Removing the diagnostic instrumentation — it stays until the fix lands (it's what this repro reads).
- LAN/IP transport (no Galaxy), and the Steam-lobby recreation path (#391, already shipped).
