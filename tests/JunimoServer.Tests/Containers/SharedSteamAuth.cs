using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Json;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JunimoServer.Tests.Containers;

/// <summary>
/// Manages a per-host shared steam-auth container. Every server and client
/// container on a given host reaches steam-auth via the Docker network alias
/// <c>"steam-auth"</c> on that host's bridge — the alias is identical across
/// hosts because each host has its own independent bridge network, so a
/// container's <c>STEAM_AUTH_URL</c> resolves to a host-local target without
/// any cross-host plumbing.
///
/// <para>Each host's steam-auth serves a *disjoint slice* of the global
/// <c>STEAM_ACCOUNTS</c> array (see <see cref="JunimoServer.Tests.Infrastructure.SteamAccountSlicer"/>).
/// SteamKit enforces a single live session per Steam account; if two
/// containers on different hosts logged in with the same account, one would
/// evict the other and ping-pong on every request. Disjoint slicing is the
/// only design that doesn't fight the protocol.</para>
/// </summary>
public class SharedSteamAuth : IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly string _runId;
    private readonly string _containerName;
    private readonly ContainerLogFile _containerLog;
    private readonly CancellationTokenSource _logStreamCts = new();
    private readonly ContainerLogStreamReader _logStreamReader;
    private readonly Task _logStreamTask;

    // Per-host context. The steam-auth container runs on a specific host; the
    // mod inside server containers reaches it via the "steam-auth" network
    // alias on that host's bridge, so steam-auth must be created on the same
    // host as the servers that will use it.
    private readonly Infrastructure.DockerHost _host;
    private readonly Infrastructure.TunnelManager _tunnels = Infrastructure.TunnelManager.Default;
    private string HostId => _host.Id;
    private string? SshDestination => _host.SshDestination;
    private string? SshKeyPath => _host.SshKeyPath;
    private Infrastructure.ForwardLease? _apiForward;

    private const int ContainerPort = 3001;
    public const string NetworkAlias = "steam-auth";

    private SharedSteamAuth(IContainer container, string runId, string containerName, Infrastructure.DockerHost host)
    {
        _container = container;
        _runId = runId;
        _containerName = containerName;
        _host = host;
        _containerLog = new ContainerLogFile("steam-auth-shared");
        _logStreamReader = new ContainerLogStreamReader(
            _host.ApiClient,
            _container,
            "steam-auth-shared",
            HandleLine);
        // SuppressFlow: shared-steam log streaming runs for the whole process and
        // emits across every test. Inheriting the constructing test's
        // TestContext.Current poisons every later log line.
        // See .claude/rules/asynclocal-pitfalls.md.
        using (ExecutionContext.SuppressFlow())
        {
            _logStreamTask = Task.Run(() => _logStreamReader.RunAsync(_logStreamCts.Token));
        }
    }

    private void HandleLine(string line)
    {
        // Forward any SDVD_EVENT structured event lines from the steam-auth
        // sidecar (e.g. account-login state transitions) to infrastructure.jsonl,
        // and write the human-readable line to the per-container log file.
        SimpleContainerLogStreamer.TryForwardSdvdEvent(line, "steam-auth-shared");
        _containerLog.WriteLine(line);
    }

    /// <summary>
    /// Creates and starts a per-host steam-auth container on the given host's
    /// network. The container serves <paramref name="steamAccountsJson"/> — the
    /// host's slice of the global <c>STEAM_ACCOUNTS</c> array, renumbered from
    /// 0 (slice-local indices are what containers see and what the wire format
    /// carries; the global → slice-local shift lives at the allocator boundary).
    /// </summary>
    /// <param name="steamAccountsJson">
    /// The host's slice of <c>STEAM_ACCOUNTS</c> as a renumbered-from-0 JSON array.
    /// </param>
    public static async Task<SharedSteamAuth> CreateAndStartAsync(
        INetwork network,
        string imageTag,
        string gameDataVolume,
        string steamSessionVolume,
        CancellationToken ct,
        Infrastructure.DockerHost host,
        string? steamAccountsJson)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        TestRunRegistry.Register(runId);

        // Container name and emergency-cleanup key are per-host: multiple
        // SharedSteamAuth instances coexist on the same run (one per Steam-
        // capable host), so {runId} alone is not unique. {hostId}-{runId} is.
        var containerName = $"sdvd-steam-auth-{host.Id}-{runId}";

        var builder = new ContainerBuilder()
            .WithDockerEndpoint(host.EndpointConfig)
            .WithLogger(NullLogger.Instance)
            .WithImage($"sdvd/steam-service:{imageTag}")
            .WithImagePullPolicy(imageTag == "local" ? PullPolicy.Never : PullPolicy.Missing)
            .WithName(containerName)
            .WithNetwork(network)
            .WithNetworkAliases(NetworkAlias)
            .WithPortBinding(ContainerPort, true)
            .WithVolumeMount(steamSessionVolume, "/data/steam-session")
            .WithVolumeMount(gameDataVolume, "/data/game")
            .WithEnvironment("PORT", ContainerPort.ToString())
            .WithEnvironment("GAME_DIR", "/data/game")
            .WithEnvironment("SESSION_DIR", "/data/steam-session")
            .WithCreateParameterModifier(p =>
            {
                p.HostConfig.CapAdd ??= new List<string>();
                p.HostConfig.CapAdd.Add("SYS_TIME");
                p.Labels ??= new Dictionary<string, string>();
                p.Labels["sdvd.test"] = "true";
                p.Labels["sdvd.run-id"] = runId;
                p.Labels["sdvd.host-id"] = host.Id;
            })
            // Defer to the image's baked-in HEALTHCHECK ("dotnet SteamService.dll
            // healthcheck"). Testcontainers reads the daemon-reported health
            // status, so this works for both local and remote daemons without
            // needing curl/wget inside the container (the steam-service runtime
            // image has neither).
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilContainerIsHealthy());

        // Inject the host's slice. The global STEAM_ACCOUNTS is intentionally NOT
        // forwarded — each host gets its disjoint slice instead.
        if (!string.IsNullOrEmpty(steamAccountsJson))
            builder = builder.WithEnvironment("STEAM_ACCOUNTS", steamAccountsJson);

        // Steam allows only one live login per account: a second SteamKit2 client
        // logging in with a username we already plan to use will kick our login with
        // LogonSessionReplaced. Detect a non-test sidecar already holding any of our
        // planned usernames on this host and abort fast — operator decides whether to
        // stop the conflicting container or remove the colliding entry from the test's
        // STEAM_ACCOUNTS env. Never tear down the conflicting container ourselves.
        await AssertNoSteamAccountConflictAsync(host, steamAccountsJson, ct);

        var container = builder.Build();

        var emergencyKey = $"SharedSteamAuth-{host.Id}-{runId}";
        var capturedHost = host;
        var capturedName = containerName;
        EmergencyCleanup.Register(emergencyKey, () =>
        {
            try { DockerOps.ForceRemoveContainerSync(capturedHost.ApiClient, capturedName); }
            catch { }
        });

        // Instantiate first so the streaming loop is running before StartAsync;
        // the streamer gracefully retries while the container is not yet created.
        var instance = new SharedSteamAuth(container, runId, containerName, host);
        var startSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await container.StartAsync(ct);
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("container_started", new
            {
                role = "steam-auth-shared",
                name = containerName,
                image = $"sdvd/steam-service:{imageTag}",
                runId,
                startupMs = startSw.ElapsedMilliseconds,
                host_id = host.Id
            });
        }
        catch (Exception ex)
        {
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("container_start_failed", new
            {
                role = "steam-auth-shared",
                name = containerName,
                image = $"sdvd/steam-service:{imageTag}",
                runId,
                elapsedMs = startSw.ElapsedMilliseconds,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
                host_id = host.Id
            });
            throw;
        }

        // Resolve the coordinator-visible host port via TunnelManager. Local
        // hosts: pass-through. Remote hosts: opens a per-forward `ssh -N -L`.
        var mapped = container.GetMappedPublicPort(ContainerPort);
        instance._apiForward = await instance._tunnels.OpenAsync(
            instance.HostId, instance.SshDestination, instance.SshKeyPath, mapped, ct);

        return instance;
    }

    /// <summary>
    /// Returns the steam-auth URL for server containers to use (via Docker network).
    /// </summary>
    public string GetUrlForServer() => $"http://{NetworkAlias}:{ContainerPort}";

    /// <summary>
    /// Returns the coordinator-visible port for the container's API. Goes
    /// through TunnelManager so callers do not need to know whether the host
    /// is local or remote.
    /// </summary>
    public int GetHostPort()
    {
        if (_apiForward == null)
            throw new InvalidOperationException("SharedSteamAuth not started yet — host port unavailable.");
        return _apiForward.CoordinatorPort;
    }

    /// <summary>
    /// Verifies that every account in 0..accountCount-1 reports logged_in == true on the
    /// steam-auth /health endpoint within TestTimings.SteamAccountReadyTimeout. Throws
    /// InvalidOperationException with an actionable message naming the failing account
    /// indices and pointing the operator at `make setup`.
    ///
    /// Polls /health (a side-effect-free status snapshot, see tools/steam-service/Program.cs).
    /// On timeout, takes one final probe to build a deterministic failure message reflecting
    /// state at the moment of failure, not whatever the polling loop last observed.
    /// </summary>
    public async Task WaitForAccountsLoggedInAsync(int accountCount, CancellationToken ct)
    {
        if (accountCount <= 0) return;

        var hostUrl = GetHostUrl();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var ok = await PollingHelper.WaitUntilAsync(
            name: WaitName.Polling_SharedSteamAuth_AccountsReady,
            condition: async () =>
            {
                var snapshot = await TryGetHealthSnapshotAsync(http, hostUrl, ct);
                return AllAccountsReady(accountCount, snapshot);
            },
            timeout: TestTimings.SteamAccountReadyTimeout,
            pollInterval: TimeSpan.FromSeconds(2),
            cancellationToken: ct);

        if (!ok)
        {
            var snapshot = await TryGetHealthSnapshotAsync(http, hostUrl, ct);
            var notReady = ComputeNotReady(accountCount, snapshot);
            throw new InvalidOperationException(BuildPreflightFailureMessage(notReady, hostUrl, snapshot is null));
        }
    }

    /// <summary>
    /// Single-shot per-account health probe. Returns true if the account at the given index
    /// reports logged_in=true on the sidecar's /health endpoint right now. Polling is the
    /// caller's responsibility (the broker's allocator runs the loop with backoff).
    /// </summary>
    public async Task<bool> IsAccountHealthyAsync(int accountIndex, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var snapshot = await TryGetHealthSnapshotAsync(http, GetHostUrl(), ct);
        var entry = snapshot?.Accounts?.FirstOrDefault(a => a.Index == accountIndex);
        return entry?.LoggedIn ?? false;
    }

    private string GetHostUrl() => $"http://localhost:{GetHostPort()}";

    private static async Task<HealthResponse?> TryGetHealthSnapshotAsync(
        HttpClient http, string hostUrl, CancellationToken ct)
    {
        try
        {
            var json = await http.GetStringAsync($"{hostUrl}/health", ct);
            return JsonSerializer.Deserialize<HealthResponse>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static bool AllAccountsReady(int accountCount, HealthResponse? snapshot)
    {
        if (snapshot?.Accounts == null) return false;
        for (var i = 0; i < accountCount; i++)
        {
            var entry = snapshot.Accounts.FirstOrDefault(a => a.Index == i);
            if (entry == null || !entry.LoggedIn) return false;
        }
        return true;
    }

    private static List<int> ComputeNotReady(int accountCount, HealthResponse? snapshot)
    {
        var notReady = new List<int>();
        for (var i = 0; i < accountCount; i++)
        {
            var entry = snapshot?.Accounts?.FirstOrDefault(a => a.Index == i);
            if (entry == null || !entry.LoggedIn) notReady.Add(i);
        }
        return notReady;
    }

    private static string BuildPreflightFailureMessage(List<int> notReady, string hostUrl, bool noResponse)
    {
        if (noResponse)
        {
            return $"Steam preflight failed: no response from steam-auth at {hostUrl} within " +
                $"{TestTimings.SteamAccountReadyTimeout.TotalSeconds:F0}s. " +
                "The steam-auth container may have crashed during startup. " +
                "Check container logs and run `make setup` to re-authenticate.";
        }

        var indices = string.Join(", ", notReady);
        return $"Steam preflight failed: account(s) [{indices}] not logged in within " +
            $"{TestTimings.SteamAccountReadyTimeout.TotalSeconds:F0}s. " +
            "This usually means a saved Steam session is missing or invalid. " +
            "Run `make setup` to re-authenticate, then retry the test run.";
    }

    /// <summary>
    /// Throws if any non-test sdvd/steam-service container on this host is configured to
    /// log in with a username the test sidecar plans to use. The test path never kills
    /// non-test containers — surfaces the conflict and points the operator at the env var
    /// they need to edit. Two SteamKit2 clients sharing one account always end with one
    /// of them taking LogonSessionReplaced; the only safe fix is disjoint usernames.
    /// </summary>
    private static async Task AssertNoSteamAccountConflictAsync(
        Infrastructure.DockerHost host,
        string? steamAccountsJson,
        CancellationToken ct)
    {
        var planned = ExtractUsernames(steamAccountsJson);
        if (planned.Count == 0) return;

        IList<ContainerListResponse> containers;
        try
        {
            containers = await host.ApiClient.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = false, // running only — paused/exited containers don't hold Steam sessions
            }, ct);
        }
        catch (Exception ex)
        {
            // Daemon unreachable here means StartAsync below would fail anyway with a clearer
            // error; don't double-throw with a confusing conflict-probe message.
            InfrastructureEventLog.Emit("steam_conflict_probe_skipped", new
            {
                host_id = host.Id,
                exceptionType = ex.GetType().Name,
                message = ex.Message,
            });
            return;
        }

        foreach (var c in containers)
        {
            var image = c.Image ?? "";
            if (!image.StartsWith("sdvd/steam-service", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Labels != null && c.Labels.TryGetValue("sdvd.test", out var testLabel) && testLabel == "true") continue;

            ContainerInspectResponse inspect;
            try
            {
                inspect = await host.ApiClient.Containers.InspectContainerAsync(c.ID, ct);
            }
            catch { continue; }

            var existing = ExtractUsernamesFromEnv(inspect.Config?.Env);
            var overlap = planned.Intersect(existing, StringComparer.OrdinalIgnoreCase).ToList();
            if (overlap.Count == 0) continue;

            var name = (c.Names != null && c.Names.Count > 0 ? c.Names[0].TrimStart('/') : c.ID[..12]);
            var overlapList = string.Join(", ", overlap);
            throw new InvalidOperationException(
                $"Steam account conflict on host '{host.Id}': container '{name}' " +
                $"(image '{image}') is already configured to use Steam username(s) [{overlapList}]. " +
                "Steam allows only one live login per account; running the test sidecar with the same " +
                "username(s) would kick the existing login (LogonSessionReplaced). " +
                $"Fix: remove [{overlapList}] from the test's STEAM_ACCOUNTS env (typically in .env.test) " +
                $"so the test sidecar uses a disjoint set, or stop container '{name}' if it is no longer needed. " +
                "The test harness will not stop non-test containers automatically.");
        }
    }

    private static HashSet<string> ExtractUsernames(string? steamAccountsJson)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(steamAccountsJson)) return set;
        foreach (var node in UserConfigJson.ParseArrayStrict("STEAM_ACCOUNTS", steamAccountsJson))
        {
            var user = node["user"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(user)) set.Add(user);
        }
        return set;
    }

    private static HashSet<string> ExtractUsernamesFromEnv(IList<string>? env)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (env == null) return set;

        string? steamAccounts = null;
        string? steamUsername = null;
        foreach (var line in env)
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var val = line[(eq + 1)..].Trim();
            if (val.Length == 0) continue;
            if (key.Equals("STEAM_ACCOUNTS", StringComparison.OrdinalIgnoreCase)) steamAccounts = val;
            else if (key.Equals("STEAM_USERNAME", StringComparison.OrdinalIgnoreCase)) steamUsername = val;
        }

        // Mirror tools/steam-service/Program.cs DiscoverAccounts — STEAM_ACCOUNTS wins;
        // on its absence the sidecar falls back to STEAM_USERNAME for account 0.
        if (steamAccounts != null)
        {
            try
            {
                foreach (var node in UserConfigJson.ParseArrayStrict("STEAM_ACCOUNTS", steamAccounts))
                {
                    var user = node["user"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(user)) set.Add(user);
                }
                return set;
            }
            catch { /* malformed JSON in the *other* container's env — fall through to legacy */ }
        }

        if (steamUsername != null) set.Add(steamUsername);
        return set;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private class HealthResponse
    {
        [JsonPropertyName("accounts")]
        public List<HealthAccountStatus>? Accounts { get; set; }
    }

    private class HealthAccountStatus
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("logged_in")]
        public bool LoggedIn { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_apiForward != null) { try { await _apiForward.DisposeAsync(); } catch { } _apiForward = null; }

        // Drain the log-stream reader before closing the file sink so any
        // fully-formed lines already split out of the in-flight chunk land in
        // container.log / infrastructure.jsonl. Per drain-before-consume-
        // disposal.md.
        try { await _logStreamReader.DrainAsync(TimeSpan.FromSeconds(2)); } catch { }
        try { await _logStreamReader.DisposeAsync(); } catch { }
        _logStreamCts.Cancel();
        try { await _logStreamTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        _logStreamCts.Dispose();
        await _containerLog.DisposeAsync();

        string? preDisposeState = null;
        try { preDisposeState = _container.State.ToString(); } catch { }

        long? exitCode = null;
        try
        {
            using var ecCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            exitCode = await _container.GetExitCodeAsync(ecCts.Token);
        }
        catch { /* exit code is advisory */ }

        var stopSw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _container.DisposeAsync();
        }
        catch
        {
            try { await DockerOps.ForceRemoveContainerAsync(_host.ApiClient, _containerName); }
            catch { }
        }

        stopSw.Stop();
        JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("container_stopped", new
        {
            role = "steam-auth-shared",
            name = _containerName,
            runId = _runId,
            preDisposeState,
            exitCode,
            disposeDurationMs = stopSw.ElapsedMilliseconds,
            host_id = HostId
        });

        if (exitCode == 137)
        {
            JunimoServer.Tests.Helpers.InfrastructureEventLog.Emit("container_oom_killed", new
            {
                role = "steam-auth-shared",
                name = _containerName,
                runId = _runId,
                host_id = HostId
            });
        }

        EmergencyCleanup.Unregister($"SharedSteamAuth-{HostId}-{_runId}");
        TestRunRegistry.Unregister(_runId);
    }
}
