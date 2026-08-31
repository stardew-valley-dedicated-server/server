using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Docker.DotNet;
using Docker.DotNet.Models;
using JunimoServer.Tests.Schema.Events;

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

    // Age of the Docker sample the container fields were taken from, at emit time.
    // The stream pushes ~1/s, so an age well above that means the stream is down
    // and the container fields are a stale repeat. Null when no Docker sample exists.
    public int? SampleAgeMs { get; init; }
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
        public DateTime SampledAt { get; init; }
    }

    private sealed class InstanceEntry
    {
        public required string InstanceId { get; init; }
        public required string ContainerId { get; init; }
        public required string ContainerName { get; init; }

        // Live-read on every poll: the container's BaseUrl moves when its API forward
        // is reopened, so a captured string would point at a dead port.
        public Func<string?>? ApiBaseUrl { get; init; }
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
        // stats stream and the game-stats parse step. Cumulative across the
        // entry's lifetime — a fresh container gets a fresh entry and thus a
        // fresh budget. Updated atomically via ShouldEmitStrike below. Distinct
        // from GameStatsPollFailStreak, which resets on a successful poll.
        public int DockerStatsFailureCount;
        public int GameStatsParseFailureCount;

        // The daemon's stats stream delivers one sample per second, so a
        // sample arriving more than StatsGapThreshold after the previous one
        // is a transport stall (or daemon stall) worth its bounds on record.
        // UTC ticks, exchanged atomically: Progress<T> posts each sample to the
        // thread pool, so a burst of buffered samples after a stall runs
        // concurrently and would otherwise report the same gap twice.
        public long LastSampleTicks;

        // Consecutive failed /stats polls; reset on success. One
        // game_stats_poll_failed at streak start, one game_stats_poll_recovered at end.
        public int GameStatsPollFailStreak;
    }

    private static readonly TimeSpan StatsGapThreshold = TimeSpan.FromSeconds(5);

    private static void RecordStatsGap(InstanceEntry entry, DateTime nowUtc)
    {
        // Monotonic CAS: an out-of-order callback never moves the value backward
        // (a regressed value would time the next gap against a stale sample).
        long previousTicks;
        do
        {
            previousTicks = Volatile.Read(ref entry.LastSampleTicks);
            if (nowUtc.Ticks <= previousTicks)
            {
                return;
            }
        } while (
            Interlocked.CompareExchange(ref entry.LastSampleTicks, nowUtc.Ticks, previousTicks)
            != previousTicks
        );

        if (previousTicks == 0 || nowUtc.Ticks - previousTicks < StatsGapThreshold.Ticks)
        {
            return;
        }

        var previous = new DateTime(previousTicks, DateTimeKind.Utc);
        InfrastructureEventLog.Emit(
            TransportEventNames.ContainerStatsStreamGap,
            new StreamGapEvent(
                entry.InstanceId,
                entry.HostId,
                previous,
                nowUtc,
                (long)(nowUtc - previous).TotalMilliseconds
            )
        );
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
        Func<string?>? apiBaseUrl = null
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
        // Only the caller that removed the entry cancels and disposes its CTS, so
        // Unregister racing Stop can never cancel an already-disposed source.
        if (_instances.TryRemove(instanceId, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
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

        _cts?.Cancel();

        foreach (var instanceId in _instances.Keys.ToArray())
        {
            Unregister(instanceId);
        }

        // Note: per-host DockerClients are owned by HostPool, not by this
        // collector — disposing them here would break other consumers.

        _cts?.Dispose();
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Non-fatal for the run: no stats at all until the next process.
            InfrastructureEventLog.Emit(
                "stats_collector_init_failed",
                new
                {
                    exceptionType = ex.GetType().FullName,
                    message = ex.Message,
                    decision = "stats_unavailable",
                }
            );
        }
    }

    // Cumulative-failure flood guard for the structured emits inside the
    // per-tick hot loops. Increments the counter atomically and returns true
    // only for the first FailureStrikeLimit failures. No reset-on-success:
    // these are bug-shaped failures (parse / shape changes, math NaN paths)
    // that recur every tick once they start, so we want first-N reports over
    // the whole lifetime, not first-N each time a burst resumes. Borrows
    // RendererDispatchGuard's threshold value (3); unlike that guard, which
    // trips on consecutive failures and resets, this counter never resets.
    private const int FailureStrikeLimit = 3;

    private static bool ShouldEmitStrike(ref int counter) =>
        Interlocked.Increment(ref counter) <= FailureStrikeLimit;

    // Reconnect backoff for the stats stream: the first retry is quick (a transport
    // blip), later ones are paced so a long outage doesn't hammer the daemon.
    private static readonly TimeSpan StreamReconnectDelayMin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StreamReconnectDelayMax = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sole supervisor of the per-container Docker stats stream. Loops
    /// <c>GetContainerStatsAsync(Stream = true)</c>; on any non-cancellation exit it
    /// emits <c>docker_stats_stream_ended</c> with the reason and, unless the container
    /// is confirmed not running, reconnects and emits
    /// <c>docker_stats_stream_reconnected</c> on the first sample of the new stream.
    /// </summary>
    private static Task StartStreamAsync(InstanceEntry entry)
    {
        // SuppressFlow: stats stream lives for the container's whole lifetime,
        // emitting instance_stats events across many tests. Without this the
        // first test that triggers Register() poisons every later event with its
        // TestContext.Current. See .claude/rules/asynclocal-pitfalls.md.
        using var _ = ExecutionContext.SuppressFlow();
        // Captured once: Unregister disposes the CTS, after which .Token throws.
        var ct = entry.Cts.Token;
        return Task.Run(() => SuperviseStreamAsync(entry, ct));
    }

    private static async Task SuperviseStreamAsync(InstanceEntry entry, CancellationToken ct)
    {
        var attempt = 0;
        DateTime? endedAt = null;
        var reconnectAnnounced = true;

        void OnSample()
        {
            if (reconnectAnnounced || endedAt is null)
            {
                return;
            }

            reconnectAnnounced = true;
            InfrastructureEventLog.Emit(
                "docker_stats_stream_reconnected",
                new
                {
                    instanceId = entry.InstanceId,
                    containerName = entry.ContainerName,
                    attempt,
                    gapMs = (long)(DateTime.UtcNow - endedAt.Value).TotalMilliseconds,
                }
            );
        }

        var progress = BuildProgress(entry, OnSample);

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            string reason;
            Exception? error = null;
            try
            {
                await entry.Client.Containers.GetContainerStatsAsync(
                    entry.ContainerId,
                    new ContainerStatsParameters { Stream = true },
                    progress,
                    ct
                );
                // The daemon closes the stream when the container stops or the
                // transport underneath (e.g. an ssh-tunneled socket) drops.
                reason = "stream_closed";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                reason = "stream_failed";
                error = ex;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            endedAt = DateTime.UtcNow;
            reconnectAnnounced = false;

            bool? running;
            string? containerState;
            string? inspectError;
            try
            {
                (running, containerState, inspectError) = await IsContainerRunningAsync(entry, ct);
            }
            catch (OperationCanceledException)
            {
                // Unregister can cancel the CTS after the check above; the inspect
                // then throws, and this task is never awaited, so swallow it here.
                return;
            }
            var decision = running == false ? "gave_up_container_not_running" : "reconnect";
            InfrastructureEventLog.Emit(
                "docker_stats_stream_ended",
                new
                {
                    instanceId = entry.InstanceId,
                    containerName = entry.ContainerName,
                    attempt,
                    reason,
                    exceptionType = error?.GetType().FullName,
                    message = error?.Message,
                    innerExceptionType = error?.InnerException?.GetType().FullName,
                    innerMessage = error?.InnerException?.Message,
                    containerState,
                    inspectError,
                    decision,
                }
            );

            if (running == false)
            {
                return;
            }

            var delay = TimeSpan.FromTicks(
                Math.Min(StreamReconnectDelayMax.Ticks, StreamReconnectDelayMin.Ticks * attempt)
            );
            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// <c>Running</c> is false only when the daemon confirmed the container is not
    /// running (or no longer exists); null when the inspect itself failed, in which
    /// case the supervisor keeps reconnecting.
    /// </summary>
    private static async Task<(
        bool? Running,
        string? State,
        string? InspectError
    )> IsContainerRunningAsync(InstanceEntry entry, CancellationToken ct)
    {
        try
        {
            var inspect = await entry.Client.Containers.InspectContainerAsync(
                entry.ContainerId,
                ct
            );
            return (inspect.State?.Running == true, inspect.State?.Status, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DockerContainerNotFoundException)
        {
            return (false, "removed", null);
        }
        catch (Exception ex)
        {
            return (null, null, $"{ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static Progress<ContainerStatsResponse> BuildProgress(
        InstanceEntry entry,
        Action onSample
    )
    {
        return new Progress<ContainerStatsResponse>(response =>
        {
            try
            {
                onSample();
                RecordStatsGap(entry, DateTime.UtcNow);

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
                    (response.CPUStats.SystemUsage ?? 0) - (response.PreCPUStats.SystemUsage ?? 0);
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
                        else if (string.Equals(e.Op, "write", StringComparison.OrdinalIgnoreCase))
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
                    SampledAt = DateTime.UtcNow,
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
                if (ShouldEmitStrike(ref entry.DockerStatsFailureCount))
                {
                    InfrastructureEventLog.Emit(
                        "docker_stats_snapshot_failed",
                        new { instanceId = entry.InstanceId, error = ex.Message }
                    );
                }
            }
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

                    // Poll game stats in parallel (3s timeout each)
                    var gameStatsTasks = mappings
                        .Where(m => m.Value.ApiBaseUrl != null)
                        .Select(async m =>
                        {
                            var baseUrl = m.Value.ApiBaseUrl!();
                            if (baseUrl == null)
                            {
                                return (m.Key, Stats: (GameStatsResponse?)null);
                            }
                            try
                            {
                                var json = await _statsHttp.GetStringAsync($"{baseUrl}/stats", ct);
                                var gameStats = JsonSerializer.Deserialize<GameStatsResponse>(
                                    json,
                                    GameStatsJsonOptions
                                );
                                var failed = Interlocked.Exchange(
                                    ref m.Value.GameStatsPollFailStreak,
                                    0
                                );
                                if (failed > 0)
                                {
                                    InfrastructureEventLog.Emit(
                                        "game_stats_poll_recovered",
                                        new
                                        {
                                            instanceId = m.Key,
                                            baseUrl,
                                            failedPolls = failed,
                                        }
                                    );
                                }
                                return (m.Key, Stats: gameStats);
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (JsonException ex)
                            {
                                if (ShouldEmitStrike(ref m.Value.GameStatsParseFailureCount))
                                {
                                    InfrastructureEventLog.Emit(
                                        "game_stats_parse_failed",
                                        new { instanceId = m.Key, error = ex.Message }
                                    );
                                }

                                return (m.Key, Stats: (GameStatsResponse?)null);
                            }
                            catch (Exception ex)
                            {
                                // HTTP-level failure: server still starting, a transport blip,
                                // or a stale forward. One event per streak names which.
                                if (Interlocked.Increment(ref m.Value.GameStatsPollFailStreak) == 1)
                                {
                                    InfrastructureEventLog.Emit(
                                        "game_stats_poll_failed",
                                        new
                                        {
                                            instanceId = m.Key,
                                            baseUrl,
                                            exceptionType = ex.GetType().FullName,
                                            message = ex.Message,
                                            innerExceptionType = ex
                                                .InnerException?.GetType()
                                                .FullName,
                                        }
                                    );
                                }
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
                            SampleAgeMs =
                                docker != null
                                    ? (int)Math.Max(0, (now - docker.SampledAt).TotalMilliseconds)
                                    : null,
                        };

                        SetupEventBus.EmitInstanceStats(instanceId, data, entry.HostId);

                        // Store for next rate computation
                        entry.Previous = docker;
                        entry.PreviousGame = game;
                        entry.PreviousTimestamp = now;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ShouldEmitStrike(ref _emissionFailureCount))
                    {
                        InfrastructureEventLog.Emit(
                            "stats_emission_tick_failed",
                            new { exceptionType = ex.GetType().FullName, message = ex.Message }
                        );
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    // Emission-loop tick failures are bug-shaped (see ShouldEmitStrike); loop-wide
    // because one tick covers every instance.
    private static int _emissionFailureCount;

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
