using System.Runtime.InteropServices;
using Docker.DotNet;
using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Static registry for synchronous cleanup actions that run on process termination.
/// When tests are terminated mid-run (Ctrl+C timeout, terminal close), DisposeAsync chains
/// may not complete. This class ensures critical resources (game client process, Docker containers)
/// are cleaned up.
///
/// Cleanup fires from three paths:
/// 1. RunAll(): called explicitly from the Ctrl+C timeout before Environment.Exit
/// 2. AppDomain.ProcessExit: backup for other termination scenarios
/// 3. SIGHUP (CTRL_CLOSE_EVENT on Windows): terminal window closed.
///    .NET 10 does not register a default handler for SIGHUP; this class
///    installs one so cleanup runs on graceful shell-driven shutdown.
///
/// Usage:
/// - Call EnsureRegistered() early in the process (e.g., Program.cs)
/// - Register("name", action) when a resource is created
/// - Unregister("name") after normal DisposeAsync succeeds
/// - If the process exits before Unregister, the action fires automatically
///
/// All actions must be synchronous. They may run during process teardown.
/// </summary>
public static class EmergencyCleanup
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, Action> Actions = new();
    private static readonly Dictionary<string, Func<ValueTask>> Drainables = new();
    private static bool _registered;

    // Hold a reference so the signal registration isn't garbage collected
    private static PosixSignalRegistration? _sighupRegistration;

    // Set by the clean-exit path (no Ctrl+C, no UI Stop, no setup-phase
    // failure) to skip the final BulkCleanupLabeledResources sweep inside
    // RunAll. Per-test DisposeAsyncs already cleaned everything; the bulk
    // pass adds 5–10s of list+remove per host for no gain on a healthy run.
    // Abort paths intentionally never set this — they need the safety net
    // because in-flight DisposeAsyncs were cancelled.
    private static volatile bool _skipBulkSweep;

    private static int _backstopArmed;

    /// <summary>
    /// One-shot absolute-deadline watchdog for the abort path. Armed when an
    /// abort begins (Ctrl+C, UI Stop, SIGHUP); if the process is still alive at
    /// the deadline, <see cref="Environment.FailFast(string)"/> — not
    /// <c>Environment.Exit</c>, which re-enters <see cref="RunAll"/> via
    /// ProcessExit (which has no runtime time budget on modern .NET) and can
    /// hang on the same sweep that stalled the first attempt. FailFast skips
    /// ProcessExit handlers by design; by backstop time every cleanup layer has
    /// had its chance, and a leaked ssh master becomes the next run's preflight
    /// reaper's job — which only works once this coordinator is actually dead.
    /// The deadline is composed from the abort path's own phase budgets
    /// (<see cref="ShutdownBudgets.ComputeAbortBackstop"/>), never a hand-picked
    /// number.
    /// </summary>
    public static void ArmExitBackstop(string cause)
    {
        if (Interlocked.Exchange(ref _backstopArmed, 1) != 0)
        {
            return;
        }

        int drainableCount;
        lock (Lock)
        {
            drainableCount = Math.Max(1, Drainables.Count);
        }
        var hostCount = Math.Max(1, HostPoolAccessor.GetHostsForCleanup()?.Count ?? 1);
        var masterCount = SshMasterJournal.OwnMasterCount();
        var deadline = ShutdownBudgets.ComputeAbortBackstop(hostCount, drainableCount, masterCount);

        // Background thread: never keeps a healthy exit alive, only outlives it
        // when the process fails to die.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(deadline);
            Environment.FailFast(
                $"Abort backstop: process still alive {deadline.TotalSeconds:F0}s after abort "
                    + $"'{cause}' — terminating hard (ProcessExit handlers skipped)."
            );
        })
        {
            IsBackground = true,
            Name = "AbortBackstop",
        };
        watchdog.Start();
    }

    /// <summary>
    /// Skip the final <see cref="BulkCleanupLabeledResources"/> pass inside
    /// <see cref="RunAll"/>. Called from <c>Program.cs</c>'s outer finally on
    /// the clean exit path only. Resource escape from BOTH per-test
    /// DisposeAsync AND <see cref="Unregister"/>'s registered cleanup action
    /// would leak until the next run's <see cref="SweepStaleResourcesAsync"/>
    /// startup pass — bounded by "between two runs", an accepted trade-off
    /// for sub-second keypress-to-shell exit.
    /// </summary>
    public static void SkipBulkSweepOnExit() => _skipBulkSweep = true;

    /// <summary>
    /// Idempotently hooks AppDomain.ProcessExit and SIGHUP (terminal close).
    /// Call once early in the process lifetime.
    /// </summary>
    public static void EnsureRegistered()
    {
        lock (Lock)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            // .NET 10 does not register a default handler for SIGHUP (CTRL_CLOSE_EVENT on Windows).
            // Without this, closing the terminal window kills the process immediately with no cleanup.
            _sighupRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGHUP,
                ctx =>
                {
                    ctx.Cancel = true; // Prevent immediate termination
                    ArmExitBackstop("sighup");
                    RunAll();
                    Environment.Exit(130);
                }
            );
        }
    }

    /// <summary>
    /// Registers a named synchronous cleanup action.
    /// If the process exits before Unregister is called, the action will fire.
    /// </summary>
    public static void Register(string name, Action action)
    {
        lock (Lock)
        {
            Actions[name] = action;
        }
    }

    /// <summary>
    /// Removes a named cleanup action (called after normal cleanup succeeds).
    /// </summary>
    public static void Unregister(string name)
    {
        lock (Lock)
        {
            Actions.Remove(name);
        }
    }

    /// <summary>
    /// Registers an async drain action that runs before the synchronous cleanup
    /// actions in <see cref="RunAll"/>. Used for sinks (the infrastructure
    /// event log, named-pipe writers) that need to flush their channels to
    /// disk before destructive Docker cleanup tears down the file system.
    /// Each drain is bounded at 2 s; a hung drain doesn't block process exit.
    /// </summary>
    public static void RegisterDrainable(string name, Func<ValueTask> drainAsync)
    {
        lock (Lock)
        {
            Drainables[name] = drainAsync;
        }
    }

    /// <summary>
    /// Runs all registered cleanup actions. Safe to call multiple times, but
    /// only the drainables and actions are latched (snapshot-and-clear runs
    /// them once); the bulk sweep and the transport teardown re-run on every
    /// call and are idempotent per call instead. Call this directly before
    /// Environment.Exit for guaranteed cleanup.
    /// </summary>
    public static void RunAll()
    {
        Action[] actionSnapshot;
        Func<ValueTask>[] drainSnapshot;
        lock (Lock)
        {
            drainSnapshot = Drainables.Values.ToArray();
            Drainables.Clear();
            actionSnapshot = Actions.Values.ToArray();
            Actions.Clear();
        }

        // Drain sinks first (event log, pipe writers) so their channels reach
        // disk / IPC peer before destructive Docker cleanup tears down state.
        // Each drain is bounded so a hung sink can't block process exit.
        foreach (var drain in drainSnapshot)
        {
            try
            {
                drain().AsTask().Wait(ShutdownBudgets.DrainablePerSink);
            }
            catch
            {
                // Best effort; swallow exceptions during emergency cleanup
            }
        }

        foreach (var action in actionSnapshot)
        {
            try
            {
                action();
            }
            catch
            {
                // Best effort; swallow exceptions during emergency cleanup
            }
        }

        // Final catch-all: remove any Docker resources labeled sdvd.test=true that
        // individual handlers missed (e.g., handler never registered, or threw).
        // Skipped on the clean-exit path — Program.cs's outer finally calls
        // SkipBulkSweepOnExit when no abort fired. Per-test DisposeAsyncs have
        // already cleaned everything, so a second list+remove pass per host
        // burns 5–10s for no gain. Abort paths leave the flag clear so the
        // safety net runs.
        if (!_skipBulkSweep)
        {
            BulkCleanupLabeledResources();
        }

        // Transport teardown LAST. The bulk sweep above removes remote
        // containers THROUGH the ssh tunnel — tearing the master down any
        // earlier strands every remote container (an ssh leak traded for a
        // container leak). Journal-driven: no-op when no master exists
        // (local-only fleets, xUnit child) and idempotent per call.
        TunnelManager.EmergencyTeardownOwnMasters();
    }

    /// <summary>
    /// Bulk-removes Docker resources owned by THIS process across every
    /// configured Docker host. Scoping by run-id (E27) prevents one process's
    /// emergency cleanup from ripping out a concurrent sibling's
    /// containers/networks/volumes on a shared Docker daemon.
    ///
    /// Falls back to <c>sdvd.test=true</c> only when the run-id is unavailable
    /// (e.g., RunMetadata not yet initialized at process-exit time) -- in that
    /// case we accept the broader cleanup as the lesser evil over leaking
    /// orphan containers.
    ///
    /// Every call is bounded (<see cref="ShutdownBudgets"/>): tight per-call
    /// caps for remote (ssh://) hosts -- a hung SSH master must not block
    /// process exit -- and generous ones for local hosts, where a hung Docker
    /// Desktop named pipe plus the client's infinite timeout reproduces the
    /// same hang. Each host additionally gets a whole-sweep deadline.
    /// <see cref="Infrastructure.HostPoolAccessor.GetHostsForCleanup"/>
    /// returns the configured hosts; if HostPool is not yet initialized
    /// (early process exit), we fall back to the local default daemon.
    /// </summary>
    public static void BulkCleanupLabeledResources()
    {
        var runId = RunMetadata.RunId;
        var (labelKey, labelValue) = !string.IsNullOrEmpty(runId)
            ? ("sdvd.run-id", runId!)
            : ("sdvd.test", "true");

        foreach (var (client, isRemote, dispose) in EnumerateHostClientsForCleanup())
        {
            try
            {
                var perCallTimeout = isRemote
                    ? ShutdownBudgets.RemoteSweepPerCall
                    : ShutdownBudgets.LocalSweepPerCall;
                BulkRemoveOnHost(client, labelKey, labelValue, perCallTimeout);
            }
            catch
            { /* best effort */
            }
            finally
            {
                if (dispose)
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch { }
                }
            }
        }
    }

    private static IEnumerable<(
        DockerClient Client,
        bool IsRemote,
        bool Dispose
    )> EnumerateHostClientsForCleanup()
    {
        var hosts = HostPoolAccessor.GetHostsForCleanup();
        if (hosts != null && hosts.Count > 0)
        {
            foreach (var (client, isRemote) in hosts)
            {
                yield return (client, isRemote, false);
            }

            yield break;
        }

        // Fallback: HostPool not initialized yet (early exit). Use the local
        // default daemon so cleanup still runs in single-host scenarios.
        DockerClient? local = null;
        try
        {
            local = DockerEndpointConfig.Instance.CreateDockerClient();
        }
        catch { }
        if (local != null)
        {
            yield return (local, false, true);
        }
    }

    private static void BulkRemoveOnHost(
        DockerClient client,
        string labelKey,
        string labelValue,
        TimeSpan? perCallTimeout
    )
    {
        // Whole-host deadline on top of the per-call caps, so a pathological
        // resource count can't stretch the sweep past its phase budget.
        using var hostBudget = new CancellationTokenSource(ShutdownBudgets.BulkSweepPerHost);
        var ct = hostBudget.Token;

        // Containers first (they hold references to networks/volumes), then
        // networks, then volumes. Each call swallows per-resource errors.
        try
        {
            DockerOps
                .BulkForceRemoveContainersByLabelAsync(
                    client,
                    labelKey,
                    labelValue,
                    perCallTimeout,
                    ct
                )
                .GetAwaiter()
                .GetResult();
        }
        catch { }
        try
        {
            DockerOps
                .BulkRemoveNetworksByLabelAsync(client, labelKey, labelValue, perCallTimeout, ct)
                .GetAwaiter()
                .GetResult();
        }
        catch { }
        try
        {
            DockerOps
                .BulkRemoveVolumesByLabelAsync(client, labelKey, labelValue, perCallTimeout, ct)
                .GetAwaiter()
                .GetResult();
        }
        catch { }
    }

    /// <summary>
    /// Result of sweeping <c>sdvd.test=true</c>-labeled resources on a single host.
    /// Counts reflect resources <em>found</em>; per-resource removal errors are swallowed.
    /// </summary>
    public record HostSweepResult(
        string HostId,
        int ContainersRemoved,
        int NetworksRemoved,
        int VolumesRemoved,
        Exception? Error
    )
    {
        public int TotalRemoved => ContainersRemoved + NetworksRemoved + VolumesRemoved;
    }

    /// <summary>
    /// Startup-time sweep of leftover <c>sdvd.test=true</c> resources across all
    /// hosts. Self-heals after a Ctrl+C abort whose disposal exceeded the
    /// graceful budget. Always uses the broad label (not run-id) — the whole
    /// point is to hit prior runs whose run-id is unrelated to ours.
    ///
    /// <para>Yields one <see cref="HostSweepResult"/> per host as cleanup
    /// completes, so callers can emit live UI progress. Per-resource removal
    /// is best-effort; per-call timeout (5s for remote, unbounded for local)
    /// bounds individual Docker API calls so a hung remote daemon can't stall
    /// the run.</para>
    /// </summary>
    public static async IAsyncEnumerable<HostSweepResult> SweepStaleResourcesAsync(
        IReadOnlyList<DockerHost> hosts,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            var perCallTimeout = string.IsNullOrEmpty(host.SshDestination)
                ? (TimeSpan?)null
                : TimeSpan.FromSeconds(5);

            int containers = 0,
                networks = 0,
                volumes = 0;
            Exception? error = null;
            try
            {
                var client = host.ApiClient;
                containers = await DockerOps.BulkForceRemoveContainersByLabelAsync(
                    client,
                    "sdvd.test",
                    "true",
                    perCallTimeout,
                    ct
                );
                networks = await DockerOps.BulkRemoveNetworksByLabelAsync(
                    client,
                    "sdvd.test",
                    "true",
                    perCallTimeout,
                    ct
                );
                volumes = await DockerOps.BulkRemoveVolumesByLabelAsync(
                    client,
                    "sdvd.test",
                    "true",
                    perCallTimeout,
                    ct
                );
            }
            catch (Exception ex)
            {
                error = ex;
            }

            yield return new HostSweepResult(host.Id, containers, networks, volumes, error);
        }
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        // RunAll is idempotent; if already called from Ctrl+C timeout, this is a no-op
        RunAll();
    }
}
