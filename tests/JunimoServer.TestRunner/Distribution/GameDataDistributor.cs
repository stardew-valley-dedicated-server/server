using System.Diagnostics;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using JunimoServer.Tests.Schema.Events;
using JunimoServer.TestRunner.Rendering;

namespace JunimoServer.TestRunner.Distribution;

/// <summary>
/// One-time-per-host distribution of the Stardew Valley game-data volume from
/// the coordinator to each remote Docker host. Server containers mount the
/// <c>server_game-data</c> volume read-only and refuse to start when it's
/// empty (the entrypoint script polls forever waiting for game files). The
/// coordinator's volume is populated by <c>make setup</c>; remote hosts have
/// nothing comparable, so we stream the contents via Docker.DotNet's
/// container-archive APIs over each host's existing daemon-socket tunnel.
///
/// <para>Skip-when-populated: each remote host is probed for the canonical
/// <c>StardewValley</c> executable inside the volume. If present, the host
/// is treated as already provisioned and the (~250&#160;MB) transfer is skipped.
/// This keeps re-runs fast — the slow case is the first run on a fresh remote
/// host.</para>
///
/// <para>Pipeline: a busybox helper container on the coordinator mounts the
/// local <c>server_game-data</c> volume read-only at <c>/data</c>; we read its
/// contents via <c>GetArchiveFromContainerAsync</c>, which returns a non-seekable
/// tar stream. A second busybox helper container on the remote host mounts the
/// remote <c>server_game-data</c> volume read-write at <c>/data</c>, and we
/// write the tar stream to it via <c>ExtractArchiveToContainerAsync</c>. The
/// stream is consumed lazily — no temp file, no buffer.</para>
///
/// <para>This avoids per-host Steam credentials and extra accounts that a
/// per-host <c>steam-service download</c> approach would require: the coordinator
/// is the single source of truth for game files and pushes them downstream.</para>
/// </summary>
public sealed class GameDataDistributor : IDisposable
{
    // Spawns short-lived helper containers on both sides plus a tar pipeline;
    // heavier per-host on each daemon than image distribution. 2 keeps
    // named-pipe pressure on Windows local daemons sane. The asymmetry with
    // ImageDistributor (3) is deliberate: that path is streaming-only and
    // CPU-light per host.
    private const int MaxConcurrentTransfers = 2;

    private const string SetupCategory = "Runner";
    private const string ProbePath = "/data/StardewValley";
    private const string MountPath = "/data";

    /// <summary>
    /// Tiny image used to mount and read/write the volume contents. Pulled from
    /// Docker Hub on the remote daemon — adds ~1&#160;MB to the first-run cost.
    /// Pinning the tag keeps the probe deterministic across CI runs.
    /// </summary>
    private const string HelperImage = "busybox:1.36";

    private readonly string _gameDataVolumeName;
    private readonly int _maxRetries;
    private readonly DockerClient _localClient;

    public GameDataDistributor(string gameDataVolumeName)
    {
        _gameDataVolumeName = gameDataVolumeName;
        var raw = Environment.GetEnvironmentVariable("SDVD_GAME_DATA_TRANSFER_RETRIES");
        _maxRetries = int.TryParse(raw, out var r) && r >= 0 ? r : 1;
        _localClient = DockerEndpointConfig.Instance.CreateDockerClient();
    }

    public void Dispose() => _localClient.Dispose();

    public sealed record TransferResult(string HostId, bool Success, bool Skipped, string? Error);

