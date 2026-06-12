using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using JunimoServer.TestRunner.Rendering;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.TestRunner.Distribution;

/// <summary>
/// Builds Docker images once on the coordinator and transfers them to remote
/// hosts via Docker.DotNet over the per-host <c>ssh -N -L</c> daemon-socket
/// tunnel that <see cref="TunnelManager"/> opens during preflight. Skips hosts
/// whose Docker daemon already holds every image with matching layer digests.
/// Local hosts are no-ops — the image is already on the same daemon.
///
/// <para>Concurrency-bounded across hosts: at most
/// <see cref="MaxConcurrentTransfers"/> transfers run at once.</para>
///
/// <para>The transfer pipeline is constant-memory: a non-seekable
/// <see cref="Stream"/> from the local daemon's <c>/images/get</c> endpoint
/// flows directly into the remote daemon's <c>/images/load</c> endpoint via
/// chunked HTTP upload — no temp file, no in-memory buffer.</para>
/// </summary>
public sealed class ImageDistributor : IDisposable
{
    // Streaming-only — local SaveImagesAsync → chunked HTTP upload → remote
    // LoadImageAsync. CPU-light per host; throttled mainly by network. 3 keeps
    // the SSH tunnel pool from saturating on typical 3–5 host fleets. The
    // asymmetry with GameDataDistributor (2) is deliberate: that path spawns
    // helper containers on both sides plus a tar pipeline and is heavier
    // per-host on each daemon.
    private const int MaxConcurrentTransfers = 3;

    // Setup-event identifiers — must match the phase name Program.cs opens
    // around DistributeAsync ("Image distribution") so per-host steps appear
    // under that phase in the WebUI tree.
    private const string SetupCategory = "Runner";
    private const string SetupPhase = "Image distribution";

    private readonly string _imageTag;
    private readonly string[] _imageNames;
    private readonly int _maxRetries;
    private readonly DockerClient _localClient;

    public ImageDistributor(string imageTag, string[] imageNames)
    {
        _imageTag = imageTag;
        _imageNames = imageNames;
        var raw = Environment.GetEnvironmentVariable("SDVD_IMAGE_TRANSFER_RETRIES");
        _maxRetries = int.TryParse(raw, out var r) && r >= 0 ? r : 1;
        _localClient = DockerEndpointConfig.Instance.CreateDockerClient();
    }

    public void Dispose() => _localClient.Dispose();

    public sealed record TransferResult(string HostId, bool Success, string? Error);

    /// <summary>
    /// Transfers images to all remote hosts in <paramref name="hosts"/>; skips
    /// any whose layer digests all match the coordinator's. Local hosts are no-ops.
    ///
    /// <para>When <paramref name="renderer"/> is non-null, per-host progress
    /// surfaces in the WebUI / CLI tree as setup-step events under the
    /// "Image distribution" phase that <c>Program.cs</c> opens around this
    /// call. The renderer parameter is optional so the distributor stays
    /// usable in contexts that don't have a renderer (e.g. unit tests).</para>
    /// </summary>
    public async Task<List<TransferResult>> DistributeAsync(
        IReadOnlyList<DockerHost> hosts,
        ITestRenderer? renderer = null,
        CancellationToken ct = default
    )
    {
        // Resolve local layer digests once. Layer digests are OCI
        // content-addressed and stable across daemon versions, unlike
        // ImageInspectResponse.ID which the daemon recomputes locally and
        // can differ between e.g. 29.1.5 and 29.2.1 for byte-identical
        // source content. Comparing layer lists therefore correctly skips
        // a re-upload after a no-op rebuild on a cross-version host pair.
        // Throws DockerApiException(404) if any image isn't built locally;
        // Program.cs's image-distribution catch emits run_aborted with
        // cause: "image_transfer_exception".
        var localLayers = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var name in _imageNames)
        {
            var fullName = $"{name}:{_imageTag}";
            var inspect = await _localClient.Images.InspectImageAsync(fullName, ct);
            localLayers[fullName] = ExtractLayers(inspect);
        }

        var baseline = ReadBaseline();

