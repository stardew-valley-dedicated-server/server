---
paths:
  - "tests/JunimoServer.Tests/Containers/**"
  - "tests/JunimoServer.Tests/Helpers/Docker*.cs"
  - "tests/JunimoServer.Tests/Helpers/ContainerStatsCollector.cs"
  - "tests/JunimoServer.Tests/Infrastructure/**"
---

# Docker test resource patterns

Patterns for creating Docker resources (containers, networks, volumes) in the E2E test harness via Testcontainers + Docker.DotNet.

- **Networks**: Testcontainers `NetworkBuilder` supports `.WithLabel()` — use it directly. Don't create networks via CLI then wrap with `NetworkBuilder` (Testcontainers will conflict).
- **Volumes**: create via Docker.DotNet (`DockerOps.CreateVolumeAsync`), which sets the `sdvd.test`/`sdvd.run-id` labels at create time and is idempotent per docker semantics.
- **Container labels**: `.WithCreateParameterModifier(p => { p.Labels[...] = ... })`.
- **Resource tagging convention**: `sdvd.test=true` and `sdvd.run-id={id}` labels for cleanup correlation.

**Every Testcontainers builder must call `.WithDockerEndpoint(host.EndpointConfig)`** (`Helpers/DockerEndpointConfig.cs`, owned per host on `DockerHost`). `DockerEndpointConfig` wraps the Testcontainers-resolved auth and builds the Docker.DotNet client via `GetDockerClientBuilder`, pinning `NPipeTransportOptions.ConnectTimeout` to 5s for named-pipe endpoints. The named-pipe connect timeout governs how long an individual Docker API call waits for the daemon's pipe to accept the connection; too short a value fails calls under parallel-startup pressure on Windows (Docker Desktop's named-pipe accept queue saturates). The override is applied only for `npipe://` endpoints — `WithTransportOptions(NPipeTransportOptions)` selects the transport, so applying it to a TCP/Unix endpoint would force the wrong transport.

**Symptom of the missing endpoint config**: `TimeoutException("The operation has timed out.")` from `_serverContainer.StartAsync` with empty `containers/server-N/container.log` and elapsed << configured `StartupTimeout`. **Do not fix this with retries** — fix it by setting `NPipeTransportOptions.ConnectTimeout` to ~5s via `host.EndpointConfig`. Linux/CI uses Unix sockets and is unaffected, but the call is safe everywhere.

**Per-host Docker.DotNet consumers go through `host.ApiClient`.** `ContainerStatsCollector`, `ManagedServer`, and the volume-create / force-remove helpers in `DockerOps` all take a `DockerClient` parameter and route per-host. `DockerEndpointConfig.Instance` is the local default and is only valid for coordinator-scoped callers that have no `DockerHost` (image distribution, run metadata, emergency cleanup); per-host call sites must use `host.ApiClient`.

**The coordinator requires a live local Docker daemon even for remote-only fleets.** The image build (`make build` via the docker CLI's default context), `ImageDistributor`, and `GameDataDistributor` all build/source from the local daemon and push to remotes — there is no build-on-remote path. A remote-only `SDVD_DOCKER_HOSTS` with local Docker stopped passes preflight (remotes are reachable over their tunnels) and then fails at the parent image build with a connect error on the default endpoint.

**One Docker host per `DockerHost` record.** `HostPool` parses `SDVD_DOCKER_HOSTS` (a JSON array of host entries; `endpoint` omitted for the local default daemon). Each `DockerHost` owns its endpoint, API client, per-host capacity gates (`HostCapacityQueue` for server slots, another for client slots), and a per-host `StartLimiter` capping concurrent `docker create+start` on that daemon. The `StartLimiter` size resolves per-host: JSON `concurrentStarts` field → `SDVD_MAX_CONCURRENT_STARTS` env (if set) → host's own `serverSlots + clientSlots`. Reuse caches are per-host by structure: `ManagedServer` is bound to its host's `ClientPool`, so config-hash collisions across hosts are structurally impossible.

**Coordinator-side ports are pre-picked.** Container code doesn't read `127.0.0.1:R` ports directly — it goes through `TunnelManager.OpenAsync(host, sshDest, mappedPort)` which returns a coordinator-side port. For local hosts the coordinator-side port equals the daemon-side mapped port; for remote (`ssh://`) hosts it's the loopback port from `ssh -L` opened against the host's `ControlMaster` socket.

**Ryuk (Testcontainers' resource reaper) is disabled.** `ModuleInit.cs` sets `TestcontainersSettings.ResourceReaperEnabled = false`. Cleanup is in-tree via `EmergencyCleanup` (per-resource Register/Unregister + startup sweep keyed on the `sdvd.test=true` label). Ryuk's connect-back from the test process to its container's mapped port is structurally incompatible with `ssh://` Docker endpoints (Testcontainers' `DockerContainer.Hostname` throws "endpoint not supported" for any non-{tcp,http,unix,npipe} scheme), and Testcontainers' reaper is a process-global singleton that cannot be instantiated per-host without private API. Do not re-enable it. If you add new Docker resources, label them `sdvd.test=true` so the in-tree sweep finds them.

**Why:** The endpoint timeout fix was reached by the user pushing back on a proposed retry loop ("hint: retries", "no bandaids"). The retry was masking a 100ms timeout default that surfaces only under Windows + parallel startup pressure — exactly the conditions a CI run never hits but local dev always does. See `retry-is-evidence-of-root-cause.md` for the meta-rule.

**How to apply:** When adding a new Testcontainers consumer, take a `DockerHost host` parameter and call `.WithDockerEndpoint(host.EndpointConfig)`. When adding a per-host Docker.DotNet consumer, take the `DockerClient` from `host.ApiClient` (do NOT create a fresh client off the local default). Reach for `DockerEndpointConfig.Instance` only when the caller is coordinator-scoped with no `DockerHost` available. When adding code that constructs a URL to a container, route the port through `TunnelManager.OpenAsync` so the call works identically on local and remote hosts.
