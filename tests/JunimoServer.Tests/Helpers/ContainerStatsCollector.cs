using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Bundled stats data passed through the event bus pipeline.
/// Nullable fields indicate data that is genuinely unavailable
/// (game not started, no previous sample for rate computation, cgroup v2 missing blkio).
/// </summary>
public sealed record InstanceStatsData
{
    // Docker container stats
    public double CpuPercent { get; init; }
    public double MemoryMb { get; init; }
    public int CpuCount { get; init; }
    public double TotalMemoryMb { get; init; }

    // Game /stats endpoint
    public double? Fps { get; init; }
    public double? Tps { get; init; }
    public double? AvgTickMs { get; init; }
    public double? GameMemoryMb { get; init; }
    public int? TargetTps { get; init; }
    public int? TargetFps { get; init; }

    // GC rate (computed from mod's gcGen0/1/2 deltas)
    public double? GcRate { get; init; }

    // Game thread queue
    public int? PendingActions { get; init; }
    public double? GameThreadWaitMs { get; init; }

    // Network I/O rates
    public double? NetRxBytesPerSec { get; init; }
    public double? NetTxBytesPerSec { get; init; }

    // Block I/O rates
    public double? BlkReadBytesPerSec { get; init; }
    public double? BlkWriteBytesPerSec { get; init; }

    // Container memory limit (0 = no limit set)
    public double MemoryLimitMb { get; init; }
}

/// <summary>
/// Streams Docker container stats via the Docker Engine API and emits
/// instance_stats events via SetupEventBus at ~1s intervals.
///
/// Uses Docker.DotNet's streaming stats API (one persistent stream per container)
/// instead of spawning <c>docker stats --no-stream</c> processes. This gives true
/// ~1s resolution since Docker pushes stats at its native sampling interval.
/// </summary>
public static class ContainerStatsCollector
{
    private sealed class StatsSnapshot
    {
        public double CpuPercent { get; init; }
        public double MemoryMb { get; init; }
        public double NetRxBytes { get; init; }
        public double NetTxBytes { get; init; }
        public double BlkReadBytes { get; init; } = -1; // -1 = no blkio data (cgroup v2)
        public double BlkWriteBytes { get; init; } = -1;
        public double MemoryLimitMb { get; init; }
    }

    private sealed class InstanceEntry
    {
        public required string InstanceId { get; init; }
        public required string ContainerId { get; init; }
        public required string ContainerName { get; init; }
        public string? ApiBaseUrl { get; init; }
        public required string HostId { get; init; }
        public required DockerClient Client { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? StreamTask { get; set; }
        public volatile StatsSnapshot? Latest;

        // Previous sample for rate computation
        public StatsSnapshot? Previous;
        public GameStatsResponse? PreviousGame;
        public DateTime PreviousTimestamp;

        // Per-instance flood-guard counters for structured-event emits in the
        // stats stream and the game-stats poll. Cumulative across the entry's
        // lifetime — a fresh container gets a fresh entry and thus a fresh
        // budget. Updated atomically via ShouldEmitStrike below.
        public int DockerStatsFailureStreak;
        public int GameStatsFailureStreak;
    }

    private static readonly ConcurrentDictionary<string, InstanceEntry> _instances = new();
    private static CancellationTokenSource? _cts;
    private static Task? _emissionLoop;
    private static volatile bool _started;

    private static int _cpuCount;
    private static double _totalMemoryMb;

    // Reused HttpClient for /stats polling. HttpClient is thread-safe and designed for reuse.
    // Creating new HttpClient per request wastes sockets and adds GC pressure.
    private static readonly HttpClient _statsHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static int CpuCount => _cpuCount;
    public static double TotalMemoryMb => _totalMemoryMb;

    public static void Register(
        string instanceId,
        string containerId,
        string containerName,
        Infrastructure.DockerHost host,
        string? apiBaseUrl = null
    )
    {
        // SDVD_TEST_STATS=none disables the entire collector — neither the
        // Docker stats stream nor the /stats HTTP poll fires. The UI's
        // instance_stats graphs render empty arrays gracefully.
        if (TestStats.Level == TestStatsLevel.None)
        {
            return;
        }

        var entry = new InstanceEntry
        {
            InstanceId = instanceId,
            ContainerId = containerId,
            ContainerName = containerName.TrimStart('/'),
            // SDVD_TEST_STATS=docker drops the per-container HTTP /stats fan-out
            // by zeroing the apiBaseUrl. The Docker stats stream still runs, so
            // CPU / memory / network graphs are populated.
            ApiBaseUrl = TestStats.Level == TestStatsLevel.DockerAndGame ? apiBaseUrl : null,
            HostId = host.Id,
            Client = host.ApiClient,
        };
        _instances[instanceId] = entry;

        if (_started)
        {
            entry.StreamTask = StartStreamAsync(entry);
        }
    }