        // Bucket hosts: skip those whose every image's layer list matches
        // the coordinator's; otherwise queue for transfer.
        var transferNeeded = new List<DockerHost>();
        foreach (var h in hosts)
        {
            if (string.IsNullOrEmpty(h.SshDestination))
            {
                continue; // local host — no-op
            }

            var allMatch = true;
            foreach (var name in _imageNames)
            {
                var fullName = $"{name}:{_imageTag}";
                try
                {
                    var remote = await h.ApiClient.Images.InspectImageAsync(fullName, ct);
                    if (!LayersEqual(localLayers[fullName], ExtractLayers(remote)))
                    {
                        allMatch = false;
                        break;
                    }
                }
                catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch)
            {
                InfrastructureEventLog.Emit("image_skip_match", new { host_id = h.Id });
                var savedSuffix = baseline.TryGetValue(h.Id, out var saved)
                    ? $" (~{FormatMb(saved)} skipped)"
                    : "";
                renderer?.OnSetupStep(
                    new SetupStepEvent(
                        SetupCategory,
                        h.Id,
                        SetupStepStatus.Completed,
                        $"already up-to-date{savedSuffix}"
                    )
                );
                continue;
            }
            transferNeeded.Add(h);
        }

        if (transferNeeded.Count > 0)
        {
            Console.Error.WriteLine(
                $"[ImageTransfer] distributing {_imageNames.Length} image(s) to {transferNeeded.Count} host(s)"
            );
        }

        // Bounded-concurrency parallel transfer.
        using var gate = new SemaphoreSlim(MaxConcurrentTransfers, MaxConcurrentTransfers);
        var tasks = transferNeeded
            .Select(async h =>
            {
                await gate.WaitAsync(ct);
                var expectedBytes = baseline.TryGetValue(h.Id, out var b) ? b : 0L;
                try
                {
                    return await TransferWithRetryAsync(h, expectedBytes, renderer, ct);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var skipped = hosts
            .Where(h => !string.IsNullOrEmpty(h.SshDestination) && !transferNeeded.Contains(h))
            .Select(h => new TransferResult(h.Id, true, null));
        return skipped.Concat(results).ToList();
    }

    /// <summary>
    /// Returns the image's OCI layer digest list, or an empty list if the
    /// daemon reports an unexpected RootFS shape (defensive — every image
    /// produced by <c>docker build</c> has <c>Type=&quot;layers&quot;</c>).
    /// An empty list never matches another empty list under
    /// <see cref="LayersEqual"/>, so a malformed response forces a re-upload
    /// rather than silently skipping.
    /// </summary>
    private static IReadOnlyList<string> ExtractLayers(ImageInspectResponse inspect)
    {
        var layers = inspect.RootFS?.Layers;
        return layers is { Count: > 0 }
            ? (IReadOnlyList<string>)layers.ToArray()
            : Array.Empty<string>();
    }

    private static bool LayersEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return false;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<TransferResult> TransferWithRetryAsync(
        DockerHost host,
        long expectedBytes,
        ITestRenderer? renderer,
        CancellationToken ct
    )
    {
        var delays = new[]
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(60),
        };
        Exception? last = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                InfrastructureEventLog.Emit(
                    "image_transfer_started",
                    new { host_id = host.Id, attempt }
                );
                renderer?.OnSetupStep(
                    new SetupStepEvent(
                        SetupCategory,
                        host.Id,
                        SetupStepStatus.Started,
                        attempt == 0
                            ? "transferring images"
                            : $"retry {attempt}: transferring images"
                    )
                );
                var sw = Stopwatch.StartNew();
                var bytesSent = await TransferAsync(host, attempt, expectedBytes, renderer, ct);
                sw.Stop();

                // Post-load: verify every image is present on the remote.
                // Layer-digest equality is enforced on the *next* run's
                // pre-transfer skip check; here we only need to confirm that
                // load actually populated the named tag, since a silent load
                // failure would otherwise surface as a confusing image-pull
                // error during container creation.
                renderer?.OnSetupStep(
                    new SetupStepEvent(
                        SetupCategory,
                        host.Id,
                        SetupStepStatus.InProgress,
                        "verifying images on remote"
                    )
                );
                foreach (var name in _imageNames)
                {
                    var fullName = $"{name}:{_imageTag}";
                    try
                    {
                        await host.ApiClient.Images.InspectImageAsync(fullName, ct);
                    }
                    catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        throw new InvalidOperationException(
                            $"Image {fullName} missing on {host.Id} after load.",
                            ex
                        );
                    }
                }

                WriteBaseline(host.Id, bytesSent);

                InfrastructureEventLog.Emit(
                    "image_transfer_completed",
                    new
                    {
                        host_id = host.Id,
                        attempt,
                        bytesSent,
                        elapsedMs = sw.ElapsedMilliseconds,
                    }
                );
                var avgRate =
                    sw.Elapsed.TotalSeconds > 0
                        ? bytesSent / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds
                        : 0;
                Console.Error.WriteLine(
                    FormattableString.Invariant(
                        $"[ImageTransfer] {host.Id}: {FormatMb(bytesSent)} sent in {sw.Elapsed.TotalSeconds:F1}s ({avgRate:F1} MB/s)"
                    )
                );
                renderer?.OnSetupStep(
                    new SetupStepEvent(
                        SetupCategory,
                        host.Id,
                        SetupStepStatus.Completed,
                        FormattableString.Invariant(
                            $"{FormatMb(bytesSent)} in {sw.Elapsed.TotalSeconds:F1}s"
                        )
                    )
                );
                return new TransferResult(host.Id, true, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                InfrastructureEventLog.Emit(
                    "image_transfer_failed",
                    new
                    {
                        host_id = host.Id,
                        attempt,
                        error = ex.Message,
                    }
                );
                if (attempt < _maxRetries)
                {
                    var delay = delays[Math.Min(attempt, delays.Length - 1)];
                    renderer?.OnSetupStep(
                        new SetupStepEvent(
                            SetupCategory,
                            host.Id,
                            SetupStepStatus.InProgress,
                            FormattableString.Invariant(
                                $"attempt {attempt} failed ({ex.Message}); retrying in {delay.TotalSeconds:F0}s"
                            )
                        )
                    );
                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
                else
                {
                    renderer?.OnSetupStep(
                        new SetupStepEvent(
                            SetupCategory,
                            host.Id,
                            SetupStepStatus.Failed,
                            ex.Message
                        )
                    );
                }
            }
        }
        return new TransferResult(host.Id, false, last?.Message ?? "unknown");
    }

