using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Json;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Process-wide singleton owning the configured Docker hosts. Parses
/// <c>SDVD_DOCKER_HOSTS</c> on first access and runs preflight against each
/// daemon (Docker.DotNet <c>System.GetVersionAsync</c> + <c>GetSystemInfoAsync</c>)
/// so the run fails loudly if any daemon is unreachable rather than failing per
/// test.
///
/// <para>
/// <c>SDVD_DOCKER_HOSTS</c> is a JSON array. Each entry must specify
/// <c>id</c>, <c>serverSlots</c>, and <c>clientSlots</c>. Optional fields:
/// <c>endpoint</c> (omit for local daemon, or <c>ssh://user@host</c>),
/// <c>sshKey</c> (either a path to a private key — <c>~</c>-expanded — or inline
/// <c>-----BEGIN…</c> key material, which is written to a 0600 temp file), and
/// <c>socketPath</c> (remote Unix socket; defaults to <c>/var/run/docker.sock</c>,
/// override e.g. <c>~/.docker/run/docker.sock</c> for macOS Docker Desktop).
/// </para>
/// <example>
/// <code>
/// SDVD_DOCKER_HOSTS='[
///   {"id": "local", "serverSlots": 3, "clientSlots": 6},
///   {"id": "vps",   "endpoint": "ssh://sdvd-runner@10.0.0.2", "sshKey": "~/.ssh/sdvd_runner",
///                   "serverSlots": 2, "clientSlots": 4},
///   {"id": "mac",   "endpoint": "ssh://dev@mac.local", "socketPath": "~/.docker/run/docker.sock",
///                   "serverSlots": 1, "clientSlots": 2}
/// ]'
/// </code>
/// </example>
///
/// <para>
/// <c>SDVD_DOCKER_HOSTS</c> is required. A run with it unset throws fast at
/// startup — set it to a JSON array of host entries.
/// </para>
/// </summary>
public sealed class HostPool : IAsyncDisposable
{
    private static readonly Lazy<HostPool> _instance = new(() => new HostPool());

    public static HostPool Instance => _instance.Value;

    public IReadOnlyList<DockerHost> Hosts { get; }
    public DockerHost First => Hosts[0];

    private HostPool()
    {
        Hosts = ParseAndBuild();

        // EmergencyCleanup may run at process exit before InitializeAsync has
        // materialized every host's ApiClient. Filter to initialized hosts so
        // a partially-set-up pool doesn't throw inside the cleanup handler.
        HostPoolAccessor.Register(() =>
        {
            var initialized = new List<(DockerClient, bool)>();
            foreach (var h in Hosts)
            {
                try
                {
                    initialized.Add((h.ApiClient, !string.IsNullOrEmpty(h.SshDestination)));
                }
                catch (InvalidOperationException)
                { /* not initialized — skip */
                }
            }
            return initialized;
        });
    }

    /// <summary>
    /// Picks a host for a server placement. Prefers hosts whose
    /// <see cref="DockerHost.ServerCapacity"/> can admit immediately;
    /// otherwise queues on the host with the fewest waiters (per the plan,
    /// snapshot reads are racy-by-design, so this is approximately fair).
    /// Poisoned hosts are skipped.
    ///
    /// <para>When <paramref name="requireSteam"/> is true, only hosts whose
    /// per-host steam-auth came up healthy (<see cref="DockerHost.IsSteamCapable"/>)
    /// are considered. The two filters are orthogonal: a Steam-capable host that
    /// later gets poisoned is correctly excluded by the <c>!IsPoisoned</c> check.
    /// </para>
    /// </summary>
    public DockerHost Place(int serverSlotsNeeded, int clientSlotsNeeded, bool requireSteam = false)
    {
        var alive = Hosts.Where(h => !h.IsPoisoned && (!requireSteam || h.IsSteamCapable)).ToList();
        if (alive.Count == 0)
        {
            if (requireSteam)
                throw new InvalidOperationException(
                    "No Steam-capable hosts available — check STEAM_ACCOUNTS sizing vs SDVD_DOCKER_HOSTS slot config. "
                        + "Each Steam-capable host needs ≥2 accounts (1 server + ≥1 client)."
                );
            throw new InvalidOperationException("HostPool: all hosts poisoned");
        }

        // Prefer admitting hosts (server capacity available now). Tiebreak by
        // free client slots, then declared order (host0 first).
        var admitting = alive
            .Where(h => h.ServerCapacity.Available >= serverSlotsNeeded)
            .OrderByDescending(h => h.ClientCapacity.Available)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .ToList();
        if (admitting.Count > 0)
            return admitting[0];

        // Nothing admits immediately: queue on the host with the fewest server
        // waiters, ties by declared order.
        return alive
            .OrderBy(h => h.ServerCapacity.WaitingCount)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .First();
    }

