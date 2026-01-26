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

    private DateTime _lastTickTime = DateTime.UtcNow;
    private long _tickCount = 0;
    private bool _isHealthy = true;
    private string? _lastUnhealthyReason;

    // Consider unhealthy if no tick for this many milliseconds
    private const int FreezeThresholdMs = 5000;

    public HealthWatchdog(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    public void Start()
    {
        _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _monitor.Log("Health watchdog started", LogLevel.Debug);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _lastTickTime = DateTime.UtcNow;
        _tickCount++;
        _isHealthy = true;
    }

    /// <summary>
    /// Get current health status.
    /// </summary>
    public HealthStatus GetStatus()
    {
        var now = DateTime.UtcNow;
        var msSinceLastTick = (int)(now - _lastTickTime).TotalMilliseconds;
        var isFrozen = msSinceLastTick > FreezeThresholdMs;

        if (isFrozen)
        {
            _isHealthy = false;
            _lastUnhealthyReason = $"No tick for {msSinceLastTick}ms";
        }

        return new HealthStatus
        {
            Healthy = _isHealthy && !isFrozen,
            TickCount = _tickCount,
            MsSinceLastTick = msSinceLastTick,
            IsFrozen = isFrozen,
            FreezeThresholdMs = FreezeThresholdMs,
            LastUnhealthyReason = isFrozen ? _lastUnhealthyReason : null,
            UptimeSeconds = (int)(now - _startTime).TotalSeconds
        };
    }

    private readonly DateTime _startTime = DateTime.UtcNow;

    public void Stop()
    {
        _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
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
}