    private async Task<long> TransferAsync(
        DockerHost host,
        int attempt,
        long expectedBytes,
        ITestRenderer? renderer,
        CancellationToken ct
    )
    {
        var imageList = _imageNames.Select(n => $"{n}:{_imageTag}").ToArray();
        var startedAt = Stopwatch.StartNew();

        // Stream local /images/get?names=… → remote /images/load. The local
        // daemon's response stream is consumed lazily and uploaded to the
        // remote daemon as chunked HTTP — no temp file, no in-memory buffer.
        await using var tar = await _localClient.Images.SaveImagesAsync(imageList, ct);
        var counted = new ByteCountingStream(tar);
        var progress = new TransferProgress(
            host.Id,
            attempt,
            expectedBytes,
            startedAt,
            counted,
            renderer
        );

        // Docker.DotNet's IProgress<JSONMessage> only fires on the daemon's
        // *response* body — silent during the chunked HTTP upload. Run a
        // background timer that polls BytesRead so the UI / log / stderr see
        // live progress every ~1s while the upload is in flight.
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task pollTask;
        using (ExecutionContext.SuppressFlow())
        {
            pollTask = Task.Run(() => PollLoop(progress, pollCts.Token));
        }
        try
        {
            await host.ApiClient.Images.LoadImageAsync(
                new ImageLoadParameters { Quiet = false },
                counted,
                progress,
                ct
            );

            // MonitorStreamForMessagesAsync reports daemon errors as IProgress
            // messages but does NOT throw — re-raise here so a load failure
            // (e.g. "no space left on device") doesn't masquerade as a missing
            // image at the post-load verify step.
            progress.ThrowIfFailed();
        }
        finally
        {
            pollCts.Cancel();
            try
            {
                await pollTask;
            }
            catch
            { /* OCE from cancel; emit failures are diagnostic-only */
            }
        }

        return counted.BytesRead;
    }