    private static IReadOnlyList<DockerHost> ParseAndBuild()
    {
        var raw = Environment.GetEnvironmentVariable("SDVD_DOCKER_HOSTS");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                "SDVD_DOCKER_HOSTS is required. "
                    + "Set it to a JSON array of host entries — see .env.test.example for the schema."
            );

        var entries = UserConfigJson.DeserializeArrayStrict<HostEntry>(
            "SDVD_DOCKER_HOSTS",
            raw,
            "{id, serverSlots, clientSlots, [endpoint], [sshKey]}"
        );
        if (entries is null || entries.Length == 0)
            throw new InvalidOperationException(
                "SDVD_DOCKER_HOSTS is set but parsed to an empty array. "
                    + "At least one host entry is required."
            );

        // SDVD_MAX_CONCURRENT_STARTS, when set, becomes the default for every
        // host that omits its own concurrentStarts. When unset, each host
        // defaults to its own serverSlots+clientSlots — sized to start every
        // container the host is provisioned to run, concurrently.
        var startsEnvOverride = ParseOptionalPositiveInt("SDVD_MAX_CONCURRENT_STARTS");
        var extractEnvOverride = ParseOptionalPositiveInt("SDVD_MAX_CONCURRENT_EXTRACTIONS");

        var hosts = new List<DockerHost>(entries.Length);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidOperationException(
                    $"SDVD_DOCKER_HOSTS entry {i}: 'id' is required."
                );
            if (!seenIds.Add(entry.Id))
                throw new InvalidOperationException(
                    $"SDVD_DOCKER_HOSTS entry {i}: duplicate id '{entry.Id}' (ids must be unique)."
                );
            if (entry.ServerSlots <= 0)
                throw new InvalidOperationException(
                    $"SDVD_DOCKER_HOSTS entry '{entry.Id}': 'serverSlots' must be a positive integer (got {entry.ServerSlots})."
                );
            if (entry.ClientSlots <= 0)
                throw new InvalidOperationException(
                    $"SDVD_DOCKER_HOSTS entry '{entry.Id}': 'clientSlots' must be a positive integer (got {entry.ClientSlots})."
                );
            // Resolution order for each cap: per-host JSON field (if positive)
            // → SDVD_MAX_* env (if set & positive) → host's serverSlots+clientSlots.
            var concurrentStarts =
                entry.ConcurrentStarts > 0
                    ? entry.ConcurrentStarts
                    : startsEnvOverride ?? entry.ServerSlots + entry.ClientSlots;
            var concurrentExtractions =
                entry.ConcurrentExtractions > 0
                    ? entry.ConcurrentExtractions
                    : extractEnvOverride ?? entry.ServerSlots + entry.ClientSlots;

