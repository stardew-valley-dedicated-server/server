using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;

namespace JunimoServer.Tests.Infrastructure;

/// <summary>
/// Owns one persistent <c>ssh -M</c> ControlMaster per remote host plus the
/// per-port <c>ssh -O forward</c> / <c>-O cancel</c> reuse calls that go
/// through it. Local hosts pass through — every method returns the
/// Testcontainers-mapped port directly.
///
/// <para>Plan invariants:
/// <list type="bullet">
///   <item><see cref="OpenAsync"/> is the ONLY way code outside this class learns
///     a port for a container on a given host. Callers must not assume the
///     coordinator-side port equals the daemon-side mapped port.</item>
///   <item>For local hosts the coordinator-side port IS the daemon-side mapped
///     port (no SSH involved); for remote hosts they are different and the
///     coordinator-side port is opened by <c>ssh -O forward</c> against the
///     per-host ControlMaster on a freshly-picked loopback port.</item>
///   <item>Linux/macOS coordinators use upstream OpenSSH (system <c>ssh</c>);
///     Windows coordinators require Git for Windows' Cygwin-built ssh
///     (<c>C:\Program Files\Git\usr\bin\ssh.exe</c>). The Microsoft port at
///     <c>C:\Windows\System32\OpenSSH\ssh.exe</c> is rejected at preflight
///     because its named-pipe transport doesn't carry the ancillary data
///     needed for ControlMaster fd-passing. <see cref="SshBinaryResolver"/>
///     enforces the rule.</item>
/// </list>
/// </para>
/// </summary>
public sealed class TunnelManager : IAsyncDisposable
{
    /// <summary>
    /// Process-wide pass-through manager used by container code that has no
    /// host context.
    /// </summary>
    public static readonly TunnelManager Default = new();

    private readonly object _lock = new();
    private readonly Dictionary<ForwardKey, ForwardEntry> _forwards = new();
    private readonly Dictionary<string, HostMaster> _masters = new(StringComparer.Ordinal);
    private string _sshPath = "ssh";

    // Caps concurrent `ssh -O` reuse invocations (forward / cancel / check) that hit one
    // shared ControlMaster's mux listener. An unbounded burst of these can exhaust the
    // master's accept backlog / fd budget — the master then logs `accept: Resource
    // temporarily unavailable`, stops answering, and the whole host is lost (see
    // host-poison-deadlocks-run.md). The spawn itself is exempt (it has no master yet).
    // Sized generously; override via SDVD_SSH_OP_CONCURRENCY. Process-wide on Default.
    private readonly SemaphoreSlim _sshOpGate = new(
        int.TryParse(Environment.GetEnvironmentVariable("SDVD_SSH_OP_CONCURRENCY"), out var c)
        && c > 0
            ? c
            : 6
    );

    // ControlMaster keepalive: probe every Interval s; declare the host dead after
    // CountMax consecutive missed probes (so the forward-drop window is Interval×CountMax).
    private static readonly int KeepAliveInterval = EnvInt("SDVD_SSH_KEEPALIVE_INTERVAL", 15);
    private static readonly int KeepAliveCountMax = EnvInt("SDVD_SSH_KEEPALIVE_COUNT", 6);

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    /// <summary>
    /// Configures the resolved <c>ssh</c> binary path for every subsequent
    /// invocation. Set by <see cref="HostPool.PreflightAsync"/> after the
    /// banner check rejects the Microsoft Windows OpenSSH port.
    /// </summary>
    public void SetSshPath(string sshPath)
    {
        if (string.IsNullOrWhiteSpace(sshPath))
        {
            throw new ArgumentException("ssh path must be non-empty", nameof(sshPath));
        }

        _sshPath = sshPath;
    }

    public string SshPath => _sshPath;

    /// <summary>
    /// Spawns a long-lived <c>ssh -M</c> ControlMaster for <paramref name="hostId"/>
    /// and verifies it with <c>ssh -O check</c>. Idempotent per host.
    /// Throws on spawn or verification failure.
    /// </summary>
    public async Task RegisterHostMasterAsync(
        string hostId,
        string sshDestination,
        string? sshKeyPath,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(sshDestination))
        {
            throw new ArgumentException(
                "RegisterHostMasterAsync requires a remote SSH destination.",
                nameof(sshDestination)
            );
        }

        lock (_lock)
        {
            if (_masters.ContainsKey(hostId))
            {
                return;
            }
        }

        var controlPath = ComputeControlPath(hostId);
        // Per-host, run-scoped error log for the -f-forked master. -E redirects
        // ssh's stderr here (GetDiagnosticsDir self-creates the dir, and RunDir
        // is already set by RunMetadata.BeginRun before preflight runs).
        var logPath = ComputeMasterLogPath(hostId);

        // Specific delete: any file at this exact path is debris from a prior
        // crashed run with the same (hostId, runId, pid). Leaving it would make
        // `ssh -M` print "ControlSocket … already exists, disabling multiplexing"
        // to stderr and exit 0 — silent multiplex disable, every later -O check
        // fails with Bad file descriptor.
        TryDeleteFile(controlPath);

        var spawnedAt = Stopwatch.GetTimestamp();
        var (spawnExit, spawnStderr) = await SpawnMasterAsync(
            sshDestination,
            sshKeyPath,
            controlPath,
            logPath,
            ct
        );

        if (spawnExit != 0)
        {
            // -E moved ssh's stderr to the log, so the parent pipe may be empty
            // even on a real failure — fall back to the log tail.
            var spawnDiag = string.IsNullOrEmpty(spawnStderr)
                ? ReadMasterLogTail(logPath, MaxLogTailBytes)
                : spawnStderr;
            EmitSafe(
                "ssh_master_spawn_failed",
                new
                {
                    host_id = hostId,
                    exitCode = spawnExit,
                    stderr = spawnDiag,
                    durationMs = ElapsedMs(spawnedAt),
                }
            );
            throw new InvalidOperationException(
                $"ssh -M spawn failed for {hostId} (exit {spawnExit}): {spawnDiag}"
            );
        }

