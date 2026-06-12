using JunimoTestClient.Auth;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace JunimoTestClient.Diagnostics;

/// <summary>
/// Monitors game health and detects freezes/hangs.
/// </summary>
public class HealthWatchdog
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;

    // Tick timestamps are written by the game thread and read by the watchdog
    // background thread and by HTTP handler threads (GetStatus). Use Interlocked
    // for tear-free cross-thread reads — the pattern mirrors ModEntry._lastGameTickTicks.
    private long _lastTickTicks = DateTime.UtcNow.Ticks;
    private long _previousTickTicks = DateTime.UtcNow.Ticks;
    private long _tickCount = 0;
    private bool _isHealthy = true;
    private string? _lastUnhealthyReason;
    private readonly DateTime _startTime = DateTime.UtcNow;

    // Consider unhealthy if no tick for this many milliseconds
    private const int FreezeThresholdMs = 5000;

    // Proactive stall logging: warn when the game thread stops ticking for this long.
    private const int StallWarnThresholdMs = 3000;

    private Thread? _watchdogThread;
    private volatile bool _shutdown;
    private bool _stallLogged;

    public HealthWatchdog(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public void Start()
    {
        // UnvalidatedUpdateTicked fires during save too; regular UpdateTicked is
        // suppressed during save and would produce false-positive stalls across
        // /newgame. Mirrors ModEntry.OnUpdateTicked.
        _helper.Events.Specialized.UnvalidatedUpdateTicked += OnUpdateTicked;

        _shutdown = false;
        _stallLogged = false;
        _watchdogThread = new Thread(WatchdogLoop) { Name = "HealthWatchdog", IsBackground = true };
        _watchdogThread.Start();

        _monitor.Log("Health watchdog started", LogLevel.Trace);
    }

    private void OnUpdateTicked(object? sender, UnvalidatedUpdateTickedEventArgs e)
    {
        var now = DateTime.UtcNow.Ticks;
        // Capture the previous tick's timestamp before overwriting so the resume
        // logger can report the full stall duration.
        var previous = Interlocked.Exchange(ref _lastTickTicks, now);
        Interlocked.Exchange(ref _previousTickTicks, previous);
        Interlocked.Increment(ref _tickCount);
        _isHealthy = true;
    }

    private void WatchdogLoop()
    {
        while (!_shutdown)
        {
            try
            {
                var lastTick = new DateTime(Interlocked.Read(ref _lastTickTicks));
                var previousTick = new DateTime(Interlocked.Read(ref _previousTickTicks));
                var msSinceLastTick = (DateTime.UtcNow - lastTick).TotalMilliseconds;

                if (msSinceLastTick > StallWarnThresholdMs)
                {
                    if (!_stallLogged)
                    {
                        _monitor.Log(
                            $"[Watchdog] Game thread stalled: no tick for {msSinceLastTick:F0}ms",
                            LogLevel.Warn
                        );
                        ClientEventLog.Emit(
                            "client_health_stall_started",
                            new { lastTickMs = (long)msSinceLastTick }
                        );
                        _stallLogged = true;
                    }
                }
                else if (_stallLogged)
                {
                    // We previously warned; the game thread has resumed. Report the
                    // full gap between the tick that preceded the stall and the
                    // first tick after it.
                    var gapMs = (lastTick - previousTick).TotalMilliseconds;
                    if (gapMs > StallWarnThresholdMs)
                    {
                        _monitor.Log(
                            $"[Watchdog] Game thread resumed after {gapMs:F0}ms pause",
                            LogLevel.Info
                        );
                        ClientEventLog.Emit(
                            "client_health_stall_recovered",
                            new { durationMs = (long)gapMs }
                        );
                    }
                    _stallLogged = false;
                }
            }
            catch (Exception ex)
            {
                // Never let the watchdog thread die silently, but never escalate
                // to LogLevel.Error (would poison the test via ServerContainer).
                _monitor.Log($"[Watchdog] loop error: {ex.Message}", LogLevel.Warn);
            }

            Thread.Sleep(1000);
        }
    }

    /// <summary>
    /// Get current health status.
    /// </summary>
    public HealthStatus GetStatus()
    {
        var now = DateTime.UtcNow;
        var lastTick = new DateTime(Interlocked.Read(ref _lastTickTicks));
        var msSinceLastTick = (int)(now - lastTick).TotalMilliseconds;
        var isFrozen = msSinceLastTick > FreezeThresholdMs;

        if (isFrozen)
        {
            _isHealthy = false;
            _lastUnhealthyReason = $"No tick for {msSinceLastTick}ms";
        }

        return new HealthStatus
        {
            Healthy = _isHealthy && !isFrozen,
            TickCount = Interlocked.Read(ref _tickCount),
            MsSinceLastTick = msSinceLastTick,
            IsFrozen = isFrozen,
            FreezeThresholdMs = FreezeThresholdMs,
            LastUnhealthyReason = isFrozen ? _lastUnhealthyReason : null,
            UptimeSeconds = (int)(now - _startTime).TotalSeconds,
            GalaxyReady = ClientAuthService.GalaxyReady,
            GalaxyState = ClientAuthService.GalaxyState,
        };
    }

    public void Stop()
    {
        _helper.Events.Specialized.UnvalidatedUpdateTicked -= OnUpdateTicked;
        _shutdown = true;
        _watchdogThread?.Join(TimeSpan.FromSeconds(1));
        _watchdogThread = null;
    }
}

public class HealthStatus
{
    public bool Healthy { get; set; }
    public long TickCount { get; set; }
    public int MsSinceLastTick { get; set; }
    public bool IsFrozen { get; set; }
    public int FreezeThresholdMs { get; set; }
    public string? LastUnhealthyReason { get; set; }
    public int UptimeSeconds { get; set; }

    /// <summary>
    /// Tri-state: null = Galaxy auth still pending, true = ready (signed in or
    /// LAN-only), false = failed/lost. The wait strategy uses this as the gate;
    /// the broker uses GalaxyState to skip Galaxy-broken clients on Steam leases.
    /// </summary>
    public bool? GalaxyReady { get; set; }

    /// <summary>
    /// Diagnostic free-form state: "uninitialized" | "pending" | "signed_in" |
    /// "failed" | "lost" | "disabled".
    /// </summary>
    public string? GalaxyState { get; set; }
}