            var sshDest = ResolveSshDestination(entry.Id, entry.Endpoint);
            var sshKeyPath = ResolveSshKeyPath(entry.Id, sshDest, entry.SshKey);
            var socketPath = ResolveSocketPath(entry.Id, sshDest, entry.SocketPath);
            hosts.Add(
                new DockerHost(
                    id: entry.Id,
                    sshDestination: sshDest,
                    sshKeyPath: sshKeyPath,
                    serverSlots: entry.ServerSlots,
                    clientSlots: entry.ClientSlots,
                    concurrentStarts: concurrentStarts,
                    concurrentExtractions: concurrentExtractions,
                    hasGpu: entry.Gpu,
                    remoteSocketPath: socketPath
                )
            );
        }
        return hosts;
    }

    /// <summary>
    /// Resolves the user@host form for `ssh -L`. Returns null for local-default
    /// entries (no endpoint or non-ssh scheme). Throws on malformed URIs.
    /// </summary>
    private static string? ResolveSshDestination(string id, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"SDVD_DOCKER_HOSTS entry '{id}': endpoint '{endpoint}' is not a valid URI. "
                    + "Use 'ssh://user@machine' for remote hosts."
            );

        if (!uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SDVD_DOCKER_HOSTS entry '{id}': endpoint '{endpoint}' uses scheme '{uri.Scheme}' but only 'ssh' is supported. "
                    + "Drop the endpoint field for local hosts, or use 'ssh://user@machine' for remote."
            );

        return string.IsNullOrEmpty(uri.UserInfo) ? uri.Host : $"{uri.UserInfo}@{uri.Host}";
    }

    /// <summary>
    /// Resolves a host's <c>sshKey</c> to a file path <c>ssh -i</c> can consume.
    /// Accepts two forms:
    /// <list type="bullet">
    ///   <item><b>Inline key material</b> (the value starts with
    ///   <c>-----BEGIN</c>): the PEM/OpenSSH-format private key is written to a
    ///   private (0600) temp file and that path is returned. This is what CI
    ///   uses — the whole <c>SDVD_DOCKER_HOSTS</c> JSON (key included) lives in
    ///   one GitHub secret, with no separate key-file step.</item>
    ///   <item><b>A path</b> (anything else): resolved against the project root
    ///   (<c>~</c>-expanded, relative paths anchored at the repo so <c>./foo</c>
    ///   works regardless of the runner's CWD) and required to exist. This is
    ///   the local-dev form pointing at <c>~/.ssh/&lt;key&gt;</c>.</item>
    /// </list>
    /// Either way the result is a filesystem path; every downstream consumer
    /// (<see cref="DockerHost.SshKeyPath"/>, <see cref="TunnelManager"/>'s
    /// <c>ssh -i</c>) is path-based and unchanged.
    /// </summary>
    private static string? ResolveSshKeyPath(string id, string? sshDest, string? sshKey)
    {
        if (string.IsNullOrWhiteSpace(sshKey))
            return null;
        if (string.IsNullOrEmpty(sshDest))
            throw new InvalidOperationException(
                $"SDVD_DOCKER_HOSTS entry '{id}': 'sshKey' is set but 'endpoint' is local. "
                    + "Drop sshKey for local entries."
            );

        // Inline PEM/OpenSSH key material (CI passes the key inside the JSON
        // secret). OpenSSH and PEM private keys begin with a `-----BEGIN` armor
        // line; a filesystem path never contains one. So a `-----BEGIN`
        // *anywhere* in the value means inline was intended — match loosely on
        // Contains (not StartsWith) so a stray leading character can't misroute
        // the key into the path branch, whose "not found" error would otherwise
        // echo the key bytes into logs.
        var looksInline = sshKey.Contains("-----BEGIN", StringComparison.Ordinal);
        if (looksInline)
        {
            if (!sshKey.TrimStart().StartsWith("-----BEGIN", StringComparison.Ordinal))
                // Inline was intended but the armor isn't at the start (leading
                // junk / wrong indentation). Fail WITHOUT echoing the key.
                throw new InvalidOperationException(
                    $"SDVD_DOCKER_HOSTS entry '{id}': sshKey looks like inline key material "
                        + "but does not start with '-----BEGIN'. Provide the key with the armor "
                        + "line first and no leading characters. (Key content omitted from this error.)"
                );
            return MaterializeInlineKey(id, sshKey);
        }

        // Otherwise treat as a path. Relative paths anchor at the project root,
        // not the binary's CWD, so `./foo` works regardless of how the runner
        // was launched. Safe to echo `sshKey` here — it is a path, not a key.
        var full = Helpers.ProjectRoot.Resolve(sshKey);
        if (!File.Exists(full))
            throw new InvalidOperationException(
                $"SDVD_DOCKER_HOSTS entry '{id}': sshKey '{sshKey}' is neither inline key "
                    + $"material (a '-----BEGIN…' block) nor an existing file (resolved to '{full}')."
            );
        return full;
    }

    /// <summary>
    /// Writes inline private-key material to a 0600 temp file and returns its
    /// path. The filename is derived from a hash of the key content so the
    /// coordinator and the xUnit child — which independently re-parse
    /// <c>SDVD_DOCKER_HOSTS</c> — converge on the same file rather than racing
    /// two writes of divergently-named files. A trailing newline is ensured
    /// because OpenSSH rejects a key file whose final armor line lacks one.
    /// </summary>
    private static string MaterializeInlineKey(string id, string keyMaterial)
    {
        var normalized = keyMaterial.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
        var hash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..16];
        var path = Path.Combine(Path.GetTempPath(), $"sdvd-test-sshkey-{hash}");

        // Content-addressed: a matching file from a sibling process (parent +
        // xUnit child both parse this) or a prior run holds these exact bytes
        // already. Create-new-only — if it exists, the content is identical, so
        // reuse it. The file is created 0600 atomically (UnixCreateMode applies
        // at open(2)), so the key bytes are never momentarily group/world-
        // readable in the temp dir — no write-then-chmod window.
        if (File.Exists(path))
            return path;

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using var stream = new FileStream(path, options);
            using var writer = new StreamWriter(stream);
            writer.Write(normalized);
        }
        catch (IOException) when (File.Exists(path))
        {
            // A sibling process created it between our Exists check and
            // CreateNew. Same content — its file is fine.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"SDVD_DOCKER_HOSTS entry '{id}': failed to materialize inline sshKey "
                    + $"to '{path}': {ex.Message}",
                ex
            );
        }

        return path;
    }

    /// <summary>
    /// Resolves the remote daemon socket path. Returns null for local entries
    /// (the local daemon is reached via OS default endpoint, no socket-path
    /// concept on this side). Defaults to <c>/var/run/docker.sock</c> for
    /// remote entries; override for hosts where the daemon listens elsewhere
    /// (macOS Docker Desktop: <c>~/.docker/run/docker.sock</c>). The path is
    /// not expanded coordinator-side — it's threaded into the remote
    /// <c>ssh -L &lt;port&gt;:&lt;path&gt;</c> command and resolved by the remote shell.
    /// </summary>
    private static string? ResolveSocketPath(string id, string? sshDest, string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
            return string.IsNullOrEmpty(sshDest) ? null : null; // remote default applied in DockerHost
        if (string.IsNullOrEmpty(sshDest))
            throw new InvalidOperationException(
                $"SDVD_DOCKER_HOSTS entry '{id}': 'socketPath' is set but 'endpoint' is local. "
                    + "Drop socketPath for local entries."
            );
        return socketPath;
    }

    private static int? ParseOptionalPositiveInt(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var v) && v > 0 ? v : null;
    }

    private sealed class HostEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("endpoint")]
        public string? Endpoint { get; set; }

        [JsonPropertyName("sshKey")]
        public string? SshKey { get; set; }

        [JsonPropertyName("serverSlots")]
        public int ServerSlots { get; set; }

        [JsonPropertyName("clientSlots")]
        public int ClientSlots { get; set; }

        /// <summary>
        /// Whether this host has an NVIDIA GPU + Container Toolkit available.
        /// Defaults to false (omit the field for hosts without GPU). Per-host so
        /// a multi-host fleet can mix GPU-equipped (local workstation) and
        /// CPU-only (cheap VPS) machines without all containers requesting GPU.
        /// </summary>
        [JsonPropertyName("gpu")]
        public bool Gpu { get; set; }

        /// <summary>
        /// Remote Unix socket path for the daemon. Defaults to
        /// <c>/var/run/docker.sock</c> when omitted. Override for hosts whose
        /// daemon listens elsewhere — most commonly macOS Docker Desktop, which
        /// uses <c>~/.docker/run/docker.sock</c> per-user. Ignored for local
        /// entries (no SSH endpoint).
        /// </summary>
        [JsonPropertyName("socketPath")]
        public string? SocketPath { get; set; }

        /// <summary>
        /// Per-host bound on concurrent Docker container starts on this daemon.
        /// When omitted or non-positive, falls back to <c>SDVD_MAX_CONCURRENT_STARTS</c>
        /// (if set) and otherwise to this host's <c>serverSlots + clientSlots</c>.
        /// Independent across hosts, so a beefy daemon and a laptop can carry
        /// different caps.
        /// </summary>
        [JsonPropertyName("concurrentStarts")]
        public int ConcurrentStarts { get; set; }

        /// <summary>
        /// Per-host bound on concurrent video-extraction operations on this daemon
        /// (ffmpeg TS→MP4 concat + Docker tar pull at run-end). When omitted or
        /// non-positive, falls back to <c>SDVD_MAX_CONCURRENT_EXTRACTIONS</c> (if
        /// set) and otherwise to this host's <c>serverSlots + clientSlots</c>.
        /// Independent of <c>concurrentStarts</c> — different daemon code paths.
        /// </summary>
        [JsonPropertyName("concurrentExtractions")]
        public int ConcurrentExtractions { get; set; }
    }

    /// <summary>
    /// Initializes every host (opens daemon-socket SSH forwards for remotes,
    /// builds Docker.DotNet clients), then runs preflight against each daemon
    /// (version + system info). Emits one <c>docker_preflight</c> event per
    /// host. Throws if any host is unreachable so the run fails loudly rather
    /// than per-test.
    /// </summary>
    public async Task PreflightAsync(TunnelManager tunnels, CancellationToken ct = default)
    {
        // Clear any stale tunnel-port map from a prior run in the same shell
        // session. If preflight fails partway, the child process inherits an
        // empty map and its lazy DockerHost getter will throw with a clear
        // message rather than dialing a stale loopback port.
        Environment.SetEnvironmentVariable(RunArtifactNames.HostTunnelsEnv, "{}");

        var hasRemoteHost = Hosts.Any(h => !string.IsNullOrEmpty(h.SshDestination));
        if (hasRemoteHost)
        {
            // Resolve a Cygwin-built ssh (banner-checked) and pin it for every
            // subsequent ssh invocation. Then sweep the temp dir for stale
            // ControlMaster sockets from prior crashed runs (sibling files
            // owned by another user on shared /tmp are tolerated via
            // permission/IO error swallowing inside CleanupStaleControlSockets).
            var sshPath = await SshBinaryResolver.ResolveAsync(ct);
            tunnels.SetSshPath(sshPath);
            var staleDeleted = TunnelManager.CleanupStaleControlSockets(TimeSpan.FromHours(1));
            InfrastructureEventLog.Emit(
                "ssh_preflight",
                new { sshPath, staleSocketsDeleted = staleDeleted }
            );

            // Open one ControlMaster per remote host before any forward open.
            // RegisterHostMasterAsync deletes this run's exact ControlPath
            // first (specific cleanup) and runs `ssh -O check` after spawn to
            // catch the silent-multiplex-disable failure mode.
            foreach (var host in Hosts)
            {
                if (string.IsNullOrEmpty(host.SshDestination))
                    continue;
                try
                {
                    await tunnels.RegisterHostMasterAsync(
                        host.Id,
                        host.SshDestination!,
                        host.SshKeyPath,
                        ct
                    );
                }
                catch (Exception ex)
                {
                    InfrastructureEventLog.Emit(
                        "docker_preflight_failed",
                        new
                        {
                            host_id = host.Id,
                            exceptionType = ex.GetType().Name,
                            message = ex.Message,
                        }
                    );
                    throw new InvalidOperationException(
                        $"SSH ControlMaster preflight failed for {host.Id} ({host.SshDestination}): {ex.Message}",
                        ex
                    );
                }
            }

            // Propagate the resolved ssh binary and the registered masters
            // so the xUnit child (a separate process spawned by the v3 runner)
            // can run `ssh -O forward` against the parent's existing sockets
            // without spawning its own masters.
            Environment.SetEnvironmentVariable(RunArtifactNames.SshPathEnv, sshPath);
            Environment.SetEnvironmentVariable(
                RunArtifactNames.SshHostMastersEnv,
                tunnels.SerializeRegisteredMasters()
            );
        }
        else
        {
            Environment.SetEnvironmentVariable(RunArtifactNames.SshHostMastersEnv, "{}");
        }

        foreach (var host in Hosts)
        {
            try
            {
                await host.InitializeAsync(tunnels, ct: ct);
                var version = await host.ApiClient.System.GetVersionAsync(ct);
                var info = await host.ApiClient.System.GetSystemInfoAsync(ct);
                InfrastructureEventLog.Emit(
                    "docker_preflight",
                    new
                    {
                        host_id = host.Id,
                        daemonVersion = version?.Version,
                        apiVersion = version?.APIVersion,
                        cpuCount = (int)info.NCPU,
                        totalMemoryMb = info.MemTotal / (1024.0 * 1024.0),
                        operatingSystem = info.OperatingSystem,
                        kernelVersion = info.KernelVersion,
                        serverVersion = info.ServerVersion,
                        architecture = info.Architecture,
                    }
                );
            }
            catch (Exception ex)
            {
                InfrastructureEventLog.Emit(
                    "docker_preflight_failed",
                    new
                    {
                        host_id = host.Id,
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                    }
                );
                throw new InvalidOperationException(
                    $"Docker preflight failed for {host.Id} ({host.SshDestination ?? "local"}): {ex.Message}",
                    ex
                );
            }
        }

        // Propagate per-host coordinator ports for the xUnit child process.
        // The child's lazy DockerHost getters read this env var to dial the
        // parent's `ssh -N -L` loopback listener (kernel binding is reachable
        // from any process on the host).
        var tunnelMap = new Dictionary<string, int>();
        foreach (var h in Hosts)
        {
            var port = h.GetCoordinatorPortOrZero();
            if (port > 0)
                tunnelMap[h.Id] = port;
        }
        Environment.SetEnvironmentVariable(
            RunArtifactNames.HostTunnelsEnv,
            JsonSerializer.Serialize(tunnelMap)
        );
    }

    public async ValueTask DisposeAsync()
    {
        HostPoolAccessor.Clear();
        foreach (var host in Hosts)
        {
            try
            {
                await host.DisposeAsync();
            }
            catch { }
        }
    }
}
