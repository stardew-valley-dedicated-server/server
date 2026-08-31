using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.Tests.Schema.Json;

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
    private readonly Dictionary<string, int> _canaryWedgeStreaks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _reopenFailStreaks = new(StringComparer.Ordinal);

    // Canary observations of the current wedge streak per host, oldest first,
    // so the wedge record can show every poll that led to the verdict.
    private readonly Dictionary<string, List<CanaryObservation>> _canaryWedgeHistory = new(
        StringComparer.Ordinal
    );
    private string _sshPath = "ssh";

    // Faults seen this long after a master respawn completes are attributed to
    // it (transport-state.{hostId}.json windowEndUtc). Matches ForwardHealingHandler's
    // heal budget, the longest a consumer keeps retrying against the new master.
    private static readonly TimeSpan RespawnAttributionWindow = TimeSpan.FromSeconds(45);

    // Set at DrainAsync entry. Teardown owns master lifecycle from that point: the
    // owner-side health monitor must not respawn a master (or re-open forwards) that
    // drain is concurrently exiting — that leaves an orphan master squatting the
    // ControlPath after the run.
    private volatile bool _draining;

    // Caps concurrent `ssh -O` reuse invocations (forward / cancel / check) that hit one
    // shared ControlMaster's mux listener. An unbounded burst of these can exhaust the
    // master's accept backlog / fd budget — the master then logs `accept: Resource
    // temporarily unavailable` and stops answering, forcing the owner-side monitor to
    // respawn it. The spawn itself is exempt (it has no master yet).
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

        var reportedPid = ParseMasterPid(checkStderr);
        if (reportedPid is null)
        {
            // Without a pid the respawn path's hard reset silently degrades to
            // "skipped_unknown_pid" — surface the parse miss at registration (an `ssh -O
            // check` output-format change would otherwise disable the kill path with no
            // signal until a wedge needs it).
            EmitSafe("ssh_master_pid_unparsed", new { host_id = hostId, stderr = checkStderr });
        }

        // -O check reports ssh's OWN pid space. Under Git for Windows' Cygwin
        // ssh the -f-forked master's Cygwin pid is NOT the Windows pid, so map
        // it via the sibling `ps` NOW, while the master is provably alive —
        // teardown paths may run after it died, when no mapping exists.
        var masterPid = reportedPid is int cygwinPid
            ? await TryMapToWindowsPidAsync(_sshPath, cygwinPid)
            : null;
        if (reportedPid is not null && masterPid is null)
        {
            // Kill path degrades to -O exit + check-based confirmation only.
            EmitSafe("ssh_master_pid_unmapped", new { host_id = hostId, reportedPid });
        }

        var spawnedAtUtc = DateTime.UtcNow;
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
                MasterPid = masterPid,
                SpawnedAtUtc = spawnedAtUtc,
            };
        }

        // Journal the owned master so teardown paths that never see this
        // process's memory (emergency cleanup after Environment.Exit, the next
        // run's preflight reaper) can still reach it. Upsert by host id — a
        // respawn re-registers and updates the journal for free.
        SshMasterJournal.RecordMaster(
            new SshMasterJournal.MasterRecord
            {
                HostId = hostId,
                SshDestination = sshDestination,
                ControlPath = controlPath,
                LogPath = logPath,
                MasterPid = masterPid,
                SpawnedAtUtc = spawnedAtUtc,
                SshPath = _sshPath,
            }
        );

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
        // A healthy -N master stays at 0 bytes, so the happy path stays lean
        // (VERBOSE would only add the "Authenticated to" line — mux channel
        // open/close is debug2+). The file is per master process: a respawn
        // archives the old one (ArchiveMasterLog), so its tail is the old
        // master's last minutes, not the new master's first.
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
    /// Parses the master's pid from <c>ssh -O check</c>'s "Master running (pid=N)" stderr
    /// line. Null when absent — the kill path then skips (degrades to the old behavior of
    /// leaving the process behind).
    /// </summary>
    private static int? ParseMasterPid(string checkStderr)
    {
        const string marker = "pid=";
        var start = checkStderr.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = start;
        while (end < checkStderr.Length && char.IsAsciiDigit(checkStderr[end]))
        {
            end++;
        }

        return int.TryParse(checkStderr.AsSpan(start, end - start), out var pid) ? pid : null;
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
    /// Owner-side master health check + heal. Two probes, because the master has two
    /// independent failure surfaces:
    /// <list type="bullet">
    ///   <item><b>Mux path</b> — <c>ssh -O check</c>. Fails when the mux listener wedged
    ///     or the process died.</item>
    ///   <item><b>Data path</b> — a canary request through the host's socket forward
    ///     (the Docker daemon's <c>/_ping</c>). Catches the wedge mode where the master
    ///     still answers <c>-O check</c> but black-holes NEW forwarded connections
    ///     (accepted into the kernel backlog, never serviced) while established channels
    ///     keep flowing — invisible to every other probe, and it hangs consumers instead
    ///     of failing them fast.</item>
    /// </list>
    /// A wedged data path (two consecutive polls, debounced against transfer-load slowness)
    /// or a failed mux check respawns the master at its existing ControlPath; a canary
    /// <i>connection-refused</i> means the forward listener dropped while the master is fine
    /// (keepalive blip), so only the owner-registered forwards are re-opened in place
    /// (skipping ones whose listener still accepts). If that in-place reopen fails on two
    /// consecutive polls, it escalates to a respawn too (cause <c>forward_reopen_failing</c>)
    /// — otherwise a permanently unrecoverable forward (e.g. an orphan squatting the pinned
    /// port) would loop reopen-failure events under a monitor that keeps reporting healthy.
    /// Returns true if the master is healthy (already, or after respawn). No-op true for a
    /// host this process doesn't own — the child CANNOT respawn (TryRespawnMasterAsync
    /// refuses on !Owned), which is why this must run in the parent.
    /// </summary>
    public async Task<bool> EnsureOwnedMasterHealthyAsync(
        string hostId,
        CancellationToken ct = default
    )
    {
        if (_draining)
        {
            return true; // teardown owns master lifecycle now
        }

        var master = TryGetMaster(hostId);
        if (master is null || !master.Owned)
        {
            return true; // not ours to heal
        }

        string cause;
        if (await IsMasterAliveAsync(hostId, ct))
        {
            var canary = await ProbeSocketForwardDataPathAsync(hostId, ct);
            if (canary.Result == SocketForwardProbeResult.Dropped)
            {
                // A drop ends any wedge streak but is not a recovery — the forward is gone
                // and the reopen below handles it, emitting its own events. Clear the streak
                // without a canary_recovered that would carry a dropped observation.
                EndWedgeStreak(hostId, canary.Observation, emitRecovered: false);
                if (await ReopenRegisteredForwardsAsync(hostId, skipAliveListeners: true, ct))
                {
                    ResetStreak(_reopenFailStreaks, hostId);
                    return true;
                }
                if (IncrementStreak(_reopenFailStreaks, hostId) < 2)
                {
                    return true; // single miss: the in-place reopen may just be racing the drop
                }
                // In-place reopen keeps failing (e.g. an orphan process squats the pinned
                // port) — without this escalation the monitor would report healthy forever
                // while every poll emits tunnel_forward_reopen_failed.
                cause = "forward_reopen_failing";
            }
            else if (canary.Result == SocketForwardProbeResult.NotApplicable)
            {
                return true;
            }
            else if (canary.Result == SocketForwardProbeResult.Healthy)
            {
                EndWedgeStreak(hostId, canary.Observation);
                ResetStreak(_reopenFailStreaks, hostId);
                return true;
            }
            else
            {
                var streak = RecordWedgedCanary(hostId, canary.Observation);
                if (streak < 2)
                {
                    return true; // single wedged poll: could be transfer-load slowness
                }
                // Everything observable about the wedge, captured BEFORE the respawn
                // destroys the evidence (the old master, its connections, its log).
                await EmitWedgeObservedAsync(master, streak, ct);
                cause = "datapath_wedged";
            }
        }
        else
        {
            cause = "mux_check_failed";
        }

        ResetStreak(_canaryWedgeStreaks, hostId);
        ResetStreak(_reopenFailStreaks, hostId);
        lock (_lock)
        {
            _canaryWedgeHistory.Remove(hostId);
        }

        // Respawn at the same ControlPath so the child's adopted entry (and its in-flight
        // forward re-opens) recover transparently.
        EmitSafe("ssh_master_unhealthy_owner", new { host_id = hostId, cause });
        return await TryRespawnMasterAsync(hostId, ct, cause);
    }

    /// <summary>
    /// Appends a wedged canary to the host's streak and returns the streak length.
    /// The first wedged poll emits <c>ssh_master_canary_stall</c> (stall start).
    /// </summary>
    private int RecordWedgedCanary(string hostId, CanaryObservation canary)
    {
        var streak = IncrementStreak(_canaryWedgeStreaks, hostId);

        lock (_lock)
        {
            if (!_canaryWedgeHistory.TryGetValue(hostId, out var history))
            {
                history = new List<CanaryObservation>();
                _canaryWedgeHistory[hostId] = history;
            }

            history.Add(canary);
        }

        if (streak == 1)
        {
            EmitSafe(
                TransportEventNames.SshMasterCanaryStall,
                new SshMasterCanaryStallEvent(hostId, canary)
            );
        }

        return streak;
    }

    /// <summary>
    /// A healthy poll after wedged ones ends the streak: emits
    /// <c>ssh_master_canary_recovered</c> with the stall's measured length (first
    /// wedged poll → this poll) — the number the stall-tolerance threshold is tuned
    /// against — and clears the streak. No-op when no streak was running.
    /// </summary>
    private void EndWedgeStreak(string hostId, CanaryObservation canary, bool emitRecovered = true)
    {
        List<CanaryObservation>? history;

        lock (_lock)
        {
            _canaryWedgeStreaks.Remove(hostId);
            _canaryWedgeHistory.Remove(hostId, out history);
        }

        if (!emitRecovered || history is null || history.Count == 0)
        {
            return;
        }

        EmitSafe(
            TransportEventNames.SshMasterCanaryRecovered,
            new SshMasterCanaryRecoveredEvent(
                hostId,
                (long)(canary.AtUtc - history[0].AtUtc).TotalMilliseconds,
                history.Count,
                canary
            )
        );
    }

    /// <summary>
    /// The detection record for a wedge verdict: every canary of the streak, a
    /// fresh <c>-O check</c>, the coordinator's TCP state toward the host, a plain
    /// TCP reachability probe, and the master's <c>-E</c> log tail.
    /// </summary>
    private async Task EmitWedgeObservedAsync(HostMaster master, int streak, CancellationToken ct)
    {
        IReadOnlyList<CanaryObservation> canaries;
        lock (_lock)
        {
            canaries = _canaryWedgeHistory.TryGetValue(master.HostId, out var history)
                ? history.ToArray()
                : Array.Empty<CanaryObservation>();
        }

        var checkStarted = Stopwatch.GetTimestamp();
        MuxCheckObservation muxCheck;
        try
        {
            var (exit, stderr) = await RunCheckAsync(master.ControlPath, master.SshDestination, ct);
            muxCheck = new MuxCheckObservation(exit, stderr, ElapsedMs(checkStarted));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            muxCheck = new MuxCheckObservation(
                -1,
                $"{ex.GetType().Name}: {ex.Message}",
                ElapsedMs(checkStarted)
            );
        }

        var (tcp, reachability) = await CoordinatorTcpSnapshot.CaptureAsync(
            master.SshDestination,
            _sshPath,
            ct
        );

        EmitSafe(
            TransportEventNames.SshMasterWedgeObserved,
            new SshMasterWedgeObservedEvent(
                master.HostId,
                streak,
                canaries,
                muxCheck,
                tcp,
                reachability,
                ReadMasterLogTail(master.LogPath, MaxLogTailBytes)
            )
        );
    }

    private int IncrementStreak(Dictionary<string, int> streaks, string hostId)
    {
        lock (_lock)
        {
            var next = streaks.TryGetValue(hostId, out var n) ? n + 1 : 1;
            streaks[hostId] = next;
            return next;
        }
    }

    private void ResetStreak(Dictionary<string, int> streaks, string hostId)
    {
        lock (_lock)
        {
            streaks.Remove(hostId);
        }
    }

    private enum SocketForwardProbeResult
    {
        /// <summary>No socket forward registered for the host (or probe glitch) — no signal.</summary>
        NotApplicable,

        /// <summary>The daemon answered through the forward: data path works.</summary>
        Healthy,

        /// <summary>Connected but no byte within the deadline: accepted-and-never-serviced.</summary>
        Wedged,

        /// <summary>Connect refused / stream reset: the forward listener is gone.</summary>
        Dropped,
    }

    /// <summary>
    /// Canary for the master's data path: one HTTP <c>GET /_ping</c> to the Docker daemon
    /// through the host's registered socket forward. Pure TCP — no <c>ssh -O</c> op, so it
    /// doesn't consume the mux gate and works even when the mux listener is dead.
    /// </summary>
    private async Task<CanaryProbe> ProbeSocketForwardDataPathAsync(
        string hostId,
        CancellationToken ct
    )
    {
        ForwardEntry? entry;
        lock (_lock)
        {
            entry = _forwards.Values.FirstOrDefault(f =>
                f.HostId == hostId && f.RemoteSocketPath is not null
            );
        }

        var atUtc = DateTime.UtcNow;

        if (entry is null)
        {
            return CanaryProbe.Of(
                SocketForwardProbeResult.NotApplicable,
                atUtc,
                0,
                null,
                null,
                "no socket forward registered"
            );
        }

        var connectTimeout = TimeSpan.FromSeconds(2);
        var ioTimeout = TimeSpan.FromSeconds(5);
        var started = Stopwatch.GetTimestamp();
        long connectMs;
        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(connectTimeout);
            try
            {
                await client.ConnectAsync(
                    IPAddress.Loopback,
                    entry.CoordinatorPort,
                    connectCts.Token
                );
                connectMs = ElapsedMs(started);
            }
            catch (SocketException ex)
            {
                return CanaryProbe.Of(
                    SocketForwardProbeResult.Dropped,
                    atUtc,
                    ElapsedMs(started),
                    null,
                    null,
                    $"connect: {ex.SocketErrorCode}"
                );
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Loopback connect only stalls when the listener's backlog is full of
                // never-accepted connections — the wedge signature, not a slow daemon.
                return CanaryProbe.Of(
                    SocketForwardProbeResult.Wedged,
                    atUtc,
                    ElapsedMs(started),
                    null,
                    null,
                    $"connect deadline {connectTimeout.TotalSeconds:F0}s"
                );
            }

            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                "GET /_ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"
            );
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(ioTimeout);
            long? writeMs = null;
            try
            {
                var writeStarted = Stopwatch.GetTimestamp();
                await stream.WriteAsync(request, ioCts.Token);
                writeMs = ElapsedMs(writeStarted);
                var readStarted = Stopwatch.GetTimestamp();
                var buffer = new byte[1];
                var read = await stream.ReadAsync(buffer, ioCts.Token);
                var readMs = ElapsedMs(readStarted);
                return read > 0
                    ? CanaryProbe.Of(
                        SocketForwardProbeResult.Healthy,
                        atUtc,
                        connectMs,
                        writeMs,
                        readMs,
                        null
                    )
                    : CanaryProbe.Of(
                        SocketForwardProbeResult.Dropped,
                        atUtc,
                        connectMs,
                        writeMs,
                        readMs,
                        "read: EOF"
                    );
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return CanaryProbe.Of(
                    SocketForwardProbeResult.Wedged,
                    atUtc,
                    connectMs,
                    writeMs,
                    null,
                    $"{(writeMs is null ? "write" : "read")} deadline {ioTimeout.TotalSeconds:F0}s"
                );
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                return CanaryProbe.Of(
                    SocketForwardProbeResult.Dropped,
                    atUtc,
                    connectMs,
                    writeMs,
                    null,
                    $"{(writeMs is null ? "write" : "read")}: {ex.GetType().Name}: {ex.Message}"
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw; // outer ct: caller is shutting down
        }
        catch (Exception ex)
        {
            // A probe glitch must not condemn a host, but its shape is recorded.
            return CanaryProbe.Of(
                SocketForwardProbeResult.NotApplicable,
                atUtc,
                ElapsedMs(started),
                null,
                null,
                $"probe glitch: {ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    /// <summary>Canary verdict plus the observation that produced it.</summary>
    private readonly record struct CanaryProbe(
        SocketForwardProbeResult Result,
        CanaryObservation Observation
    )
    {
        public static CanaryProbe Of(
            SocketForwardProbeResult result,
            DateTime atUtc,
            long connectMs,
            long? writeMs,
            long? readMs,
            string? detail
        ) =>
            new(
                result,
                new CanaryObservation(
                    atUtc,
                    result switch
                    {
                        SocketForwardProbeResult.Healthy => "healthy",
                        SocketForwardProbeResult.Wedged => "wedged",
                        SocketForwardProbeResult.Dropped => "dropped",
                        _ => "not_applicable",
                    },
                    connectMs,
                    writeMs,
                    readMs,
                    detail
                )
            );
    }

    /// <summary>
    /// Attempts to resurrect a dead/unresponsive ControlMaster once: kills the old master
    /// process (a wedged master survives <c>-O exit</c> — its mux is the broken part — and
    /// left alive it squats every forward port, turning would-be fast
    /// <c>ConnectionRefused</c> faults into unclassifiable hangs), evicts the stale entry +
    /// socket, re-runs <see cref="RegisterHostMasterAsync"/> with the original
    /// destination/key, then re-opens this process's registered forwards at their original
    /// coordinator ports. Returns true if a usable master is back. Used by the poison seam
    /// and the owner-side monitor — one shared master carries every forward, so a transient
    /// master death (e.g. <c>accept: Resource temporarily unavailable</c> from fd/backlog
    /// exhaustion) otherwise loses the whole host even though the host is fine.
    /// Only the owner (parent) can respawn — an adopted child entry has no spawn rights, so
    /// it returns false and lets the normal poison proceed.
    /// <paramref name="cause"/> names what tripped the respawn; it is recorded in the
    /// <c>ssh_master_respawn_attempt</c> event and <c>transport-state.{hostId}.json</c>.
    /// </summary>
    public async Task<bool> TryRespawnMasterAsync(
        string hostId,
        CancellationToken ct = default,
        string cause = "master_check_failing"
    )
    {
        if (_draining || Helpers.ShutdownCoordinator.IsShuttingDown)
        {
            // Teardown owns master lifecycle now. The shutdown check covers the
            // abort paths (Ctrl+C, UI Stop), which never set _draining: without
            // it the health monitor races the emergency teardown, respawning a
            // fresh master for every one the teardown kills — the last respawn
            // can outlive the process as an unreaped orphan.
            return false;
        }

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
        var actionStartedAtUtc = DateTime.UtcNow;
        var actionStarted = Stopwatch.GetTimestamp();
        var incidentId = $"{hostId}-{actionStartedAtUtc:yyyyMMddTHHmmssfff}";

        // Terminal teardown of the old master: bounded -O exit, pid-kill
        // fallback, then CONFIRM the process is gone. The old process must be
        // GONE before the same-port forward re-open below, and every connection
        // it still holds must die fast rather than hang. Established streams
        // through it (container log/stats) are severed too — their readers
        // reconnect through the restored forwards or surface a classifiable
        // fault.
        var teardown = await TerminateMasterCoreAsync(
            _sshPath,
            destination,
            master.ControlPath,
            master.MasterPid,
            master.SpawnedAtUtc,
            TimeSpan.FromSeconds(3)
        );
        var terminatedAtUtc = DateTime.UtcNow;

        // Evict the dead entry so RegisterHostMasterAsync's ContainsKey guard
        // doesn't short-circuit. (The core already unlinked the socket iff the
        // process is confirmed gone, so `ssh -M` can't hit the "socket exists"
        // trap on the success path.)
        lock (_lock)
        {
            _masters.Remove(hostId);
        }

        // The old master's -E log is its evidence; the new master must not
        // append into it. Archived only once the process is gone (a survivor
        // keeps writing to it).
        var archivePath = teardown.Gone
            ? ArchiveMasterLog(master.LogPath, master.MasterPid, master.SpawnedAtUtc)
            : null;

        var termination = teardown switch
        {
            { KillOutcome: "killed" } => "killed",
            { Gone: false } => "unconfirmed",
            { ExitCode: 0 } => "exit_ok",
            _ => "socket_gone",
        };

        var state = new TransportState
        {
            IncidentId = incidentId,
            HostId = hostId,
            Cause = cause,
            Action = "master_respawn",
            Termination = termination,
            ExitCode = teardown.ExitCode,
            ExitStderr = teardown.ExitStderr,
            KillOutcome = teardown.KillOutcome,
            OldPid = master.MasterPid,
            MasterLogArchivePath = archivePath,
            ActionStartedAtUtc = actionStartedAtUtc,
            TerminatedAtUtc = terminatedAtUtc,
            Outcome = "in_progress",
        };
        PublishTransportState(state);

        EmitSafe(
            TransportEventNames.SshMasterRespawnAttempt,
            new SshMasterRespawnAttemptEvent(
                hostId,
                incidentId,
                cause,
                master.MasterPid,
                termination,
                teardown.ExitCode,
                teardown.ExitStderr,
                teardown.KillOutcome,
                ElapsedMs(actionStarted),
                terminatedAtUtc,
                archivePath
            )
        );

        if (!teardown.Gone)
        {
            // The old process survived even the kill — the wedge state (alive,
            // holding the forward listeners, refusing service).
            // Respawning at the same ControlPath would trip the
            // "ControlSocket already exists, disabling multiplexing" trap, and
            // every same-port reopen would collide with the survivor's
            // listeners. Keep the socket (it is the only remaining handle) and
            // report unrecoverable so the caller poisons the host.
            EmitSafe(
                "ssh_master_respawn_failed",
                new
                {
                    host_id = hostId,
                    incidentId,
                    error = "old master process not confirmed gone",
                    killOutcome = teardown.KillOutcome,
                }
            );
            PublishTransportState(CompleteTransportState(state, "old_master_survived"));
            return false;
        }

        try
        {
            await RegisterHostMasterAsync(hostId, destination, keyPath, ct);
            var alive = await IsMasterAliveAsync(hostId, ct);
            EmitSafe(
                "ssh_master_respawned",
                new
                {
                    host_id = hostId,
                    incidentId,
                    alive,
                }
            );
            if (alive)
            {
                // Same-port restoration for forwards THIS process registered (the parent's
                // daemon-socket forward — its port is pinned in Docker.DotNet endpoints and
                // published to the child via env, so a new port would strand every client).
                // Forwards other processes registered (the child's per-server API forwards)
                // heal lazily through their own seams: with the old master killed, their
                // stale ports now fail fast as forward-scoped faults.
                await ReopenRegisteredForwardsAsync(hostId, skipAliveListeners: false, ct);
            }

            PublishTransportState(
                CompleteTransportState(state, alive ? "respawned" : "respawn_failed")
            );
            return alive;
        }
        catch (Exception ex)
        {
            EmitSafe(
                "ssh_master_respawn_failed",
                new
                {
                    host_id = hostId,
                    incidentId,
                    error = ex.Message,
                }
            );
            PublishTransportState(CompleteTransportState(state, "respawn_failed"));
            return false;
        }
    }

    private static TransportState CompleteTransportState(TransportState state, string outcome)
    {
        var endedAtUtc = DateTime.UtcNow;
        return state with
        {
            Outcome = outcome,
            ActionEndedAtUtc = endedAtUtc,
            WindowEndUtc = endedAtUtc + RespawnAttributionWindow,
        };
    }

    /// <summary>
    /// Writes the host's <c>diagnostics/transport-state.{hostId}.json</c> for the xUnit child. A write
    /// failure is reported as an event rather than thrown: the respawn itself must
    /// still complete, and the child's reader treats a missing file as "no action".
    /// </summary>
    private static void PublishTransportState(TransportState state)
    {
        try
        {
            TransportStateFile.Write(TestArtifacts.RunDir, state);
        }
        catch (Exception ex)
        {
            EmitSafe(
                "transport_state_write_failed",
                new
                {
                    host_id = state.HostId,
                    incidentId = state.IncidentId,
                    path = TransportStateFile.PathFor(TestArtifacts.RunDir, state.HostId),
                    error = $"{ex.GetType().Name}: {ex.Message}",
                }
            );
        }
    }

    /// <summary>
    /// Moves the dead master's <c>-E</c> log to <c>ssh-master-{host}.pid{pid}-{spawn}.log</c>
    /// so the replacement master starts a fresh file at the deterministic path.
    /// Returns the archive path, or null when there was no log to archive; a
    /// failed move is reported as an event and leaves the file in place.
    /// </summary>
    private static string? ArchiveMasterLog(string? logPath, int? masterPid, DateTime spawnedAtUtc)
    {
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return null;
        }

        var archivePath = Path.Combine(
            Path.GetDirectoryName(logPath)!,
            $"{Path.GetFileNameWithoutExtension(logPath)}"
                + $".pid{(masterPid is int pid ? pid.ToString() : "unknown")}"
                + $"-{spawnedAtUtc:HHmmss}{Path.GetExtension(logPath)}"
        );
        try
        {
            File.Move(logPath, archivePath, overwrite: true);
            return archivePath;
        }
        catch (Exception ex)
        {
            EmitSafe(
                "ssh_master_log_archive_failed",
                new
                {
                    logPath,
                    archivePath,
                    error = $"{ex.GetType().Name}: {ex.Message}",
                }
            );
            return null;
        }
    }

    /// <summary>Outcome of <see cref="TerminateMasterCoreAsync"/>. <c>Gone</c>
    /// means the master <em>process</em> is confirmed dead (or its pid was
    /// recycled to something else) — only then was the socket unlinked.</summary>
    private readonly record struct MasterTeardownOutcome(
        bool Gone,
        int ExitCode,
        string ExitStderr,
        string KillOutcome
    );

    /// <summary>
    /// Terminal ControlMaster teardown — the one primitive every teardown path
    /// uses (drain, respawn, emergency cleanup, cross-run reaper):
    /// <list type="number">
    ///   <item>Bounded <c>ssh -O exit</c>. Exit 0 only <em>acknowledges</em> the
    ///     request — the master's shutdown is asynchronous.</item>
    ///   <item>Confirm the master process is gone (bounded wait); if it isn't
    ///     (or <c>-O exit</c> failed/timed out), kill it by pid and re-confirm.</item>
    ///   <item>Unlink the socket <b>only after</b> the process is confirmed
    ///     gone. A survivor keeps its socket — it is the only remaining handle
    ///     (the cross-run reaper needs it), and unlinking it would convert a
    ///     reapable orphan into a PID-only one.</item>
    /// </list>
    /// Static and fully parameterized so foreign-process paths (emergency
    /// teardown after <c>Environment.Exit</c>, the next run's preflight reaper)
    /// can drive it from journal records. Cancellation propagates (leaving the
    /// half-torn master's socket and journal entry intact for the next
    /// attempt); every existing caller passing <see cref="CancellationToken.None"/>
    /// is unaffected.
    /// </summary>
    private static async Task<MasterTeardownOutcome> TerminateMasterCoreAsync(
        string sshPath,
        string sshDestination,
        string controlPath,
        int? masterPid,
        DateTime spawnedAtUtc,
        TimeSpan exitTimeout,
        CancellationToken ct = default
    )
    {
        int exit;
        string stderr;
        try
        {
            var psi = NewSshPsiFor(sshPath);
            psi.ArgumentList.Add("-O");
            psi.ArgumentList.Add("exit");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add($"ControlPath={controlPath}");
            psi.ArgumentList.Add(sshDestination);
            (exit, stderr) = await RunSshToCompletionAsync(psi, exitTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            (exit, stderr) = (-1, ex.Message);
        }

        var gone = await WaitForMasterGoneAsync(
            sshPath,
            sshDestination,
            controlPath,
            masterPid,
            spawnedAtUtc,
            TimeSpan.FromSeconds(2),
            ct
        );

        var killOutcome = "not_needed";
        if (!gone)
        {
            killOutcome = TryKillMasterProcess(masterPid, spawnedAtUtc, sshPath);
            // "killed" is itself an observed termination — the identity-matched
            // process was alive, Kill() succeeded, and WaitForExit saw it end —
            // so no further corroboration is needed (and the socket-side check
            // can false-negative right after a hard kill: a dead Cygwin socket
            // takes ~2s to refuse).
            gone =
                killOutcome == "killed"
                || await WaitForMasterGoneAsync(
                    sshPath,
                    sshDestination,
                    controlPath,
                    masterPid,
                    spawnedAtUtc,
                    TimeSpan.FromSeconds(2),
                    ct
                );
        }

        if (gone)
        {
            TryDeleteFile(controlPath);
        }

        return new MasterTeardownOutcome(gone, exit, stderr, killOutcome);
    }

    /// <summary>
    /// Bounded wait for the master process to disappear. With a known Windows
    /// pid, first waits out a live identity-matched process; the verdict then
    /// ALWAYS comes from <c>ssh -O check</c> against the socket — pid absence
    /// alone proves nothing (an unmapped/recycled pid reads as absent while the
    /// master lives, since <c>-O check</c> reports the Cygwin-space pid, not
    /// the Windows pid). A check that times out is "cannot confirm", NOT gone:
    /// a wedged master answers nothing while staying very much alive.
    /// </summary>
    private static async Task<bool> WaitForMasterGoneAsync(
        string sshPath,
        string sshDestination,
        string controlPath,
        int? masterPid,
        DateTime spawnedAtUtc,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        if (masterPid is int pid)
        {
            var seenAlive = false;
            var startedAt = Stopwatch.GetTimestamp();
            while (true)
            {
                var (process, _) = ProbeMasterProcess(pid, spawnedAtUtc, sshPath);
                if (process is null)
                {
                    if (seenAlive)
                    {
                        return true; // watched the identity-matched process die
                    }

                    break; // ambient absence — only the socket can confirm
                }

                seenAlive = true;
                process.Dispose();
                if (Stopwatch.GetElapsedTime(startedAt) >= timeout)
                {
                    return false; // identity-matched process still alive
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }

        try
        {
            var psi = NewSshPsiFor(sshPath);
            psi.ArgumentList.Add("-O");
            psi.ArgumentList.Add("check");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add($"ControlPath={controlPath}");
            psi.ArgumentList.Add(sshDestination);
            // Floor the check budget above Cygwin's dead-socket refusal latency
            // (measured ~2.1s), or a genuinely-gone master reads as 124/cannot-
            // confirm when the caller's wait budget is tight.
            var checkTimeout =
                timeout > TimeSpan.FromSeconds(4) ? timeout : TimeSpan.FromSeconds(4);
            var (exit, stderr) = await RunSshToCompletionAsync(psi, checkTimeout, ct);
            if (exit == 0 && stderr.Contains("Master running", StringComparison.Ordinal))
            {
                return false; // alive
            }

            return exit != 124; // 124 = check timed out: cannot confirm gone
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // cannot confirm
        }
    }

    /// <summary>
    /// Maps the pid <c>ssh -O check</c> reported to a Windows pid. On POSIX
    /// they are the same value. On Windows the Cygwin master's mapping lives in
    /// the WINPID column of the <c>ps</c> shipped next to ssh.exe (same Cygwin
    /// namespace as the master, since both run the same msys runtime). Null
    /// when ps or the row is missing — the caller then has no kill path, only
    /// <c>-O exit</c> plus check-based confirmation.
    /// </summary>
    private static async Task<int?> TryMapToWindowsPidAsync(string sshPath, int reportedPid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return reportedPid;
        }

        try
        {
            var psPath = Path.Combine(Path.GetDirectoryName(sshPath) ?? "", "ps.exe");
            if (!File.Exists(psPath))
            {
                return null;
            }

            var psi = new ProcessStartInfo(psPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var stdout = await RunToolForStdoutAsync(psi, TimeSpan.FromSeconds(5));

            // Header: "PID PPID PGID WINPID TTY UID STIME COMMAND". Rows can
            // carry a leading one-char status flag (e.g. S for a stopped
            // process) that shifts every column right by one.
            int pidCol = -1,
                winPidCol = -1;
            foreach (var line in stdout.Split('\n'))
            {
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pidCol < 0)
                {
                    pidCol = Array.IndexOf(tokens, "PID");
                    winPidCol = Array.IndexOf(tokens, "WINPID");
                    if (pidCol < 0 || winPidCol < 0)
                    {
                        pidCol = -1; // not the header line yet
                    }
                    continue;
                }

                if (tokens.Length == 0)
                {
                    continue;
                }

                var shift = int.TryParse(tokens[0], out _) ? 0 : 1;
                if (
                    tokens.Length > winPidCol + shift
                    && int.TryParse(tokens[pidCol + shift], out var rowPid)
                    && rowPid == reportedPid
                    && int.TryParse(tokens[winPidCol + shift], out var winPid)
                )
                {
                    return winPid;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs a short local diagnostic tool and returns its stdout —
    /// "" on any failure or timeout, which callers read as "no answer".</summary>
    private static async Task<string> RunToolForStdoutAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        using var process =
            Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {psi.FileName}");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync(); // drain, or a chatty tool deadlocks on a full pipe
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token);
            return await stdoutTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Timed out or unreadable: kill the straggler, report "no answer".
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            { /* lost the race: already gone */
            }
            return "";
        }
    }

    /// <summary>
    /// Resolves the live master process for a pid, triple-guarded against a
    /// stale or OS-reused pid: the process must be named <c>ssh</c>, must have
    /// started within two minutes of the master's spawn, and must run the
    /// resolved ssh binary (when both paths are comparable). The guards make a
    /// mis-match unlikely, not impossible — an ssh master another coordinator
    /// on this box spawned inside that window matches all three. Returns
    /// <c>(null, reason)</c> when gone or not ours; <c>(process, "live")</c>
    /// otherwise. Pid identity note: the pid must be a WINDOWS pid. What
    /// <c>ssh -O check</c> reports for a <c>-f</c>-forked master is its
    /// Cygwin-space pid, which does NOT equal the Windows pid — registration
    /// maps it via the sibling <c>ps</c> before it is stored anywhere.
    /// </summary>
    private static (Process? Process, string Reason) ProbeMasterProcess(
        int pid,
        DateTime spawnedAtUtc,
        string sshPath
    )
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);

            if (!process.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase))
            {
                var name = process.ProcessName;
                process.Dispose();
                return (null, $"name_mismatch({name})");
            }

            if (Math.Abs((process.StartTime.ToUniversalTime() - spawnedAtUtc).TotalSeconds) > 120)
            {
                process.Dispose();
                return (null, "start_time_mismatch");
            }

            // Best-effort binary check; MainModule can be unreadable (access, bitness).
            try
            {
                var modulePath = process.MainModule?.FileName;
                if (
                    modulePath is not null
                    && Path.IsPathRooted(sshPath)
                    && !string.Equals(
                        Path.GetFullPath(modulePath),
                        Path.GetFullPath(sshPath),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    process.Dispose();
                    return (null, $"binary_mismatch({modulePath})");
                }
            }
            catch
            { /* unreadable module: name + start-time guards carry the decision */
            }

            return (process, "live");
        }
        catch (ArgumentException)
        {
            return (null, "gone");
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            return (null, "gone");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Name/start-time read denied — a pid recycled to another user's
            // process. We spawned our master under this user, so ours would be
            // readable; still, "cannot inspect" is not "confirmed gone".
            process?.Dispose();
            return (null, "unreadable");
        }
    }

    /// <summary>
    /// Kills the master process by pid behind the identity guards. Returns a
    /// short outcome string for teardown events.
    /// </summary>
    private static string TryKillMasterProcess(
        int? masterPid,
        DateTime spawnedAtUtc,
        string sshPath
    )
    {
        if (masterPid is not int pid)
        {
            return "skipped_unknown_pid";
        }

        var (process, reason) = ProbeMasterProcess(pid, spawnedAtUtc, sshPath);
        if (process is null)
        {
            return reason == "gone" ? "already_exited" : $"skipped_{reason}";
        }

        try
        {
            using (process)
            {
                process.Kill();
                // A survivor still squats the forward ports — surface it so the
                // same-port reopen's bind failure is self-explaining.
                return process.WaitForExit(2000) ? "killed" : "killed_exit_timeout";
            }
        }
        catch (InvalidOperationException)
        {
            return "already_exited";
        }
        catch (Exception ex)
        {
            return $"failed({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Synchronous journal-driven teardown of every ControlMaster THIS process
    /// registered, for abort paths where the parent's <c>finally</c> (and thus
    /// <see cref="DrainAsync"/>) never ran — <c>Environment.Exit</c> does not
    /// unwind the stack. Consumes the on-disk journal, not <c>_masters</c>:
    /// <see cref="DrainAsync"/> snapshot-clears the dictionary <em>before</em>
    /// running its exits, so on the one path where drain's own teardown failed
    /// an in-memory read would silently no-op. No-op when no journal exists
    /// (local-only fleet, xUnit child, or every master already confirmed gone);
    /// idempotent per call; bounded per master.
    /// </summary>
    public static void EmergencyTeardownOwnMasters()
    {
        lock (EmergencyTeardownLock)
        {
            // Re-snapshot until no unseen (host, pid) remains: a health-monitor
            // respawn that raced the shutdown gate can register a fresh master
            // while the first pass runs. The attempted-set keeps each pair
            // single-shot; the pass cap bounds a pathological register loop.
            var attempted = new HashSet<(string HostId, int? Pid)>();
            for (var pass = 0; pass < 4; pass++)
            {
                var pending = SshMasterJournal
                    .SnapshotOwnMasters()
                    .Where(m => attempted.Add((m.HostId, m.MasterPid)))
                    .ToList();
                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var m in pending)
                {
                    try
                    {
                        var result = TerminateMasterCoreAsync(
                                m.SshPath,
                                m.SshDestination,
                                m.ControlPath,
                                m.MasterPid,
                                m.SpawnedAtUtc,
                                exitTimeout: TimeSpan.FromSeconds(3)
                            )
                            .GetAwaiter()
                            .GetResult();

                        if (result.Gone)
                        {
                            SshMasterJournal.RemoveMaster(m.HostId, m.MasterPid, m.SpawnedAtUtc);
                        }

                        EmitSafe(
                            "ssh_master_emergency_teardown",
                            new
                            {
                                host_id = m.HostId,
                                gone = result.Gone,
                                exitCode = result.ExitCode,
                                killOutcome = result.KillOutcome,
                            }
                        );

                        // The -E tail is the only record of a silent-timeout drop —
                        // fold it into diagnostics like the drain path does.
                        var tail = ReadMasterLogTail(m.LogPath, MaxLogTailBytes);
                        if (tail.Length > 0)
                        {
                            EmitSafe(
                                "ssh_master_log",
                                new
                                {
                                    host_id = m.HostId,
                                    logPath = m.LogPath,
                                    tail,
                                }
                            );
                        }
                    }
                    catch
                    { /* teardown must never block process exit */
                    }
                }
            }
        }
    }

    private static readonly object EmergencyTeardownLock = new();

    /// <summary>
    /// Preflight-time cross-run reap: for every ssh-master journal in the temp
    /// dir whose coordinator process is dead (PID + start-time liveness),
    /// terminally tears down the listed masters and deletes the journal.
    /// Mirrors the Docker-side <c>SweepStaleResourcesAsync</c> startup pattern
    /// (label-scoped resources reaped by the next run). A journal whose
    /// coordinator is alive — including a hung one — is never touched: sibling
    /// safety outranks reap eagerness, and F5's exit guarantee is what makes
    /// "coordinator dead" eventually true. Cancellation is a graceful stop,
    /// not an error: the in-progress journal is kept for the next run and the
    /// partial count comes back with <c>Stopped = true</c> — the caller
    /// decides whether the stop was its own sub-budget or a real abort.
    /// </summary>
    public static async Task<(int Reaped, bool Stopped)> ReapOrphanedMastersAsync(
        string fallbackSshPath,
        CancellationToken ct = default
    )
    {
        var reaped = 0;
        foreach (var orphan in SshMasterJournal.SnapshotOrphanedJournals())
        {
            var allGone = true;
            foreach (var m in orphan.Journal.Masters)
            {
                if (ct.IsCancellationRequested)
                {
                    return (reaped, true);
                }

                // Prefer the binary the master was spawned with (its identity
                // guard); fall back to the freshly resolved one when it no
                // longer exists — otherwise the teardown can never run and the
                // journal would be retried forever.
                var sshPath =
                    !string.IsNullOrEmpty(m.SshPath) && File.Exists(m.SshPath)
                        ? m.SshPath
                        : fallbackSshPath;
                try
                {
                    var result = await TerminateMasterCoreAsync(
                        sshPath,
                        m.SshDestination,
                        m.ControlPath,
                        m.MasterPid,
                        m.SpawnedAtUtc,
                        exitTimeout: TimeSpan.FromSeconds(3),
                        ct: ct
                    );
                    EmitSafe(
                        "ssh_orphan_master_reaped",
                        new
                        {
                            host_id = m.HostId,
                            coordinatorPid = orphan.Journal.CoordinatorPid,
                            controlPath = m.ControlPath,
                            gone = result.Gone,
                            killOutcome = result.KillOutcome,
                        }
                    );
                    if (result.Gone)
                    {
                        reaped++;
                    }
                    else
                    {
                        allGone = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    return (reaped, true); // journal kept; the next run retries
                }
                catch
                {
                    allGone = false;
                }
            }

            // Keep the journal when any master survived — the next run retries.
            if (allGone)
            {
                SshMasterJournal.DeleteJournalIfUnchanged(orphan);
            }
        }

        return (reaped, false);
    }

    /// <summary>
    /// Re-opens every forward THIS process registered for <paramref name="hostId"/> at its
    /// ORIGINAL coordinator port, after a master respawn (the old master's listeners died
    /// with it) or a canary-detected forward drop. Same-port is the point: consumers pin
    /// these ports (the daemon-socket forward feeds Docker.DotNet endpoints in both
    /// processes via <see cref="Helpers.RunArtifactNames.HostTunnelsEnv"/>) and have no
    /// re-resolve path. Per-forward failures are evented and skipped — a partial reopen
    /// beats none. Returns false when any forward could not be reopened (the monitor's
    /// escalation signal).
    ///
    /// <paramref name="skipAliveListeners"/> is for the canary-drop path only, where the
    /// master is unchanged: a forward whose local listener still accepts is live, and
    /// reopening it would just bind-collide into a noise event. Post-respawn reopens must
    /// NOT skip — the old master's listeners died with it, and a squatting orphan's
    /// listener must surface as the bind failure it causes, not be mistaken for healthy.
    /// </summary>
    private async Task<bool> ReopenRegisteredForwardsAsync(
        string hostId,
        bool skipAliveListeners,
        CancellationToken ct
    )
    {
        ForwardEntry[] entries;
        lock (_lock)
        {
            entries = _forwards.Values.Where(f => f.HostId == hostId).ToArray();
        }
        if (entries.Length == 0)
        {
            return true;
        }

        var master = TryGetMaster(hostId);
        if (master is null)
        {
            return false;
        }

        var allReopened = true;
        foreach (var entry in entries)
        {
            if (skipAliveListeners && await IsListenerAcceptingAsync(entry.CoordinatorPort, ct))
            {
                continue;
            }

            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await OpenForwardOnMasterAsync(
                    master,
                    entry.CoordinatorPort,
                    entry.MappedPort,
                    entry.RemoteSocketPath,
                    ct
                );
                await ProbeListenerAsync(entry.CoordinatorPort, TimeSpan.FromSeconds(2), ct);
                EmitSafe(
                    "tunnel_forward_reopened",
                    new
                    {
                        host_id = hostId,
                        coordinator_port = entry.CoordinatorPort,
                        mapped_port = entry.RemoteSocketPath is null
                            ? (int?)entry.MappedPort
                            : null,
                        remote_socket = entry.RemoteSocketPath,
                        durationMs = ElapsedMs(startedAt),
                    }
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                allReopened = false;
                EmitSafe(
                    "tunnel_forward_reopen_failed",
                    new
                    {
                        host_id = hostId,
                        coordinator_port = entry.CoordinatorPort,
                        mapped_port = entry.RemoteSocketPath is null
                            ? (int?)entry.MappedPort
                            : null,
                        remote_socket = entry.RemoteSocketPath,
                        message = ex.Message,
                        durationMs = ElapsedMs(startedAt),
                    }
                );
            }
        }

        return allReopened;
    }

    /// <summary>Quick accept-probe backing <c>skipAliveListeners</c>; false on refuse or
    /// a 200ms silence (a wedged-but-accepting listener is the Wedged branch's business,
    /// not this probe's).</summary>
    private static async Task<bool> IsListenerAcceptingAsync(int port, CancellationToken ct)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(TimeSpan.FromMilliseconds(200));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, attemptCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
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
        _draining = true;

        ForwardEntry[] forwardSnapshot;
        HostMaster[] masterSnapshot;
        lock (_lock)
        {
            forwardSnapshot = _forwards.Values.ToArray();
            _forwards.Clear();
            masterSnapshot = _masters.Values.ToArray();
            _masters.Clear();
            _canaryWedgeStreaks.Clear();
            _reopenFailStreaks.Clear();
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

        // Terminal teardown: -O exit, pid-kill fallback, unlink only once the
        // process is confirmed gone. A survivor keeps its socket (the only
        // remaining handle) and its journal entry, so the emergency path and
        // the next run's reaper can still reach it.
        var result = await TerminateMasterCoreAsync(
            _sshPath,
            master.SshDestination,
            master.ControlPath,
            master.MasterPid,
            master.SpawnedAtUtc,
            perCancelTimeout
        );

        if (result.Gone)
        {
            SshMasterJournal.RemoveMaster(master.HostId, master.MasterPid, master.SpawnedAtUtc);
        }

        EmitSafe(
            "ssh_master_exited",
            new
            {
                host_id = master.HostId,
                exitCode = result.ExitCode,
                gone = result.Gone,
                // Happy path stays lean: extra fields only when something failed.
                killOutcome = result.KillOutcome == "not_needed" ? null : result.KillOutcome,
                stderr = result.ExitCode != 0 ? result.ExitStderr : null,
                durationMs = ElapsedMs(startedAt),
            }
        );

        if (!result.Gone)
        {
            EmitSafe(
                "ssh_master_teardown_failed",
                new
                {
                    host_id = master.HostId,
                    masterPid = master.MasterPid,
                    controlPath = master.ControlPath,
                    killOutcome = result.KillOutcome,
                }
            );
        }

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

    private ProcessStartInfo NewSshPsi() => NewSshPsiFor(_sshPath);

    private static ProcessStartInfo NewSshPsiFor(string sshPath)
    {
        var psi = new ProcessStartInfo(sshPath)
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
    /// Sweeps the temp dir for stale ControlMaster sockets from prior runs
    /// whose journal was lost (journaled orphans are
    /// <see cref="ReapOrphanedMastersAsync"/>'s job). Run at preflight start,
    /// before any master spawn, so the specific delete in
    /// <see cref="RegisterHostMasterAsync"/> can't hit a sibling-occupied path.
    ///
    /// Reap-then-unlink, never unlink-first: a stale socket may still front a
    /// live orphan master, and unlinking it would strip the orphan's only
    /// remaining handle. <c>ssh -O exit</c> through the socket kills such a
    /// master (which removes its own socket on clean shutdown) before the
    /// leftover file is deleted. The destination argument is required by ssh's
    /// CLI but unused for mux commands — the socket carries the target (a
    /// missing socket fails with "Control socket connect" before any hostname
    /// resolution). Returns the number of stale sockets swept — reaped by the
    /// master's own clean shutdown and/or unlinked here. Cancellation is a
    /// graceful stop, not an error: unswept sockets are left for the next run
    /// and the partial count comes back with <c>Stopped = true</c>.
    /// </summary>
    public static async Task<(int Swept, bool Stopped)> CleanupStaleControlSocketsAsync(
        string sshPath,
        TimeSpan maxAge,
        CancellationToken ct = default
    )
    {
        var swept = 0;
        var tempDir = Path.GetTempPath();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(tempDir, "sdvd-test-ssh-*");
        }
        catch
        {
            return (0, false);
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var journaled = SshMasterJournal.SnapshotJournaledControlPaths();
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested)
            {
                return (swept, true);
            }

            try
            {
                // A journal-referenced socket has an owner (its coordinator, or
                // the orphan reaper once that coordinator dies) — age-sweeping
                // it would kill a live sibling's master or strip a kept
                // survivor's only handle. This sweep is strictly the fallback
                // for sockets whose journal was lost. Keyed by file name (a
                // unique hash) so a sibling's differently-spelled temp dir
                // (e.g. an 8.3 short path) can't defeat the exemption.
                if (journaled.Contains(Path.GetFileName(file)))
                {
                    continue;
                }

                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }

                if (info.LastWriteTimeUtc > cutoff)
                {
                    continue;
                }

                try
                {
                    var psi = NewSshPsiFor(sshPath);
                    psi.ArgumentList.Add("-O");
                    psi.ArgumentList.Add("exit");
                    psi.ArgumentList.Add("-o");
                    psi.ArgumentList.Add($"ControlPath={file}");
                    psi.ArgumentList.Add("sdvd-orphan");
                    await RunSshToCompletionAsync(psi, TimeSpan.FromSeconds(3), ct);
                }
                catch (OperationCanceledException)
                {
                    return (swept, true); // this socket left for the next run
                }
                catch
                { /* dead socket: nothing to reap */
                }

                info.Refresh();
                if (info.Exists)
                {
                    info.Delete();
                }
                swept++;
            }
            catch (UnauthorizedAccessException)
            { /* shared /tmp on Linux: not ours */
            }
            catch (IOException)
            { /* in use by another live master, or transient */
            }
        }
        return (swept, false);
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

        /// <summary>
        /// WINDOWS pid of the live master process: <c>ssh -O check</c>'s
        /// "Master running (pid=N)" value mapped out of Cygwin pid space at
        /// registration (they differ for the <c>-f</c>-forked master). Owned
        /// masters only (null on adopted child entries — the child never
        /// kills); null when the parse or mapping missed, which downgrades
        /// every kill to a no-op and teardown to check-based confirmation.
        /// </summary>
        public int? MasterPid { get; init; }

        /// <summary>
        /// When the master was spawned. Guards the respawn-path kill against OS pid
        /// reuse: only a process whose start time is near this is eligible.
        /// </summary>
        public DateTime SpawnedAtUtc { get; init; }
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