    public static void Unregister(string instanceId)
    {
        if (_instances.TryRemove(instanceId, out var entry))
        {
            try
            {
                entry.Cts.Cancel();
            }
            catch { }
            try
            {
                entry.Cts.Dispose();
            }
            catch { }
        }
    }

    public static void Start()
    {
        if (_started)
        {
            return;
        }
        // No stream, no emission loop, no HTTP poller when stats are off.
        if (TestStats.Level == TestStatsLevel.None)
        {
            return;
        }

        _started = true;
        _cts = new CancellationTokenSource();
        // SuppressFlow: the emission loop runs for the whole process and emits
        // instance_stats events across every test. Inheriting the constructing
        // test's TestContext.Current would attribute every later /stats poll to
        // it. See .claude/rules/asynclocal-pitfalls.md.
        using (ExecutionContext.SuppressFlow())
        {
            _ = InitializeAsync(_cts.Token);
        }
    }

    public static void Stop()
    {
        _started = false;

        try
        {
            _cts?.Cancel();
        }
        catch { }

        foreach (var (_, entry) in _instances)
        {
            try
            {
                entry.Cts.Cancel();
            }
            catch { }
            try
            {
                entry.Cts.Dispose();
            }
            catch { }
        }
        _instances.Clear();

        // Note: per-host DockerClients are owned by HostPool, not by this
        // collector — disposing them here would break other consumers.

        try
        {
            _cts?.Dispose();
        }
        catch { }
        _cts = null;
    }

    private static async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            // Surface CPU/memory totals from the first host for the headline UI
            // numbers. Per-host preflight (with host_id) is emitted by HostPool.
            var first = Infrastructure.HostPool.Instance.First;
            var info = await first.ApiClient.System.GetSystemInfoAsync(ct);
            _cpuCount = (int)info.NCPU;
            _totalMemoryMb = info.MemTotal / (1024.0 * 1024.0);

            // Start streams for any containers registered before Start() was called
            foreach (var (_, entry) in _instances)
            {
                if (entry.StreamTask == null)
                {
                    entry.StreamTask = StartStreamAsync(entry);
                }
            }