    private static async Task PollLoop(TransferProgress progress, CancellationToken ct)
    {
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await ticker.WaitForNextTickAsync(ct))
        {
            progress.PollByteProgress();
        }
    }

    // Invariant culture — these strings land in CI logs and the WebUI; a locale's
    // "," decimal would mangle them per-runner.
    private static string FormatMb(long bytes) =>
        FormattableString.Invariant($"{bytes / 1024.0 / 1024.0:F1} MB");

    /// <summary>Compact duration for ETA display: "45s", "2m18s", "1h03m".</summary>
    private static string FormatEta(double seconds)
    {
        if (seconds < 0 || double.IsInfinity(seconds) || double.IsNaN(seconds))
        {
            return "?";
        }

        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1)
        {
            return FormattableString.Invariant($"{(int)t.TotalHours}h{t.Minutes:D2}m");
        }

        if (t.TotalMinutes >= 1)
        {
            return FormattableString.Invariant($"{t.Minutes}m{t.Seconds:D2}s");
        }

        return FormattableString.Invariant($"{t.Seconds}s");
    }

    // {hostId → last transfer bytes}, persisted across runs. Image metadata can't
    // predict the deduped+compressed save-tar size, so we estimate from the prior
    // run. Display only; never gates correctness.
    private static readonly object BaselineLock = new();

    private static string BaselinePath =>
        Path.Combine(TestArtifacts.OutputDir, RunArtifactNames.ImageTransferBaseline);

    private static Dictionary<string, long> ReadBaseline()
    {
        lock (BaselineLock)
        {
            try
            {
                if (!File.Exists(BaselinePath))
                {
                    return new Dictionary<string, long>();
                }

                return JsonSerializer.Deserialize<Dictionary<string, long>>(
                        File.ReadAllText(BaselinePath)
                    ) ?? new Dictionary<string, long>();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ImageTransfer] baseline read failed: {ex.Message}");
                return new Dictionary<string, long>();
            }
        }
    }

    private static void WriteBaseline(string hostId, long bytes)
    {
        lock (BaselineLock)
        {
            try
            {
                var map = ReadBaselineUnlocked();
                map[hostId] = bytes;
                Directory.CreateDirectory(TestArtifacts.OutputDir);
                File.WriteAllText(BaselinePath, JsonSerializer.Serialize(map));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ImageTransfer] baseline write failed: {ex.Message}");
            }
        }
    }

    // Caller must already hold BaselineLock (it is non-recursive).
    private static Dictionary<string, long> ReadBaselineUnlocked()
    {
        if (!File.Exists(BaselinePath))
        {
            return new Dictionary<string, long>();
        }

        return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(BaselinePath))
            ?? new Dictionary<string, long>();
    }

    /// <summary>
    /// Read-only <see cref="Stream"/> decorator that increments a counter on
    /// every read. Drives byte-based progress reporting; not disposable
    /// (the underlying stream's <c>await using</c> owns lifetime).
    /// </summary>
    private sealed class ByteCountingStream : Stream
    {
        private readonly Stream _inner;
        private long _bytesRead;

        public ByteCountingStream(Stream inner)
        {
            _inner = inner;
        }

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

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0)
            {
                Interlocked.Add(ref _bytesRead, n);
            }

            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken);
            if (n > 0)
            {
                Interlocked.Add(ref _bytesRead, n);
            }

            return n;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// <see cref="IProgress{JSONMessage}"/> that captures daemon errors,
    /// tracks the daemon's most recent status line, and emits throttled
    /// progress events to <see cref="InfrastructureEventLog"/>, stderr, and
    /// (when non-null) the renderer. Docker.DotNet's <c>StreamUtil.MonitorStreamForMessagesAsync</c>
    /// does NOT throw on daemon errors — we stash the first error and
    /// re-raise via <see cref="ThrowIfFailed"/>.
    /// </summary>
    private sealed class TransferProgress : IProgress<JSONMessage>
    {
        private const long EmitBytesThreshold = 5L * 1024 * 1024;
        private static readonly TimeSpan StatusEmitThreshold = TimeSpan.FromSeconds(1);

        private readonly string _hostId;
        private readonly int _attempt;
        private readonly long _expectedBytes; // 0 = no baseline yet (first run)
        private readonly Stopwatch _startedAt;
        private readonly ByteCountingStream _counted;
        private readonly ITestRenderer? _renderer;
        private readonly Stopwatch _lastEmit = Stopwatch.StartNew();
        private readonly object _lock = new();
        private long _lastEmitBytes;
        private string? _lastStatus;
        private string? _lastEmittedStatus;
        private string? _firstError;

        public TransferProgress(
            string hostId,
            int attempt,
            long expectedBytes,
            Stopwatch startedAt,
            ByteCountingStream counted,
            ITestRenderer? renderer
        )
        {
            _hostId = hostId;
            _attempt = attempt;
            _expectedBytes = expectedBytes;
            _startedAt = startedAt;
            _counted = counted;
            _renderer = renderer;
        }

        public void Report(JSONMessage value)
        {
            if (_firstError == null)
            {
                var msg = value.Error?.Message;
                if (!string.IsNullOrEmpty(msg))
                {
                    _firstError = msg;
                }
            }

            // `Stream` carries human-readable lines like "Loaded image: foo:bar";
            // `Status` carries per-layer phases like "Loading layer".
            var status = TrimStatus(value.Stream) ?? TrimStatus(value.Status);
            MaybeEmit(status);
        }

        /// <summary>
        /// Driven by the periodic timer in <see cref="ImageDistributor.PollLoop"/>.
        /// Provides byte-progress visibility during the chunked HTTP upload,
        /// when the daemon's response body — and therefore <see cref="Report"/>
        /// — is silent.
        /// </summary>
        public void PollByteProgress() => MaybeEmit(daemonStatus: null);

        private void MaybeEmit(string? daemonStatus)
        {
            lock (_lock)
            {
                if (daemonStatus != null)
                {
                    _lastStatus = daemonStatus;
                }

                var bytesSent = _counted.BytesRead;
                var bytesAdvanced = bytesSent - _lastEmitBytes >= EmitBytesThreshold;
                var statusChanged =
                    _lastStatus != _lastEmittedStatus && _lastEmit.Elapsed >= StatusEmitThreshold;
                if (!bytesAdvanced && !statusChanged)
                {
                    return;
                }

                var elapsedSec = _startedAt.Elapsed.TotalSeconds;
                var rateMbPerSec = elapsedSec > 0 ? bytesSent / 1024.0 / 1024.0 / elapsedSec : 0;

                // Clamp so an overrun of the estimate caps at the bar's 100%.
                int? pct =
                    _expectedBytes > 0
                        ? (int)Math.Min(100, bytesSent * 100.0 / _expectedBytes)
                        : null;

                InfrastructureEventLog.Emit(
                    "image_transfer_progress",
                    new
                    {
                        host_id = _hostId,
                        attempt = _attempt,
                        bytesSent,
                        expectedBytes = _expectedBytes,
                        pct,
                        elapsedMs = _startedAt.ElapsedMilliseconds,
                    }
                );

                var phase = bytesAdvanced ? "uploading" : "remote";
                var sizePart =
                    _expectedBytes > 0
                        ? $"{FormatMb(bytesSent)} / ~{FormatMb(_expectedBytes)}"
                        : FormatMb(bytesSent);
                var pctPart = pct is int p
                    ? (bytesSent >= _expectedBytes ? ">99%, " : $"{p}%, ")
                    : "";
                var etaPart =
                    _expectedBytes > bytesSent && rateMbPerSec > 0
                        ? $", ETA {FormatEta((_expectedBytes - bytesSent) / 1024.0 / 1024.0 / rateMbPerSec)}"
                        : "";
                var statusSuffix = _lastStatus != null ? $" — {_lastStatus}" : "";
                var detail = FormattableString.Invariant(
                    $"{phase} {sizePart} ({pctPart}{rateMbPerSec:F1} MB/s, {elapsedSec:F0}s{etaPart}){statusSuffix}"
                );
                Console.Error.WriteLine($"[ImageTransfer] {_hostId}: {detail}");
                _renderer?.OnSetupStep(
                    new SetupStepEvent(SetupCategory, _hostId, SetupStepStatus.InProgress, detail)
                );
                _lastEmitBytes = bytesSent;
                _lastEmittedStatus = _lastStatus;
                _lastEmit.Restart();
            }
        }

        public void ThrowIfFailed()
        {
            if (_firstError != null)
            {
                throw new InvalidOperationException(
                    $"Docker daemon load error on {_hostId}: {_firstError}"
                );
            }
        }

        private static string? TrimStatus(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return null;
            }

            var trimmed = s.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
