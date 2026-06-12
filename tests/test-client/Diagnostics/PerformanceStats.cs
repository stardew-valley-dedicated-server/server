using System.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace JunimoTestClient.Diagnostics;

/// <summary>
/// Tracks performance statistics (FPS, tick time, memory).
/// </summary>
public class PerformanceStats
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;

    // FPS tracking
    private int _frameCount = 0;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;
    private double _currentFps = 0;

    // Tick timing
    private readonly Stopwatch _tickStopwatch = new();
    private double _lastTickMs = 0;
    private double _avgTickMs = 0;
    private double _maxTickMs = 0;
    private readonly Queue<double> _tickHistory = new();
    private const int TickHistorySize = 60;

    // TPS tracking
    private int _tickCount = 0;
    private double _currentTps = 0;
    private DateTime _lastTpsUpdate = DateTime.UtcNow;

    // FPS cap (0 = unlimited)
    private int _targetFps;

    // Memory tracking
    private long _lastMemoryBytes = 0;
    private DateTime _lastMemoryUpdate = DateTime.UtcNow;
    private DateTime _lastGcCollect = DateTime.UtcNow;

    public PerformanceStats(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
    }

    /// <summary>
    /// Set the FPS cap so stats can report the correct target.
    /// </summary>
    public void SetTargetFps(int fps) => _targetFps = fps;

    public void Start()
    {
        _helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
        _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _helper.Events.Display.Rendered += OnRendered;
        _monitor.Log("Performance stats tracking started", LogLevel.Trace);
    }

    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        _tickStopwatch.Restart();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _tickStopwatch.Stop();
        _lastTickMs = _tickStopwatch.Elapsed.TotalMilliseconds;

        // Track history for averaging
        _tickHistory.Enqueue(_lastTickMs);
        if (_tickHistory.Count > TickHistorySize)
        {
            _tickHistory.Dequeue();
        }

        _avgTickMs = _tickHistory.Average();
        _maxTickMs = Math.Max(_maxTickMs, _lastTickMs);

        // TPS tracking
        _tickCount++;
        var now = DateTime.UtcNow;
        var tpsElapsed = (now - _lastTpsUpdate).TotalSeconds;
        if (tpsElapsed >= 1.0)
        {
            _currentTps = _tickCount / tpsElapsed;
            _tickCount = 0;
            _lastTpsUpdate = now;
        }

        // Periodic background Gen 2 collect to keep the managed heap trimmed.
        // Non-blocking so it doesn't stall the game thread; runs concurrently.
        if ((now - _lastGcCollect).TotalSeconds >= 30)
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            _lastGcCollect = now;
        }

        // Update memory every second
        if ((now - _lastMemoryUpdate).TotalSeconds >= 1)
        {
            _lastMemoryBytes = GC.GetTotalMemory(false);
            _lastMemoryUpdate = now;
        }
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        _frameCount++;

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastFpsUpdate).TotalSeconds;

        if (elapsed >= 1.0)
        {
            _currentFps = _frameCount / elapsed;
            _frameCount = 0;
            _lastFpsUpdate = now;
        }
    }

    /// <summary>
    /// Get current performance statistics.
    /// </summary>
    public PerfStats GetStats()
    {
        var targetTps = (int)Math.Round(1000.0 / Game1.game1.TargetElapsedTime.TotalMilliseconds);

        return new PerfStats
        {
            Fps = Math.Round(_currentFps, 1),
            Tps = Math.Round(_currentTps, 1),
            TargetTps = targetTps,
            TargetFps = _targetFps > 0 ? _targetFps : targetTps,
            LastTickMs = Math.Round(_lastTickMs, 2),
            AvgTickMs = Math.Round(_avgTickMs, 2),
            MaxTickMs = Math.Round(_maxTickMs, 2),
            MemoryMb = Math.Round(_lastMemoryBytes / 1024.0 / 1024.0, 1),
            GcGen0 = GC.CollectionCount(0),
            GcGen1 = GC.CollectionCount(1),
            GcGen2 = GC.CollectionCount(2),
            TickHistorySize = _tickHistory.Count,
        };
    }

    /// <summary>
    /// Reset max tick tracking.
    /// </summary>
    public void ResetMax()
    {
        _maxTickMs = 0;
    }

    public void Stop()
    {
        _helper.Events.GameLoop.UpdateTicking -= OnUpdateTicking;
        _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _helper.Events.Display.Rendered -= OnRendered;
    }
}

public class PerfStats
{
    public double Fps { get; set; }
    public double Tps { get; set; }
    public int TargetTps { get; set; }
    public int TargetFps { get; set; }
    public double LastTickMs { get; set; }
    public double AvgTickMs { get; set; }
    public double MaxTickMs { get; set; }
    public double MemoryMb { get; set; }
    public int GcGen0 { get; set; }
    public int GcGen1 { get; set; }
    public int GcGen2 { get; set; }
    public int TickHistorySize { get; set; }
}