            _emissionLoop = EmissionLoopAsync(ct);
        }
        catch
        {
            // Non-fatal; stats will be unavailable
        }
    }

    // Cumulative-failure flood guard for the structured emits inside the
    // per-tick hot loops. Increments the counter atomically and returns true
    // only for the first FailureStrikeLimit failures. No reset-on-success:
    // these are bug-shaped failures (parse / shape changes, math NaN paths)
    // that recur every tick once they start; we want first-N reports total,
    // not first-N per streak. Mirrors RendererDispatchGuard's strike threshold
    // (3) but per-instance instead of per-renderer.
    private const int FailureStrikeLimit = 3;

    private static bool ShouldEmitStrike(ref int counter) =>
        Interlocked.Increment(ref counter) <= FailureStrikeLimit;

    private static Task StartStreamAsync(InstanceEntry entry)
    {
        // SuppressFlow: stats stream lives for the container's whole lifetime,
        // emitting instance_stats events across many tests. Without this the
        // first test that triggers Register() poisons every later event with its
        // TestContext.Current. See .claude/rules/asynclocal-pitfalls.md.
        using var _ = ExecutionContext.SuppressFlow();
        return Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<ContainerStatsResponse>(response =>
                {
                    try
                    {
                        // The daemon omits these blocks for a container that has no
                        // CPU/memory accounting yet (just-created, or between restarts);
                        // skip the sample rather than synthesize zeroes.
                        if (
                            response.CPUStats is null
                            || response.PreCPUStats is null
                            || response.MemoryStats is null
                        )
                        {
                            return;
                        }

                        var cpuDelta =
                            response.CPUStats.CPUUsage.TotalUsage
                            - response.PreCPUStats.CPUUsage.TotalUsage;
                        var systemDelta =
                            (response.CPUStats.SystemUsage ?? 0)
                            - (response.PreCPUStats.SystemUsage ?? 0);
                        var onlineCpus = response.CPUStats.OnlineCPUs ?? 0;

                        double cpuPercent = 0;
                        if (systemDelta > 0 && onlineCpus > 0)
                        {
                            cpuPercent = (double)cpuDelta / systemDelta * onlineCpus * 100.0;
                        }

                        var memBytes = response.MemoryStats.Usage ?? 0;
                        if (response.MemoryStats.Stats?.TryGetValue("cache", out var cache) == true)
                        {
                            memBytes -= cache;
                        }

                        // Network I/O: sum all interfaces
                        double netRx = 0,
                            netTx = 0;
                        if (response.Networks != null)
                        {
                            foreach (var net in response.Networks.Values)
                            {
                                netRx += net.RxBytes;
                                netTx += net.TxBytes;
                            }
                        }

                        // Block I/O: sum read/write ops (-1 sentinel when cgroup v2 provides no data)
                        double blkRead = -1,
                            blkWrite = -1;
                        var blkioEntries = response.BlkioStats?.IoServiceBytesRecursive;
                        if (blkioEntries is { Count: > 0 })
                        {
                            blkRead = 0;
                            blkWrite = 0;
                            foreach (var e in blkioEntries)
                            {
                                if (string.Equals(e.Op, "read", StringComparison.OrdinalIgnoreCase))
                                {
                                    blkRead += e.Value;
                                }
                                else if (
                                    string.Equals(e.Op, "write", StringComparison.OrdinalIgnoreCase)
                                )
                                {
                                    blkWrite += e.Value;
                                }
                            }
                        }

                        // Memory limit
                        var memLimit = response.MemoryStats.Limit ?? 0;
                        double memLimitMb = memLimit > 0 ? memLimit / (1024.0 * 1024.0) : 0;

                        entry.Latest = new StatsSnapshot
                        {
                            CpuPercent = cpuPercent,
                            MemoryMb = memBytes / (1024.0 * 1024.0),
                            NetRxBytes = netRx,
                            NetTxBytes = netTx,
                            BlkReadBytes = blkRead,
                            BlkWriteBytes = blkWrite,
                            MemoryLimitMb = memLimitMb,
                        };
                    }
                    catch (Exception ex)
                    {
                        if (ShouldEmitStrike(ref entry.DockerStatsFailureStreak))
                        {
                            InfrastructureEventLog.Emit(
                                "docker_stats_snapshot_failed",
                                new { instanceId = entry.InstanceId, error = ex.Message }
                            );
                        }
                    }
                });

                await entry.Client.Containers.GetContainerStatsAsync(
                    entry.ContainerId,
                    new ContainerStatsParameters { Stream = true },
                    progress,
                    entry.Cts.Token
                );
            }
            catch (OperationCanceledException) { }
            catch { }
        });
    }

    private static async Task EmissionLoopAsync(CancellationToken ct)
    {
        try
        {
            using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await ticker.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (_instances.IsEmpty)
                    {
                        continue;
                    }

                    var mappings = _instances.ToArray();

                    // Poll game stats in parallel (best-effort, 3s timeout)
                    var gameStatsTasks = mappings
                        .Where(m => m.Value.ApiBaseUrl != null)
                        .Select(async m =>
                        {
                            try
                            {
                                var json = await _statsHttp.GetStringAsync(
                                    $"{m.Value.ApiBaseUrl}/stats",
                                    ct
                                );
                                var gameStats = JsonSerializer.Deserialize<GameStatsResponse>(
                                    json,
                                    GameStatsJsonOptions
                                );
                                return (m.Key, Stats: gameStats);
                            }
                            catch (JsonException ex)
                            {
                                if (ShouldEmitStrike(ref m.Value.GameStatsFailureStreak))
                                {
                                    InfrastructureEventLog.Emit(
                                        "game_stats_parse_failed",
                                        new { instanceId = m.Key, error = ex.Message }
                                    );
                                }

                                return (m.Key, Stats: (GameStatsResponse?)null);
                            }
                            catch
                            {
                                // HTTP-level failures (server starting up, brief disconnect) are
                                // expected and not worth a structured event.
                                return (m.Key, Stats: (GameStatsResponse?)null);
                            }
                        });
                    var gameStatsResults = await Task.WhenAll(gameStatsTasks);
                    var gameStats = gameStatsResults
                        .Where(r => r.Stats != null)
                        .ToDictionary(r => r.Key, r => r.Stats!);

                    foreach (var (instanceId, entry) in mappings)
                    {
                        var docker = entry.Latest;
                        gameStats.TryGetValue(instanceId, out var game);

                        if (docker == null && game == null)
                        {
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        var elapsed =
                            entry.PreviousTimestamp != default
                                ? (now - entry.PreviousTimestamp).TotalSeconds
                                : 0;

                        // Compute rates from deltas (only with a previous sample and reasonable elapsed time)
                        double? netRxRate = null,
                            netTxRate = null;
                        double? blkReadRate = null,
                            blkWriteRate = null;
                        double? gcRate = null;

                        if (elapsed > 0.1 && docker != null && entry.Previous != null)
                        {
                            netRxRate = Math.Max(
                                0,
                                (docker.NetRxBytes - entry.Previous.NetRxBytes) / elapsed
                            );
                            netTxRate = Math.Max(
                                0,
                                (docker.NetTxBytes - entry.Previous.NetTxBytes) / elapsed
                            );

                            // Block I/O: only compute rate if data is available (not -1 sentinel)
                            if (docker.BlkReadBytes >= 0 && entry.Previous.BlkReadBytes >= 0)
                            {
                                blkReadRate = Math.Max(
                                    0,
                                    (docker.BlkReadBytes - entry.Previous.BlkReadBytes) / elapsed
                                );
                                blkWriteRate = Math.Max(
                                    0,
                                    (docker.BlkWriteBytes - entry.Previous.BlkWriteBytes) / elapsed
                                );
                            }
                        }

                        if (elapsed > 0.1 && game != null && entry.PreviousGame != null)
                        {
                            var totalGcNow = game.GcGen0 + game.GcGen1 + game.GcGen2;
                            var totalGcPrev =
                                entry.PreviousGame.GcGen0
                                + entry.PreviousGame.GcGen1
                                + entry.PreviousGame.GcGen2;
                            gcRate = Math.Max(0, (totalGcNow - totalGcPrev) / elapsed);
                        }

                        var data = new InstanceStatsData
                        {
                            CpuPercent = docker?.CpuPercent ?? 0,
                            MemoryMb = docker?.MemoryMb ?? 0,
                            CpuCount = _cpuCount,
                            TotalMemoryMb = _totalMemoryMb,
                            Fps = game?.Fps,
                            Tps = game?.Tps,
                            AvgTickMs = game?.AvgTickMs,
                            GameMemoryMb = game?.MemoryMb,
                            TargetTps = game?.TargetTps,
                            TargetFps = game?.TargetFps,
                            GcRate = gcRate,
                            PendingActions = game?.PendingActions,
                            GameThreadWaitMs = game?.GameThreadWaitMs,
                            NetRxBytesPerSec = netRxRate,
                            NetTxBytesPerSec = netTxRate,
                            BlkReadBytesPerSec = blkReadRate,
                            BlkWriteBytesPerSec = blkWriteRate,
                            MemoryLimitMb = docker?.MemoryLimitMb ?? 0,
                        };

                        SetupEventBus.EmitInstanceStats(instanceId, data, entry.HostId);

                        // Store for next rate computation
                        entry.Previous = docker;
                        entry.PreviousGame = game;
                        entry.PreviousTimestamp = now;
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private sealed class GameStatsResponse
    {
        [JsonPropertyName("fps")]
        public double Fps { get; set; }

        [JsonPropertyName("tps")]
        public double Tps { get; set; }

        [JsonPropertyName("avgTickMs")]
        public double AvgTickMs { get; set; }

        [JsonPropertyName("memoryMb")]
        public double MemoryMb { get; set; }

        [JsonPropertyName("targetTps")]
        public int TargetTps { get; set; }

        [JsonPropertyName("targetFps")]
        public int TargetFps { get; set; }

        [JsonPropertyName("gcGen0")]
        public int GcGen0 { get; set; }

        [JsonPropertyName("gcGen1")]
        public int GcGen1 { get; set; }

        [JsonPropertyName("gcGen2")]
        public int GcGen2 { get; set; }

        [JsonPropertyName("pendingActions")]
        public int? PendingActions { get; set; }

        [JsonPropertyName("gameThreadWaitMs")]
        public double? GameThreadWaitMs { get; set; }
    }

    private static readonly JsonSerializerOptions GameStatsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