        var (checkExit, checkStderr) = await RunCheckAsync(controlPath, sshDestination, ct);
        var masterRunning =
            checkExit == 0 && checkStderr.Contains("Master running", StringComparison.Ordinal);

        if (!masterRunning)
        {
            // Same -E-moves-stderr fallback as the spawn path.
            var spawnDiag = string.IsNullOrEmpty(spawnStderr)
                ? ReadMasterLogTail(logPath, MaxLogTailBytes)
                : spawnStderr;
            EmitSafe(
                "ssh_master_check_failed",
                new
                {
                    host_id = hostId,
                    exitCode = checkExit,
                    stderr = checkStderr,
                    spawnStderr = spawnDiag,
                    durationMs = ElapsedMs(spawnedAt),
                }
            );
            // Best-effort cleanup of whatever the spawn left behind so a retry
            // (or the next run with the same path) doesn't trip the "socket
            // already exists" branch.
            TryDeleteFile(controlPath);
            throw new InvalidOperationException(
                $"ssh -M did not produce a usable master for {hostId}. "
                    + $"spawn stderr: {spawnDiag}; -O check stderr: {checkStderr}"
            );
        }

        lock (_lock)
        {
            _masters[hostId] = new HostMaster
            {
                HostId = hostId,
                SshDestination = sshDestination,
                SshKeyPath = sshKeyPath,
                ControlPath = controlPath,
                LogPath = logPath,
                Owned = true,
            };
        }