    /// <summary>
    /// Provisions the game-data volume on every remote host that doesn't already
    /// have one. Local hosts are no-ops — the coordinator's volume is the source
    /// of truth and is assumed to be populated by <c>make setup</c>. Throws if
    /// the local volume is empty so the operator catches that mistake before
    /// every remote host wastes a probe round-trip.
    /// </summary>
    public async Task<List<TransferResult>> DistributeAsync(
        IReadOnlyList<DockerHost> hosts,
        ITestRenderer? renderer = null,
        CancellationToken ct = default)
    {
        await EnsureHelperImageAsync(_localClient, hostId: "local", ct);
        var localPopulated = await IsVolumePopulatedAsync(_localClient, ct);
        if (!localPopulated)
            throw new InvalidOperationException(
                $"Local volume '{_gameDataVolumeName}' is not populated " +
                $"(no '{ProbePath}' inside). Run 'make setup' before launching tests.");

        var remotes = hosts.Where(h => !string.IsNullOrEmpty(h.SshDestination)).ToList();
        if (remotes.Count == 0) return new List<TransferResult>();

        using var gate = new SemaphoreSlim(MaxConcurrentTransfers, MaxConcurrentTransfers);
        var tasks = remotes.Select(async h =>
        {
            await gate.WaitAsync(ct);
            try { return await ProvisionHostAsync(h, renderer, ct); }
            finally { gate.Release(); }
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<TransferResult> ProvisionHostAsync(
        DockerHost host, ITestRenderer? renderer, CancellationToken ct)
    {
        // Pull the helper image before any probe: IsVolumePopulatedAsync runs a
        // busybox container to test for the game-data marker, and on a fresh
        // remote daemon that CreateContainerAsync fails with "No such image"
        // before the populated-skip path can return. Mirrors the local
        // pre-check order in DistributeAsync.
        renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
            SetupStepStatus.Started, "ensuring helper image"));
        await EnsureHelperImageAsync(host.ApiClient, host.Id, ct);

        renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
            SetupStepStatus.InProgress, "checking game-data volume"));
        if (await IsVolumePopulatedAsync(host.ApiClient, ct))
        {
            InfrastructureEventLog.Emit("game_data_skip_populated", new { host_id = host.Id });
            renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
                SetupStepStatus.Completed, "already populated"));
            return new TransferResult(host.Id, true, Skipped: true, Error: null);
        }

        // Per-attempt try/catch mirrors ImageDistributor.TransferWithRetryAsync.
        // A real failure (e.g. remote disk full) will incur the full backoff
        // sequence before the run aborts — the cost is symmetric with image
        // distribution and is the price of retry symmetry.
        var delays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60) };
        Exception? last = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                InfrastructureEventLog.Emit("game_data_transfer_started", new { host_id = host.Id, attempt });
                renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
                    SetupStepStatus.Started,
                    attempt == 0 ? "transferring game files" : $"retry {attempt}: transferring game files"));
                var sw = Stopwatch.StartNew();
                var bytes = await TransferAsync(host, attempt, renderer, ct);
                sw.Stop();

                // Verify the transfer actually populated the volume — catches partial
                // streams and silent untar failures (e.g. disk-full on the remote).
                // Inside the per-attempt try so a partial extract on attempt 0 is
                // caught and triggers a retry rather than being silently observed
                // by attempt 1.
                if (!await IsVolumePopulatedAsync(host.ApiClient, ct))
                    throw new InvalidOperationException(
                        $"Volume on {host.Id} still empty after {FormatMb(bytes)} transfer; " +
                        "check remote disk space and busybox logs.");

                InfrastructureEventLog.Emit("game_data_transfer_completed", new
                {
                    host_id = host.Id,
                    bytesSent = bytes,
                    elapsedMs = sw.ElapsedMilliseconds
                });
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[GameData] {host.Id}: {FormatMb(bytes)} sent in {sw.Elapsed.TotalSeconds:F1}s"));
                renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
                    SetupStepStatus.Completed,
                    FormattableString.Invariant($"{FormatMb(bytes)} in {sw.Elapsed.TotalSeconds:F1}s")));
                return new TransferResult(host.Id, true, Skipped: false, Error: null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                InfrastructureEventLog.Emit("game_data_transfer_failed",
                    new { host_id = host.Id, attempt, error = ex.Message });
                if (attempt < _maxRetries)
                {
                    var delay = delays[Math.Min(attempt, delays.Length - 1)];
                    renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
                        SetupStepStatus.InProgress,
                        FormattableString.Invariant($"attempt {attempt} failed ({ex.Message}); retrying in {delay.TotalSeconds:F0}s")));
                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { throw; }
                }
                else
                {
                    renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, host.Id,
                        SetupStepStatus.Failed, ex.Message));
                }
            }
        }
        return new TransferResult(host.Id, Success: false, Skipped: false, Error: last?.Message ?? "unknown");
    }

    /// <summary>
    /// Checks whether the volume on the given daemon contains the canonical
    /// <c>StardewValley</c> executable. Auto-creates the volume if missing
    /// (a missing volume is just an empty volume for our purposes), and runs
    /// <c>test -e</c> in a short-lived container with the volume bind-mounted
    /// read-only. The helper image must already be cached on the daemon — the
    /// caller is responsible for pulling it; if it isn't, the probe-container
    /// create surfaces <c>DockerApiException(NotFound)</c>.
    /// </summary>
    private async Task<bool> IsVolumePopulatedAsync(DockerClient client, CancellationToken ct)
    {
        await EnsureVolumeExistsAsync(client, ct);

        var probe = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = HelperImage,
            Cmd = new List<string> { "test", "-e", ProbePath },
            HostConfig = new HostConfig
            {
                Binds = new List<string> { $"{_gameDataVolumeName}:{MountPath}:ro" },
            },
        }, ct);
        try
        {
            await client.Containers.StartContainerAsync(probe.ID, new ContainerStartParameters(), ct);
            var wait = await client.Containers.WaitContainerAsync(probe.ID, ct);
            return wait.StatusCode == 0;
        }
        finally
        {
            try { await client.Containers.RemoveContainerAsync(probe.ID, new ContainerRemoveParameters { Force = true }, ct); }
            catch { /* best-effort cleanup */ }
        }
    }

    private async Task EnsureVolumeExistsAsync(DockerClient client, CancellationToken ct)
    {
        try
        {
            await client.Volumes.InspectAsync(_gameDataVolumeName, ct);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = _gameDataVolumeName,
            }, ct);
        }
    }

    private static async Task EnsureHelperImageAsync(DockerClient client, string hostId, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(HelperImage, ct);
            return;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Pull below.
        }

        InfrastructureEventLog.Emit("helper_image_pull_started", new { host_id = hostId, image = HelperImage });

        var parts = HelperImage.Split(':');
        var pullProgress = new PullProgress();
        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = parts[0], Tag = parts.Length > 1 ? parts[1] : "latest" },
                authConfig: null,
                progress: pullProgress,
                cancellationToken: ct);
            pullProgress.ThrowIfFailed(hostId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            InfrastructureEventLog.Emit("helper_image_pull_failed",
                new { host_id = hostId, image = HelperImage, error = ex.Message });
            throw;
        }

        InfrastructureEventLog.Emit("helper_image_pull_completed", new { host_id = hostId, image = HelperImage });
    }

    /// <summary>
    /// <see cref="IProgress{JSONMessage}"/> that captures the first daemon-error
    /// message during a pull. <see cref="DockerClient.Images"/>.<c>CreateImageAsync</c>
    /// reports daemon errors via the progress channel and does NOT throw — without
    /// this, a failed pull surfaces only as a confusing downstream "image not found"
    /// when the probe container is created.
    /// </summary>
    private sealed class PullProgress : IProgress<JSONMessage>
    {
        private readonly object _lock = new();
        private string? _firstError;

        public void Report(JSONMessage value)
        {
            lock (_lock)
            {
                if (_firstError != null) return;
                var msg = value.Error?.Message;
                if (!string.IsNullOrEmpty(msg)) _firstError = msg;
            }
        }

        public void ThrowIfFailed(string hostId)
        {
            string? err;
            lock (_lock) { err = _firstError; }
            if (err != null)
                throw new InvalidOperationException($"Docker daemon pull error on {hostId}: {err}");
        }
    }

    /// <summary>
    /// Streams the local volume's contents into the remote volume in one pass.
    /// A short-lived helper container on each side holds the volume mount; the
    /// daemon's <c>GetArchiveFromContainer</c> / <c>ExtractArchiveToContainer</c>
    /// endpoints stream tar bytes between them with no buffering on our side.
    /// </summary>
    private async Task<long> TransferAsync(DockerHost host, int attempt, ITestRenderer? renderer, CancellationToken ct)
    {
        var srcContainer = await _localClient.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = HelperImage,
            Cmd = new List<string> { "true" },
            HostConfig = new HostConfig
            {
                Binds = new List<string> { $"{_gameDataVolumeName}:{MountPath}:ro" },
            },
        }, ct);
        var dstContainer = await host.ApiClient.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = HelperImage,
            Cmd = new List<string> { "true" },
            HostConfig = new HostConfig
            {
                Binds = new List<string> { $"{_gameDataVolumeName}:{MountPath}" },
            },
        }, ct);

        try
        {
            // GetArchiveFromContainer returns the contents of `path` as a tar
            // stream. Path must be a directory inside the (running-or-stopped)
            // container; the daemon walks the bind mount transparently.
            var archive = await _localClient.Containers.GetArchiveFromContainerAsync(
                srcContainer.ID,
                new ContainerPathStatParameters { Path = MountPath },
                statOnly: false,
                ct);

            var counted = new ByteCountingStream(archive.Stream
                ?? throw new InvalidOperationException(
                    $"GetArchiveFromContainer returned no stream for {srcContainer.ID}:{MountPath}."));
            var startedAt = Stopwatch.StartNew();
            var progress = new TransferProgress(host.Id, attempt, startedAt, counted, renderer);

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task pollTask;
            using (ExecutionContext.SuppressFlow())
            {
                pollTask = Task.Run(() => PollLoop(progress, pollCts.Token), CancellationToken.None);
            }
            try
            {
                // ExtractArchiveToContainer untars into `path`. The tar produced
                // by GetArchive is rooted at the *directory entry* (`data/...`);
                // extracting at `/` on the destination places contents at
                // `/data/...` to match. This mirrors `docker cp` semantics on
                // both ends.
                await host.ApiClient.Containers.ExtractArchiveToContainerAsync(
                    dstContainer.ID,
                    new CopyToContainerParameters { Path = "/" },
                    counted,
                    ct);
            }
            finally
            {
                pollCts.Cancel();
                try { await pollTask; } catch { /* expected on cancel */ }
                try { await archive.Stream.DisposeAsync(); } catch { }
            }
            return counted.BytesRead;
        }
        finally
        {
            try { await _localClient.Containers.RemoveContainerAsync(srcContainer.ID, new ContainerRemoveParameters { Force = true }, ct); } catch { }
            try { await host.ApiClient.Containers.RemoveContainerAsync(dstContainer.ID, new ContainerRemoveParameters { Force = true }, ct); } catch { }
        }
    }

    private static async Task PollLoop(TransferProgress progress, CancellationToken ct)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await ticker.WaitForNextTickAsync(ct))
            progress.PollByteProgress();
    }

    // Invariant culture — these strings land in CI logs and the WebUI; a locale's
    // "," decimal would mangle them per-runner.
    private static string FormatMb(long bytes) =>
        FormattableString.Invariant($"{bytes / 1024.0 / 1024.0:F1} MB");

    /// <summary>
    /// Read-only stream decorator that increments a byte counter on every read.
    /// Same shape as <see cref="ImageDistributor"/>'s counted stream, mirrored
    /// here so each distributor can evolve independently.
    /// </summary>
    private sealed class ByteCountingStream : Stream
    {
        private readonly Stream _inner;
        private long _bytesRead;

        public ByteCountingStream(Stream inner) { _inner = inner; }

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0) Interlocked.Add(ref _bytesRead, n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken);
            if (n > 0) Interlocked.Add(ref _bytesRead, n);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Throttled progress emitter. Periodic byte poll (1s) + InProgress renderer
    /// step + structured infra event. Mirrors <see cref="ImageDistributor"/>'s
    /// emitter; the two intentionally don't share code so each can evolve its
    /// throttle policy independently.
    /// </summary>
    private sealed class TransferProgress
    {
        private const long EmitBytesThreshold = 5L * 1024 * 1024;
        private static readonly TimeSpan StatusEmitThreshold = TimeSpan.FromSeconds(1);

        private readonly string _hostId;
        private readonly int _attempt;
        private readonly Stopwatch _startedAt;
        private readonly ByteCountingStream _counted;
        private readonly ITestRenderer? _renderer;
        private readonly Stopwatch _lastEmit = Stopwatch.StartNew();
        private readonly object _lock = new();
        private long _lastEmitBytes;

        public TransferProgress(string hostId, int attempt, Stopwatch startedAt,
            ByteCountingStream counted, ITestRenderer? renderer)
        {
            _hostId = hostId;
            _attempt = attempt;
            _startedAt = startedAt;
            _counted = counted;
            _renderer = renderer;
        }

        public void PollByteProgress()
        {
            lock (_lock)
            {
                var bytesSent = _counted.BytesRead;
                var bytesAdvanced = bytesSent - _lastEmitBytes >= EmitBytesThreshold;
                var heartbeat = _lastEmit.Elapsed >= StatusEmitThreshold;
                if (!bytesAdvanced && !heartbeat) return;

                var rateMbPerSec = _startedAt.Elapsed.TotalSeconds > 0
                    ? bytesSent / 1024.0 / 1024.0 / _startedAt.Elapsed.TotalSeconds
                    : 0;
                InfrastructureEventLog.Emit("game_data_transfer_progress", new
                {
                    host_id = _hostId,
                    attempt = _attempt,
                    bytesSent,
                    elapsedMs = _startedAt.ElapsedMilliseconds,
                });
                var detail = FormattableString.Invariant(
                    $"transferring game files {FormatMb(bytesSent)} ({rateMbPerSec:F1} MB/s, {_startedAt.Elapsed.TotalSeconds:F0}s)");
                Console.Error.WriteLine($"[GameData] {_hostId}: {detail}");
                _renderer?.OnSetupStep(new SetupStepEvent(SetupCategory, _hostId,
                    SetupStepStatus.InProgress, detail));
                _lastEmitBytes = bytesSent;
                _lastEmit.Restart();
            }
        }
    }
}
