using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Coordinates graceful shutdown between the Ctrl+C handler and the disposal chain.
/// On first Ctrl+C, <see cref="SignalShutdown"/> is called immediately, setting <see cref="IsShuttingDown"/>
/// so infrastructure components can suppress noise (poisoning, health checks, log stream errors).
/// The ForceKillTimeout thread waits on <see cref="WaitForGraceful"/> instead of a fixed sleep,
/// giving the graceful disposal chain time to stop ffmpeg, extract recordings, and copy artifacts.
/// Once disposal completes, <see cref="SignalGracefulComplete"/> releases the ForceKillTimeout thread.
///
/// Same static pattern as <see cref="EmergencyCleanup"/>, accessible from all layers without DI.
/// </summary>
public static class ShutdownCoordinator
{
    private static readonly ManualResetEventSlim GracefulDone = new(false);
    private static readonly CancellationTokenSource ShutdownCts = new();
    private static volatile bool _isShuttingDown;
    private static volatile bool _dockerDown;

    /// <summary>True after first Ctrl+C or Docker daemon failure. Infrastructure checks this to suppress noise.</summary>
    public static bool IsShuttingDown => _isShuttingDown;

    /// <summary>True when Docker daemon has been detected as unavailable (OOM restart, WSL crash).</summary>
    public static bool IsDockerDown => _dockerDown;

    /// <summary>
    /// Cancelled on first Ctrl+C or Docker daemon failure. Pass to disposal operations
    /// (recording extraction, container stop) so they abort immediately on shutdown
    /// instead of hanging indefinitely. Normal runs are unbounded.
    /// </summary>
    public static CancellationToken Token => ShutdownCts.Token;

    /// <summary>Called from Ctrl+C handler (first press).</summary>
    public static void SignalShutdown()
    {
        _isShuttingDown = true;
        try { ShutdownCts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
    }

    /// <summary>
    /// Marks Docker as down and triggers shutdown. Called from log stream error handlers
    /// when InternalServerError is detected (indicates Docker daemon restart/OOM).
    /// Thread-safe; only the first caller logs the message.
    /// </summary>
    public static void NotifyDockerDown(string reason)
    {
        if (_dockerDown || _isShuttingDown) return;
        _dockerDown = true;
        SignalShutdown();
        TestLog.Server($"Docker daemon failure detected: {reason}");
        TestLog.Server("Aborting test run, all containers are likely dead");
    }

    /// <summary>Called when graceful DisposeAsync completes. Releases the ForceKillTimeout thread.</summary>
    public static void SignalGracefulComplete() => GracefulDone.Set();

    /// <summary>
    /// Blocks until graceful disposal signals completion or timeout expires.
    /// Returns true if signaled, false if timed out.
    /// Used by ForceKillTimeout thread as a safety net.
    /// </summary>
    public static bool WaitForGraceful(TimeSpan timeout) => GracefulDone.Wait(timeout);
}