        EmitSafe(
            "ssh_master_ready",
            new
            {
                host_id = hostId,
                controlPath,
                logPath,
                durationMs = ElapsedMs(spawnedAt),
            }
        );
    }

    private async Task<(int ExitCode, string Stderr)> SpawnMasterAsync(
        string sshDestination,
        string? sshKeyPath,
        string controlPath,
        string logPath,
        CancellationToken ct
    )
    {
        // -f forks ssh after auth; the parent exits 0 and the forked child
        // becomes the long-lived master. The Process handle returned by
        // Process.Start is the parent — once it exits we drop it. Don't try
        // to track the master via this handle (Kill/WaitForExit on it after
        // -f has fired won't reach the child). Reach the master only via
        // ControlPath: ssh -O check / -O forward / -O cancel / -O exit.
        var psi = NewSshPsi();
        AddIdentityArg(psi, sshKeyPath);
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ControlMaster=auto");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={controlPath}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ControlPersist=10m");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("Compression=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ServerAliveInterval={KeepAliveInterval}");
        // KeepAliveCountMax × Interval = how long a silent control-channel stall must last
        // before the master tears down every forward. The old 2×15=30s was SHORTER than the
        // harness's routine long ops (a docker pull/create can be silent for ~90s+), so a
        // transient stall during normal work dropped all forwards and cascaded a host poison
        // (reproduced 2026-06-26; the daemon stayed alive the whole time). Widened to 6×15=90s
        // so a brief blip is ridden out rather than fatal. The corroborate-then-heal path
        // (PoisonIfTransportFaultAsync) covers the rarer case where it does drop, so a longer
        // self-exit window is no longer a liability. Override via SDVD_SSH_KEEPALIVE_*.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ServerAliveCountMax={KeepAliveCountMax}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("TCPKeepAlive=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        // INFO, not ERROR: the silent-drop line "Timeout, server not responding."
        // is LOG_INFO, so ERROR would suppress the one line this log exists for.
        // A healthy -N master stays at 0 bytes, so the happy path stays lean.
        // -E *moves* ssh's stderr to the file (parent pipe goes empty), so the
        // spawn/check failure paths read the tail instead. Only the silent-timeout
        // drop lands here; an RST drop leaves it empty (caught by the classifier).
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("LogLevel=INFO");
        psi.ArgumentList.Add("-E");
        psi.ArgumentList.Add(logPath);
        psi.ArgumentList.Add(sshDestination);

        return await RunSshToCompletionAsync(psi, TimeSpan.FromSeconds(15), ct);
    }

    private async Task<(int ExitCode, string Stderr)> RunCheckAsync(
        string controlPath,
        string sshDestination,
        CancellationToken ct
    )
    {
        var psi = NewSshPsi();
        psi.ArgumentList.Add("-O");
        psi.ArgumentList.Add("check");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={controlPath}");
        psi.ArgumentList.Add(sshDestination);
        return await RunSshOpAsync(psi, TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Corroboration probe: does the host's SSH ControlMaster still answer
    /// <c>ssh -O check</c>? Returns false (never throws) when the master is gone
    /// (no entry, or socket removed → exit 255). The mid-run seams use this to
    /// resolve a bare <see cref="TimeoutException"/>: master dead ⇒ tunnel dead ⇒
    /// poison. NOTE: this checks master-*process* liveness, not tunnel liveness,
    /// so it has a bounded ~30s self-healing false-negative window — see the
    /// "Host disconnect cascades" invariant in <c>test-broker-invariants.md</c>.
    /// </summary>
    public async Task<bool> IsMasterAliveAsync(string hostId, CancellationToken ct = default)
    {
        // Lookup-then-hydrate-then-lookup, mirroring ResolveMasterOrThrow's body
        // but returning false on the second miss instead of throwing. The xUnit
        // child's _masters is empty until HydrateFromEnvIfPresent reads
        // SDVD_SSH_HOST_MASTERS; the -O check then works against the
        // filesystem-global ControlPath socket regardless of spawning process.
        HostMaster? master = TryGetMaster(hostId);
        if (master is null)
        {
            HydrateFromEnvIfPresent();
            master = TryGetMaster(hostId);
        }
        if (master is null)
        {
            return false;
        }

        try
        {
            var (exit, stderr) = await RunCheckAsync(master.ControlPath, master.SshDestination, ct);
            return exit == 0 && stderr.Contains("Master running", StringComparison.Ordinal);
        }
        catch
        {
            // -O check 255 on a removed socket, or any IO error → "already gone".
            return false;
        }
    }

    /// <summary>
    /// Confirms the host's ControlMaster is usable, tolerating the transient window where a
    /// keepalive blip has briefly broken even <c>ssh -O check</c> (the check needs a mux
    /// channel too, so a single failure right after a drop is a false negative — the master
    /// usually recovers within seconds while Docker stats keep flowing). Retries the check a
    /// few times; if it stays down, respawns the master once. Returns false only when the
    /// master is genuinely unrecoverable, so the caller can stop trying to heal and poison.
    /// The canonical "is the host actually gone, or just a forward blip?" primitive — used by
    /// the per-server forward heal and the API-client transparent heal.
    /// </summary>
    public async Task<bool> EnsureMasterUsableAsync(string hostId, CancellationToken ct = default)
    {
        const int checkAttempts = 4;
        for (var i = 0; i < checkAttempts; i++)
        {
            if (await IsMasterAliveAsync(hostId, ct))
            {
                return true;
            }
            if (i < checkAttempts - 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }

        // Check kept failing ⇒ master likely genuinely dead; one respawn attempt.
        return await TryRespawnMasterAsync(hostId, ct);
    }

    private HostMaster? TryGetMaster(string hostId)
    {
        lock (_lock)
        {
            return _masters.TryGetValue(hostId, out var m) ? m : null;
        }
    }

    /// <summary>
    /// Host ids whose ControlMaster this process OWNS (spawned, can respawn). Empty in the
    /// xUnit child (it only adopts read-only entries). The parent-side master-health monitor
    /// iterates these — only the owner can heal a wedged master, and crucially the respawn
    /// reuses the SAME deterministic ControlPath, so the child's adopted entry keeps working
    /// transparently (no re-publish needed).
    /// </summary>
    public IReadOnlyList<string> GetOwnedHostIds()
    {
        lock (_lock)
        {
            return _masters.Values.Where(m => m.Owned).Select(m => m.HostId).ToArray();
        }
    }

    /// <summary>
    /// Owner-side master health check + heal: if <c>ssh -O check</c> fails (mux wedged or
    /// process dead), respawn the master at its existing ControlPath. Returns true if the
    /// master is healthy (already, or after respawn). No-op true for a host this process
    /// doesn't own. The child CANNOT do this (TryRespawnMasterAsync refuses on !Owned) — that
    /// is the whole reason this must run in the parent.
    /// </summary>
    public async Task<bool> EnsureOwnedMasterHealthyAsync(
        string hostId,
        CancellationToken ct = default
    )
    {
        var master = TryGetMaster(hostId);
        if (master is null || !master.Owned)
        {
            return true; // not ours to heal
        }

        if (await IsMasterAliveAsync(hostId, ct))
        {
            return true;
        }

        // -O check failed ⇒ mux wedged or process gone. Respawn at the same ControlPath so
        // the child's adopted entry (and its in-flight forward re-opens) recover transparently.
        EmitSafe("ssh_master_unhealthy_owner", new { host_id = hostId });
        return await TryRespawnMasterAsync(hostId, ct);
    }

    /// <summary>
    /// Attempts to resurrect a dead/unresponsive ControlMaster once: evicts the stale
    /// entry + socket, then re-runs <see cref="RegisterHostMasterAsync"/> with the
    /// original destination/key. Returns true if a usable master is back. Used by the
    /// poison seam before condemning a host — one shared master carries every forward, so
    /// a transient master death (e.g. <c>accept: Resource temporarily unavailable</c> from
    /// fd/backlog exhaustion) otherwise loses the whole host even though the VPS is fine.
    /// Only the owner (parent) can respawn — an adopted child entry has no spawn rights, so
    /// it returns false and lets the normal poison proceed.
    /// </summary>
    public async Task<bool> TryRespawnMasterAsync(string hostId, CancellationToken ct = default)
    {
        HostMaster? master = TryGetMaster(hostId);
        if (master is null)
        {
            HydrateFromEnvIfPresent();
            master = TryGetMaster(hostId);
        }
        if (master is null || !master.Owned)
        {
            return false;
        }

        var destination = master.SshDestination;
        var keyPath = master.SshKeyPath;

        // Best-effort tear down the wedged-but-alive old master first (a mux wedge leaves the
        // process running but useless), so it doesn't linger orphaned after we re-bind the
        // ControlPath. Bounded; ignore the result — the file delete below is the hard reset.
        try
        {
            var exitPsi = NewSshPsi();
            exitPsi.ArgumentList.Add("-O");
            exitPsi.ArgumentList.Add("exit");
            exitPsi.ArgumentList.Add("-o");
            exitPsi.ArgumentList.Add($"ControlPath={master.ControlPath}");
            exitPsi.ArgumentList.Add(destination);
            await RunSshOpAsync(exitPsi, TimeSpan.FromSeconds(3), ct);
        }
        catch
        { /* wedged master may not answer -O exit; the socket delete is the fallback */
        }

        // Evict the dead entry + its socket so RegisterHostMasterAsync's ContainsKey guard
        // doesn't short-circuit and its `ssh -M` doesn't hit the "socket exists" trap.
        lock (_lock)
        {
            _masters.Remove(hostId);
        }
        TryDeleteFile(master.ControlPath);

        EmitSafe("ssh_master_respawn_attempt", new { host_id = hostId });
        try
        {
            await RegisterHostMasterAsync(hostId, destination, keyPath, ct);
            var alive = await IsMasterAliveAsync(hostId, ct);
            EmitSafe("ssh_master_respawned", new { host_id = hostId, alive });
            return alive;
        }
        catch (Exception ex)
        {
            EmitSafe("ssh_master_respawn_failed", new { host_id = hostId, error = ex.Message });
            return false;
        }
    }

    /// <summary>
    /// Adds <c>-i {keyPath}</c> when <paramref name="keyPath"/> is non-null,
    /// plus <c>-o IdentitiesOnly=yes</c> so an unrelated key in ssh-agent
    /// can't shadow the explicitly configured one. Used on master spawn only;
    /// reuse calls (<c>-O forward</c>/<c>cancel</c>/<c>exit</c>) don't need
    /// identity options because the master is already authenticated.
    /// </summary>
    private static void AddIdentityArg(ProcessStartInfo psi, string? keyPath)
    {
        if (string.IsNullOrEmpty(keyPath))
        {
            return;
        }

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(keyPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("IdentitiesOnly=yes");
    }

    /// <summary>
    /// Opens a forward from a coordinator-side loopback port to the given
    /// remote port on the host. Returns the coordinator-side port. For local
    /// hosts <paramref name="mappedPort"/> is returned unchanged. The returned
    /// <see cref="ForwardLease"/> closes the forward on dispose.
    /// </summary>
    public Task<ForwardLease> OpenAsync(
        string hostId,
        string? sshDestination,
        string? sshKeyPath,
        int mappedPort,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(sshDestination))
        {
            return Task.FromResult(
                new ForwardLease(this, hostId, mappedPort, mappedPort, isRemote: false)
            );
        }

        return OpenForwardCoreAsync(
            hostId,
            sshDestination!,
            sshKeyPath,
            mappedPort: mappedPort,
            remoteSocketPath: null,
            ct
        );
    }

    /// <summary>
    /// Opens a forward from a coordinator-side loopback TCP port to a Unix socket
    /// path on the remote host. OpenSSH supports <c>-L tcp:path</c> for this. Used
    /// to expose the remote Docker daemon's <c>/var/run/docker.sock</c> over a
    /// local TCP port that Docker.DotNet (which can't speak <c>ssh://</c>
    /// natively) can dial.
    /// </summary>
    public Task<ForwardLease> OpenSocketForwardAsync(
        string hostId,
        string sshDestination,
        string? sshKeyPath,
        string remoteSocketPath,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrEmpty(sshDestination))
        {
            throw new ArgumentException(
                "OpenSocketForwardAsync requires a remote SSH destination.",
                nameof(sshDestination)
            );
        }

        return OpenForwardCoreAsync(
            hostId,
            sshDestination,
            sshKeyPath,
            mappedPort: 0,
            remoteSocketPath: remoteSocketPath,
            ct
        );
    }

    private async Task<ForwardLease> OpenForwardCoreAsync(
        string hostId,
        string sshDestination,
        string? sshKeyPath,
        int mappedPort,
        string? remoteSocketPath,
        CancellationToken ct
    )
    {
        const int maxAttempts = 5;
        Exception? lastFailure = null;

        var master = ResolveMasterOrThrow(hostId);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var coordinatorPort = PickFreeLoopbackPort();
            var openStartedAt = Stopwatch.GetTimestamp();

            try
            {
                await OpenForwardOnMasterAsync(
                    master,
                    coordinatorPort,
                    mappedPort,
                    remoteSocketPath,
                    ct
                );

                // Safety net: -O forward returns 0 once ssh has set up the
                // forward, but kernel listener readiness is technically
                // separate. A short TCP probe catches any delay before we
                // hand the port back to a Docker.DotNet client.
                await ProbeListenerAsync(coordinatorPort, TimeSpan.FromSeconds(2), ct);

                lock (_lock)
                {
                    _forwards[new ForwardKey(hostId, coordinatorPort)] = new ForwardEntry
                    {
                        HostId = hostId,
                        SshDestination = sshDestination,
                        SshKeyPath = sshKeyPath,
                        CoordinatorPort = coordinatorPort,
                        MappedPort = mappedPort,
                        RemoteSocketPath = remoteSocketPath,
                        ControlPath = master.ControlPath,
                    };
                }

                EmitSafe(
                    "tunnel_forward_opened",
                    new
                    {
                        host_id = hostId,
                        coordinator_port = coordinatorPort,
                        mapped_port = remoteSocketPath is null ? (int?)mappedPort : null,
                        remote_socket = remoteSocketPath,
                        durationMs = ElapsedMs(openStartedAt),
                        attempts = attempt,
                    }
                );

                return new ForwardLease(this, hostId, coordinatorPort, mappedPort, isRemote: true);
            }
            catch (PortCollisionException ex)
            {
                EmitSafe(
                    "tunnel_forward_failed",
                    new
                    {
                        host_id = hostId,
                        coordinator_port = (int?)coordinatorPort,
                        mapped_port = remoteSocketPath is null ? (int?)mappedPort : null,
                        remote_socket = remoteSocketPath,
                        reason = "port_collision_retry",
                        message = ex.Message,
                        attempt,
                        attempts = maxAttempts,
                    }
                );
                lastFailure = ex;
                // fall through to next attempt
            }
            catch (OperationCanceledException ex)
            {
                EmitSafe(
                    "tunnel_forward_failed",
                    new
                    {
                        host_id = hostId,
                        coordinator_port = (int?)coordinatorPort,
                        mapped_port = remoteSocketPath is null ? (int?)mappedPort : null,
                        remote_socket = remoteSocketPath,
                        reason = "cancelled",
                        message = ex.Message,
                        attempt,
                        attempts = maxAttempts,
                    }
                );
                throw;
            }
            catch (Exception ex)
            {
                var reason = ex is ProbeTimeoutException ? "probe_timeout" : "forward_failed";
                EmitSafe(
                    "tunnel_forward_failed",
                    new
                    {
                        host_id = hostId,
                        coordinator_port = (int?)coordinatorPort,
                        mapped_port = remoteSocketPath is null ? (int?)mappedPort : null,
                        remote_socket = remoteSocketPath,
                        reason,
                        message = ex.Message,
                        attempt,
                        attempts = maxAttempts,
                    }
                );
                throw;
            }
        }

        var label = remoteSocketPath ?? $"127.0.0.1:{mappedPort}";
        throw new InvalidOperationException(
            $"Failed to open ssh -O forward for {hostId} → {label} after {maxAttempts} attempts: {lastFailure?.Message}",
            lastFailure
        );
    }

    private async Task OpenForwardOnMasterAsync(
        HostMaster master,
        int coordinatorPort,
        int mappedPort,
        string? remoteSocketPath,
        CancellationToken ct
    )
    {
        var target = remoteSocketPath ?? $"127.0.0.1:{mappedPort}";
        var psi = NewSshPsi();
        psi.ArgumentList.Add("-O");
        psi.ArgumentList.Add("forward");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={master.ControlPath}");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ExitOnForwardFailure=yes");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add($"127.0.0.1:{coordinatorPort}:{target}");
        psi.ArgumentList.Add(master.SshDestination);

        var (exit, stderr) = await RunSshOpAsync(psi, TimeSpan.FromSeconds(5), ct);
        if (exit == 0)
        {
            return;
        }

        if (LooksLikePortCollision(stderr))
        {
            throw new PortCollisionException(
                $"ssh -O forward exited (code {exit}); local bind collision on {coordinatorPort}: {stderr}"
            );
        }

        throw new InvalidOperationException(
            $"ssh -O forward failed (exit {exit}) for {master.HostId} → {target}: {stderr}"
        );
    }

    private static async Task ProbeListenerAsync(
        int coordinatorPort,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
            {
                throw new ProbeTimeoutException(
                    $"127.0.0.1:{coordinatorPort} did not accept within {timeout.TotalMilliseconds:F0}ms after ssh -O forward returned 0."
                );
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                deadline.Token
            );
            attemptCts.CancelAfter(TimeSpan.FromMilliseconds(200));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, coordinatorPort, attemptCts.Token);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            { /* deadline or per-attempt cap; loop */
            }
            catch (SocketException)
            { /* not yet listening; loop */
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            { /* deadline tripped; next iter handles */
            }
        }
    }

    private static bool LooksLikePortCollision(string stderr) =>
        stderr.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("cannot listen to port", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("forwarding request failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes a previously opened forward. Called from <see cref="ForwardLease.DisposeAsync"/>.
    /// The <paramref name="mappedPort"/> parameter is preserved for signature
    /// stability with <see cref="ForwardLease"/> but unused — the
    /// <c>(hostId, coordinatorPort)</c> key is unique per active forward.
    /// </summary>
    internal async ValueTask CloseAsync(string hostId, int coordinatorPort, int mappedPort)
    {
        ForwardEntry? entry;
        lock (_lock)
        {
            if (!_forwards.Remove(new ForwardKey(hostId, coordinatorPort), out entry))
            {
                return;
            }
        }

        await CancelForwardAsync(entry, TimeSpan.FromSeconds(2), via: "dispose");
    }

    /// <summary>
    /// Drains all open forwards in parallel, each bounded by
    /// <paramref name="perCancelTimeout"/>. The outer <paramref name="timeout"/>
    /// caps total drain time so a hung process can't extend shutdown. After
    /// every forward has been cancelled (or its cancel timed out), each host
    /// master is shut down with <c>ssh -O exit</c>.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout, TimeSpan perCancelTimeout)
    {
        ForwardEntry[] forwardSnapshot;
        HostMaster[] masterSnapshot;
        lock (_lock)
        {
            forwardSnapshot = _forwards.Values.ToArray();
            _forwards.Clear();
            masterSnapshot = _masters.Values.ToArray();
            _masters.Clear();
        }

        if (forwardSnapshot.Length > 0)
        {
            var tasks = forwardSnapshot
                .Select(e => CancelForwardAsync(e, perCancelTimeout, via: "drain"))
                .ToArray();
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout));
        }

        // Forwards-then-masters ordering: tearing the master down while
        // forwards are still attached produces stderr noise that can bleed
        // into the next run's diagnostics file. Only owned masters get
        // -O exit; the xUnit child has read-only adopted entries that the
        // parent will tear down on its own drain.
        var ownedMasters = masterSnapshot.Where(m => m.Owned).ToArray();
        if (ownedMasters.Length > 0)
        {
            var tasks = ownedMasters.Select(m => ExitMasterAsync(m, perCancelTimeout)).ToArray();
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout));
        }
    }

    /// <summary>
    /// Hosts whose dead control socket has already been reported via
    /// <c>tunnel_forwards_skipped</c>, so a teardown after a transport poison
    /// emits one summary event instead of one failure per forward.
    /// </summary>
    private readonly HashSet<string> _deadControlSocketReported = new();

    private async Task CancelForwardAsync(ForwardEntry entry, TimeSpan perCancelTimeout, string via)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var target = entry.RemoteSocketPath ?? $"127.0.0.1:{entry.MappedPort}";

        // A dead master removes its control socket; every -O cancel against it
        // exits 255 with "Control socket ... No such file or directory". Skip
        // the doomed exec and report once per host.
        if (!File.Exists(entry.ControlPath))
        {
            bool firstForHost;
            lock (_deadControlSocketReported)
            {
                firstForHost = _deadControlSocketReported.Add(entry.HostId);
            }
            if (firstForHost)
            {
                EmitSafe(
                    "tunnel_forwards_skipped",
                    new
                    {
                        host_id = entry.HostId,
                        reason = "control_socket_gone",
                        controlPath = entry.ControlPath,
                        via,
                    }
                );
            }
            return;
        }

        var psi = NewSshPsi();
        psi.ArgumentList.Add("-O");
        psi.ArgumentList.Add("cancel");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={entry.ControlPath}");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add($"127.0.0.1:{entry.CoordinatorPort}:{target}");
        psi.ArgumentList.Add(entry.SshDestination);

        // Best-effort, but record why a cancel failed instead of discarding it.
        int exit;
        string stderr;
        try
        {
            (exit, stderr) = await RunSshOpAsync(psi, perCancelTimeout, CancellationToken.None);
        }
        catch (Exception ex)
        {
            (exit, stderr) = (-1, ex.Message);
        }

        EmitSafe(
            "tunnel_forward_closed",
            new
            {
                host_id = entry.HostId,
                coordinator_port = entry.CoordinatorPort,
                via,
                exitCode = exit,
                // Happy path stays lean: no stderr field on a clean close.
                stderr = exit != 0 ? stderr : null,
                durationMs = ElapsedMs(startedAt),
            }
        );
    }

    private async Task ExitMasterAsync(HostMaster master, TimeSpan perCancelTimeout)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var psi = NewSshPsi();
        psi.ArgumentList.Add("-O");
        psi.ArgumentList.Add("exit");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={master.ControlPath}");
        psi.ArgumentList.Add(master.SshDestination);

        // Best-effort, but record why the exit failed instead of discarding it.
        int exit;
        string stderr;
        try
        {
            (exit, stderr) = await RunSshOpAsync(psi, perCancelTimeout, CancellationToken.None);
        }
        catch (Exception ex)
        {
            (exit, stderr) = (-1, ex.Message);
        }

        // ssh -O exit removes the socket file as part of clean shutdown.
        // If anything remains (timeout, partial shutdown), drop the file so
        // a future run on the same path doesn't hit the silent-disable trap.
        TryDeleteFile(master.ControlPath);

        EmitSafe(
            "ssh_master_exited",
            new
            {
                host_id = master.HostId,
                exitCode = exit,
                // Happy path stays lean: no stderr field on a clean exit.
                stderr = exit != 0 ? stderr : null,
                durationMs = ElapsedMs(startedAt),
            }
        );

        // Fold the master's own -E death line into the log. Read AFTER -O exit
        // (final line flushed); emit only when non-empty (a stable master logs
        // nothing, and an RST drop leaves it empty — that's the classifier's job).
        var tail = ReadMasterLogTail(master.LogPath, MaxLogTailBytes);
        if (tail.Length > 0)
        {
            long byteLength = 0;
            try
            {
                byteLength = new FileInfo(master.LogPath!).Length;
            }
            catch
            { /* size is advisory */
            }
            EmitSafe(
                "ssh_master_log",
                new
                {
                    host_id = master.HostId,
                    logPath = master.LogPath,
                    byteLength,
                    tail,
                }
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Opens a TcpListener on a random loopback port to learn an OS-assigned
    /// free port, immediately closes it, and returns the number. Pre-picking
    /// avoids depending on <c>ssh</c> echoing the assigned port. The TOCTOU
    /// gap (another consumer grabs the port between Stop and ssh's bind) is
    /// recovered by <see cref="OpenForwardCoreAsync"/>'s retry loop.
    /// </summary>
    private static int PickFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static long ElapsedMs(long startTicks) =>
        (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

    private static void EmitSafe(string name, object payload)
    {
        try
        {
            InfrastructureEventLog.Emit(name, payload);
        }
        catch
        { /* event log must never be load-bearing on tunnel teardown */
        }
    }

    private ProcessStartInfo NewSshPsi()
    {
        var psi = new ProcessStartInfo(_sshPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        return psi;
    }

    /// <summary>
    /// Runs an <c>ssh -O</c> reuse op (forward / cancel / check / exit) under the per-host
    /// mux-concurrency gate so a burst can't exhaust the shared master's accept backlog.
    /// The master <c>-M</c> spawn does NOT use this (it has no master to overload yet).
    /// </summary>
    private async Task<(int ExitCode, string Stderr)> RunSshOpAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        await _sshOpGate.WaitAsync(ct);
        try
        {
            return await RunSshToCompletionAsync(psi, timeout, ct);
        }
        finally
        {
            _sshOpGate.Release();
        }
    }

    private static async Task<(int ExitCode, string Stderr)> RunSshToCompletionAsync(
        ProcessStartInfo psi,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start ssh process: {psi.FileName} {string.Join(' ', psi.ArgumentList)}"
            );

        var stderr = new StringBuilder();

        // Read stderr and stdout in parallel via local async functions. ReadLineAsync
        // / ReadToEndAsync on Process.StandardError|Output are fully async on .NET 6+,
        // so wrapping them in Task.Run only adds thread-pool overhead. Drain stdout
        // so a chatty ssh build (e.g. -v left set) can't fill the OS pipe buffer
        // and deadlock the child.
        async Task ReadStderrAsync()
        {
            try
            {
                string? line;
                while (
                    (line = await process.StandardError.ReadLineAsync().ConfigureAwait(false))
                    != null
                )
                {
                    lock (stderr)
                    {
                        stderr.AppendLine(line);
                    }
                }
            }
            catch
            { /* diagnostic-only */
            }
        }

        async Task ReadStdoutAsync()
        {
            try
            {
                _ = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            }
            catch
            { /* diagnostic-only */
            }
        }

        var stderrTask = ReadStderrAsync();
        var stdoutTask = ReadStdoutAsync();

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            { /* best effort */
            }
            try
            {
                await process.WaitForExitAsync(
                    new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token
                );
            }
            catch
            { /* bounded */
            }
            try
            {
                await stderrTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch { }
            try
            {
                await stdoutTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch { }
            string captured;
            lock (stderr)
            {
                captured = stderr.ToString().TrimEnd();
            }

            return (
                124,
                captured
                    + (captured.Length > 0 ? "\n" : "")
                    + $"[timeout after {timeout.TotalMilliseconds:F0}ms]"
            );
        }

        try
        {
            await stderrTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch { }
        try
        {
            await stdoutTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch { }
        string captured2;
        lock (stderr)
        {
            captured2 = stderr.ToString().TrimEnd();
        }

        return (process.ExitCode, captured2);
    }

    private HostMaster ResolveMasterOrThrow(string hostId)
    {
        lock (_lock)
        {
            if (_masters.TryGetValue(hostId, out var m))
            {
                return m;
            }
        }

        // Child-process path: xUnit's AssemblyRunner spawns the test assembly
        // out-of-process, so each process has its own TunnelManager.Default
        // singleton. The parent registered masters in *its* singleton; this
        // process's singleton is empty until first miss triggers env-var
        // hydration. Same handoff pattern as SDVD_RUN_DIR / SDVD_HOST_TUNNELS:
        // parent writes the env, child lazy-reads on first need.
        HydrateFromEnvIfPresent();

        lock (_lock)
        {
            if (_masters.TryGetValue(hostId, out var m))
            {
                return m;
            }

            throw new InvalidOperationException(
                $"No SSH ControlMaster registered for host '{hostId}'. "
                    + $"HostPool.PreflightAsync must run RegisterHostMasterAsync before any forward open "
                    + $"(or {RunArtifactNames.SshHostMastersEnv} must be inherited from the parent)."
            );
        }
    }

    private void HydrateFromEnvIfPresent()
    {
        var sshPathEnv = Environment.GetEnvironmentVariable(RunArtifactNames.SshPathEnv);
        if (!string.IsNullOrWhiteSpace(sshPathEnv) && _sshPath == "ssh")
        {
            _sshPath = sshPathEnv;
        }

        var raw = Environment.GetEnvironmentVariable(RunArtifactNames.SshHostMastersEnv);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        Dictionary<string, HostMasterEnvEntry>? map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, HostMasterEnvEntry>>(
                raw,
                HostMasterEnvJson
            );
        }
        catch
        {
            return;
        }
        if (map is null)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var (hostId, entry) in map)
            {
                if (_masters.ContainsKey(hostId))
                {
                    continue;
                }

                if (
                    string.IsNullOrEmpty(entry.SshDestination)
                    || string.IsNullOrEmpty(entry.ControlPath)
                )
                {
                    continue;
                }

                _masters[hostId] = new HostMaster
                {
                    HostId = hostId,
                    SshDestination = entry.SshDestination,
                    SshKeyPath = entry.SshKeyPath,
                    ControlPath = entry.ControlPath,
                    Owned = false,
                };
            }
        }
    }

    /// <summary>
    /// Returns a JSON string suitable for <see cref="RunArtifactNames.SshHostMastersEnv"/>:
    /// <c>{hostId → {sshDestination, sshKeyPath, controlPath}}</c>. Called by
    /// <see cref="HostPool.PreflightAsync"/> in the parent after every remote
    /// host's master is registered, so the xUnit child can run
    /// <c>ssh -O forward</c> against the parent's existing sockets.
    /// </summary>
    public string SerializeRegisteredMasters()
    {
        Dictionary<string, HostMasterEnvEntry> snapshot;
        lock (_lock)
        {
            snapshot = _masters.ToDictionary(
                kv => kv.Key,
                kv => new HostMasterEnvEntry
                {
                    SshDestination = kv.Value.SshDestination,
                    SshKeyPath = kv.Value.SshKeyPath,
                    ControlPath = kv.Value.ControlPath,
                }
            );
        }
        return JsonSerializer.Serialize(snapshot, HostMasterEnvJson);
    }

    private static string ComputeControlPath(string hostId)
    {
        var runId =
            RunMetadata.RunId
            ?? throw new InvalidOperationException(
                "RunMetadata.RunId is null when computing ControlPath. "
                    + "RunMetadata.BeginRun must run before HostPool.PreflightAsync."
            );
        var pid = Process.GetCurrentProcess().Id;
        // pid is the load-bearing third term: it keeps two concurrent
        // coordinator processes (e.g. `make test` invoked twice on the same
        // box, or a CI matrix sharing temp dir) from colliding on
        // (hostId, runId). Don't simplify back to two terms.
        var input = $"{hostId}|{runId}|{pid}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var hex = Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 12);
        return Path.Combine(Path.GetTempPath(), $"sdvd-test-ssh-{hex}");
    }

    /// <summary>
    /// Sweeps temp dir for stale ControlMaster sockets from prior runs. Run at
    /// preflight start, before any master spawn, so the specific delete in
    /// <see cref="RegisterHostMasterAsync"/> can't hit a sibling-occupied path.
    /// </summary>
    public static int CleanupStaleControlSockets(TimeSpan maxAge)
    {
        var deleted = 0;
        var tempDir = Path.GetTempPath();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(tempDir, "sdvd-test-ssh-*");
        }
        catch
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.LastWriteTimeUtc > cutoff)
                {
                    continue;
                }

                info.Delete();
                deleted++;
            }
            catch (UnauthorizedAccessException)
            { /* shared /tmp on Linux: not ours */
            }
            catch (IOException)
            { /* in use by another live master, or transient */
            }
        }
        return deleted;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        { /* best effort */
        }
    }

    /// <summary>Max bytes of the master log tail attached to any event.</summary>
    private const int MaxLogTailBytes = 2048;

    /// <summary>
    /// Reads the last <paramref name="maxBytes"/> of the master's <c>-E</c> log.
    /// The death reason (e.g. "Timeout, server not responding.") is at the end,
    /// so we tail rather than head. Returns "" on any IO error or missing file
    /// — diagnostic-only, never load-bearing. Shared by the spawn/check failure
    /// paths (parent stderr empty under <c>-E</c>), the <c>ssh_master_log</c>
    /// teardown emit, and the <c>host_disconnected</c> transport enrichment.
    /// </summary>
    private static string ReadMasterLogTail(string? logPath, int maxBytes)
    {
        if (string.IsNullOrEmpty(logPath))
        {
            return "";
        }

        try
        {
            if (!File.Exists(logPath))
            {
                return "";
            }

            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            var length = stream.Length;
            if (length == 0)
            {
                return "";
            }

            var take = (int)Math.Min(length, maxBytes);
            stream.Seek(-take, SeekOrigin.End);
            var buffer = new byte[take];
            var read = stream.Read(buffer, 0, take);
            return Encoding.UTF8.GetString(buffer, 0, read).TrimEnd();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Run-scoped path of a host's master <c>-E</c> log. Deterministic from
    /// <paramref name="hostId"/> + the run's diagnostics dir, so any process
    /// (including the xUnit child that poisons the host) can locate the file the
    /// parent's master wrote on the shared filesystem.
    /// </summary>
    private static string ComputeMasterLogPath(string hostId) =>
        Path.Combine(TestArtifacts.GetDiagnosticsDir(), $"ssh-master-{hostId}.log");

    /// <summary>
    /// Reads the tail of a host's master log by host id (recomputing the path),
    /// for <see cref="DockerHost.Poison"/>'s transport-class
    /// <c>sshMasterLogTail</c> enrichment. Returns "" when the log is missing or
    /// empty — e.g. an RST drop, where the reset line never reaches <c>-E</c>.
    /// </summary>
    public static string ReadMasterLogTailForHost(string hostId) =>
        ReadMasterLogTail(ComputeMasterLogPath(hostId), MaxLogTailBytes);

    private readonly record struct ForwardKey(string HostId, int CoordinatorPort);

    private sealed class ForwardEntry
    {
        public required string HostId { get; init; }
        public required string SshDestination { get; init; }
        public required string? SshKeyPath { get; init; }
        public required int CoordinatorPort { get; init; }
        public required int MappedPort { get; init; }
        public required string? RemoteSocketPath { get; init; }
        public required string ControlPath { get; init; }
    }

    private sealed class HostMaster
    {
        public required string HostId { get; init; }
        public required string SshDestination { get; init; }
        public required string? SshKeyPath { get; init; }
        public required string ControlPath { get; init; }

        /// <summary>
        /// Path to the master's <c>-E</c> error log. Set only on owned masters
        /// (the parent that spawned them); null on adopted masters in the xUnit
        /// child, which never spawn or tear down the log. Only owned masters
        /// reach <see cref="ExitMasterAsync"/>, so the <c>ssh_master_log</c>
        /// emit there always has a path.
        /// </summary>
        public string? LogPath { get; init; }

        /// <summary>
        /// True in the parent process where <see cref="RegisterHostMasterAsync"/>
        /// spawned this master; false in the xUnit child where the master was
        /// adopted from the parent's <c>SDVD_SSH_HOST_MASTERS</c> env var.
        /// Drain teardown only sends <c>ssh -O exit</c> for owned masters —
        /// the child does not own the parent's sockets.
        /// </summary>
        public required bool Owned { get; init; }
    }

    private sealed class HostMasterEnvEntry
    {
        [JsonPropertyName("sshDestination")]
        public string SshDestination { get; set; } = "";

        [JsonPropertyName("sshKeyPath")]
        public string? SshKeyPath { get; set; }

        [JsonPropertyName("controlPath")]
        public string ControlPath { get; set; } = "";
    }

    private static readonly JsonSerializerOptions HostMasterEnvJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class PortCollisionException : Exception
    {
        public PortCollisionException(string message)
            : base(message) { }
    }

    private sealed class ProbeTimeoutException : Exception
    {
        public ProbeTimeoutException(string message)
            : base(message) { }
    }
}

/// <summary>
/// A held forward from a coordinator-side port to a daemon-side mapped port.
/// Disposing closes the forward (or is a no-op for local hosts).
/// </summary>
public sealed class ForwardLease : IAsyncDisposable
{
    private readonly TunnelManager _owner;
    private readonly string _hostId;
    private readonly int _mappedPort;
    private readonly bool _isRemote;
    private bool _disposed;

    /// <summary>The coordinator-side port a caller can connect to.</summary>
    public int CoordinatorPort { get; }

    public ForwardLease(
        TunnelManager owner,
        string hostId,
        int coordinatorPort,
        int mappedPort,
        bool isRemote
    )
    {
        _owner = owner;
        _hostId = hostId;
        CoordinatorPort = coordinatorPort;
        _mappedPort = mappedPort;
        _isRemote = isRemote;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_isRemote)
        {
            await _owner.CloseAsync(_hostId, CoordinatorPort, _mappedPort);
        }
    }
}
