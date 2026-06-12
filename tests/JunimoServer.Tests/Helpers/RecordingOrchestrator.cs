using System.Collections.Concurrent;
using System.Diagnostics;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Coordinates per-test video clip extraction across containers.
///
/// During tests: <see cref="MarkContainerUsedAsync"/> records which containers a test
/// touched and the host-monotonic moment of each touch. At test cleanup
/// (<see cref="FinalizeAsync"/>): in parallel, asks each container's
/// <see cref="ContainerRecorder"/> to extract a clip covering [mark, end_of_test], then
/// emits structured events for the test UI.
///
/// <para>
/// <b>Cross-clip alignment:</b> two-phase finalize. Phase 1 runs all extractions and
/// collects each clip's <c>actualFirstFramePts</c> (the seek-landing frame's absolute
/// Unix-epoch wall-clock, returned by the recorder). Phase 2 picks
/// <c>alignmentBase = min(actualFirstFramePts)</c> and emits each clip with
/// <c>timelineOffsetSec = actualFirstFramePts − alignmentBase</c>, so the UI scrubs
/// clips by content-aligned wall-clock rather than the requested mark (which can be
/// snap-quantized by ffmpeg's <c>-ss</c>). Falls back to mark-based offsets when a clip
/// doesn't report <c>actualFirstFramePts</c>.
/// </para>
///
/// <para>
/// Parallel extraction is safe: each container has its own segment files; each
/// extraction uses Guid-based temp file IDs; finalized segments are read-only.
/// </para>
///
/// <para>
/// The orchestrator carries no <c>TestContext.Current</c>-bound state, so it can be
/// invoked from a deferred background task after the originating test's
/// <c>DisposeAsync</c> has completed.
/// </para>
/// </summary>
internal sealed class RecordingOrchestrator
{
    // ── Static state (shared across all tests) ──

    private static readonly ConcurrentDictionary<string, ContainerRecorder> _recorders = new();

    public static void RegisterRecorder(string containerId, ContainerRecorder recorder) =>
        _recorders[containerId] = recorder;

    public static void UnregisterRecorder(string containerId) =>
        _recorders.TryRemove(containerId, out _);

    public static ContainerRecorder? GetRecorder(string containerId)
    {
        _recorders.TryGetValue(containerId, out var recorder);
        return recorder;
    }

    // ── Per-test instance state ──

    /// <param name="MarkTimestamp">Absolute host monotonic ticks at mark time (for timeline positioning and elapsed computation).</param>
    /// <param name="MarkRunMs">Run-relative milliseconds at mark time (diagnostic — maps the absolute Stopwatch tick to the runMs scale used by every other event so clip windows can be reconciled against test_started/test_completed without external clock conversion).</param>
    private record ContainerUsage(
        string ContainerId,
        string Label,
        long MarkTimestamp,
        long MarkRunMs
    );

    private readonly List<ContainerUsage> _usages = new();

    /// <summary>
    /// Records that this test is using a container, starting now. Captures the host
    /// monotonic timestamp synchronously; <see cref="FinalizeAsync"/> translates it to
    /// container Unix-epoch via <see cref="ContainerRecorder.ContainerEpochAtHostTicks"/>.
    /// Safe to call before the recorder reaches Recording state — translation tolerates
    /// that and uses the eventual offset once available.
    /// </summary>
    public Task MarkContainerUsedAsync(
        string containerId,
        string label,
        CancellationToken ct = default
    )
    {
        if (!RecordingPolicy.IsEnabled)
            return Task.CompletedTask;
        _usages.Add(
            new ContainerUsage(containerId, label, Stopwatch.GetTimestamp(), RunMetadata.GetRunMs())
        );
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the two-phase finalize (see class doc): extracts per-test clips in parallel
    /// across containers, computes content-aligned <c>timelineOffsetSec</c> from each
    /// clip's <c>actualFirstFramePts</c>, then emits the structured events for the test UI.
    /// Called per test from the test-lifecycle dispose path (including for KeepConnected).
    /// </summary>
    public async Task FinalizeAsync(
        string testClass,
        string testMethod,
        string displayName,
        bool testFailed,
        CancellationToken ct = default
    )
    {
        if (!RecordingPolicy.IsEnabled || _usages.Count == 0)
            return;

        // Deduplicate: when the same container is marked multiple times with the same label
        // prefix (e.g., BreakSession returns client to pool, test re-leases the SAME container),
        // keep only one usage per container+label with the earliest mark timestamp.
        var deduped = _usages
            .GroupBy(u =>
                (u.ContainerId, LabelPrefix: u.Label.StartsWith("server") ? "server" : "client")
            )
            .Select(g => g.OrderBy(u => u.MarkTimestamp).First())
            .ToList();

        // Failure mode + passing test: nothing to keep. Skip the entire ffmpeg pipeline
        // instead of extracting and deleting post-hoc. Screenshots (independent retention,
        // captured earlier by TestArtifactCollector) still cover failure-visibility.
        if (RecordingPolicy.Mode == TestRecordingMode.Failure && !testFailed)
        {
            foreach (var usage in deduped)
            {
                InfrastructureEventLog.Emit(
                    "recording_per_test_clip_skipped",
                    new { container = usage.Label, reason = "retention_passed" }
                );
                // usage.Label is "server" or "client" (un-indexed) at this point —
                // indexing only happens later in the per-clip loop. The un-indexed
                // "client" applies to ALL client cards in the UI for this test.
                SetupEventBus.EmitRecordingSkipped(
                    testClass,
                    testClass,
                    displayName,
                    usage.Label,
                    RecordingSkipReason.RetentionPassed
                );
            }
            return;
        }

        var testDir = TestArtifacts.GetTestDir(testClass, testMethod);

        // Timeline reference: earliest absolute timestamp across all containers.
        var minMarkTs = deduped.Min(u => u.MarkTimestamp);

        // Snapshot end time upfront so all clips share the same reference point. Per-recorder
        // endEpochs are container Unix-epoch seconds, the same coordinate system as the
        // segments' per-frame PTS.
        var endAbsoluteTs = Stopwatch.GetTimestamp();
        var endRunMs = RunMetadata.GetRunMs();
        var endEpochs = new Dictionary<string, double>();
        foreach (var usage in deduped)
        {
            var recorder = GetRecorder(usage.ContainerId);
            if (recorder is { IsRecording: true })
            {
                endEpochs[usage.ContainerId] = recorder.ContainerEpochAtHostTicks(endAbsoluteTs);
            }
        }

        // Pre-assign client indices and output filenames BEFORE parallel extraction.
        // This must be done sequentially to avoid race conditions on the client counter.
        var clientIndex = 0;
        var extractionJobs =
            new List<(
                ContainerUsage usage,
                ContainerRecorder recorder,
                string fileName,
                string destPath,
                double startEpoch,
                double durationSec,
                double timelineOffset,
                double wallDuration,
                long endRunMs,
                double markEpoch
            )>();

        foreach (var usage in deduped)
        {
            var recorder = GetRecorder(usage.ContainerId);
            if (recorder == null)
            {
                InfrastructureEventLog.Emit(
                    "recording_per_test_clip_skipped",
                    new { container = usage.Label, reason = "recorder_missing" }
                );
                SetupEventBus.EmitRecordingSkipped(
                    testClass,
                    testClass,
                    displayName,
                    usage.Label,
                    RecordingSkipReason.RecorderMissing
                );
                continue;
            }
            if (!recorder.IsRecording)
            {
                InfrastructureEventLog.Emit(
                    "recording_per_test_clip_skipped",
                    new { container = usage.Label, reason = "recorder_never_started" }
                );
                SetupEventBus.EmitRecordingSkipped(
                    testClass,
                    testClass,
                    displayName,
                    usage.Label,
                    RecordingSkipReason.RecorderNeverStarted
                );
                continue;
            }

            if (usage.Label.StartsWith("server") && !RecordingPolicy.RecordServer)
                continue;
            if (usage.Label.StartsWith("client") && !RecordingPolicy.RecordClient)
                continue;

            // Use pre-snapshotted end time so all clips share the same reference point.
            if (!endEpochs.TryGetValue(usage.ContainerId, out var endEpoch))
            {
                InfrastructureEventLog.Emit(
                    "recording_per_test_clip_skipped",
                    new { container = usage.Label, reason = "end_time_missing" }
                );
                SetupEventBus.EmitRecordingSkipped(
                    testClass,
                    testClass,
                    displayName,
                    usage.Label,
                    RecordingSkipReason.EndTimeMissing
                );
                continue;
            }

            // Translate the host-monotonic mark to container Unix-epoch via the recorder's
            // host-shared clock offset. The recorder's segments carry per-frame PTS in the
            // same coordinate system, so this value passes through to `ffmpeg -ss` directly.
            var markEpoch = recorder.ContainerEpochAtHostTicks(usage.MarkTimestamp);
            var startEpoch = markEpoch;
            var durationSec = endEpoch - startEpoch;
            if (durationSec <= 0)
            {
                InfrastructureEventLog.Emit(
                    "recording_per_test_clip_skipped",
                    new
                    {
                        container = usage.Label,
                        reason = "zero_duration",
                        durationSec,
                    }
                );
                SetupEventBus.EmitRecordingSkipped(
                    testClass,
                    testClass,
                    displayName,
                    usage.Label,
                    RecordingSkipReason.ZeroDuration
                );
                continue;
            }

            // Wall-clock duration: absolute time from mark to end snapshot.
            // Used by the UI for clip width on the timeline.
            var wallDuration = Stopwatch
                .GetElapsedTime(usage.MarkTimestamp, endAbsoluteTs)
                .TotalSeconds;

            // Determine output filename (must be sequential for client indexing)
            string fileName;
            if (usage.Label.StartsWith("server"))
            {
                fileName = "server_recording.mp4";
            }
            else
            {
                var suffix = clientIndex == 0 ? "" : $"_{clientIndex + 1}";
                fileName = $"client{suffix}_recording.mp4";
                clientIndex++;
            }

            var destPath = Path.Combine(testDir, fileName);

            // Timeline offset: absolute time difference from earliest container mark.
            // This correctly positions clips from different containers (started at different times).
            var timelineOffset = Stopwatch
                .GetElapsedTime(minMarkTs, usage.MarkTimestamp)
                .TotalSeconds;

            extractionJobs.Add(
                (
                    usage,
                    recorder,
                    fileName,
                    destPath,
                    startEpoch,
                    durationSec,
                    timelineOffset,
                    wallDuration,
                    endRunMs,
                    markEpoch
                )
            );
        }

        // Phase 1 of the two-phase finalize (see class doc): extract all clips in parallel
        // across containers, collecting each clip's actualFirstFramePts for the alignment
        // base computation below.
        var extractionOutputs = await Task.WhenAll(
            extractionJobs.Select(async job =>
            {
                var encodedWith = job.recorder.EncoderName;
                var source = job.fileName.StartsWith("server")
                    ? "server"
                    : Path.GetFileNameWithoutExtension(job.fileName).Replace("_recording", "");
                try
                {
                    var sw = Stopwatch.StartNew();
                    var result = await job.recorder.ExtractClipFromLiveAsync(
                        job.startEpoch,
                        job.durationSec,
                        job.destPath,
                        ct
                    );
                    sw.Stop();
                    return (
                        job,
                        source,
                        encodedWith,
                        result,
                        elapsedMs: sw.ElapsedMilliseconds,
                        exception: (Exception?)null
                    );
                }
                catch (Exception ex)
                {
                    return (
                        job,
                        source,
                        encodedWith,
                        result: new ContainerRecorder.ExtractionResult(null, null),
                        elapsedMs: 0L,
                        exception: ex
                    );
                }
            })
        );

        // Compute the alignment base: the MINIMUM actual-first-frame-PTS across successful
        // extractions. Each clip's timelineOffsetSec = (its actualFirstFramePts − base).
        // Falls back to mark-based timelineOffset if no clip reported actualFirstFramePts.
        double? alignmentBaseEpoch = null;
        foreach (var (_, _, _, result, _, _) in extractionOutputs)
        {
            if (result.ActualFirstFramePts is double pts && pts > 1e9)
            {
                if (alignmentBaseEpoch == null || pts < alignmentBaseEpoch.Value)
                    alignmentBaseEpoch = pts;
            }
        }

        var tasks = extractionOutputs
            .Select(async eo =>
            {
                var job = eo.job;
                var source = eo.source;
                var encodedWith = eo.encodedWith;
                var result = eo.result;
                var elapsedMs = eo.elapsedMs;
                var exception = eo.exception;

                try
                {
                    if (exception != null)
                        throw exception;

                    if (result.HostPath != null)
                    {
                        var path = result.HostPath;
                        var sizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0;

                        // Prefer content-aligned offset (clip's actual frame-0 wall-clock minus the
                        // earliest such across all clips). Fall back to the mark-based monotonic
                        // delta when this clip didn't report actualFirstFramePts.
                        var effectiveTimelineOffset =
                            (
                                result.ActualFirstFramePts is double pts
                                && pts > 1e9
                                && alignmentBaseEpoch is double basePts
                            )
                                ? pts - basePts
                                : job.timelineOffset;

                        SetupEventBus.EmitRecording(
                            testClass,
                            testClass,
                            displayName,
                            path,
                            source,
                            effectiveTimelineOffset,
                            job.wallDuration
                        );

                        // Field semantics live in the InfrastructureEventLog event-catalog comment.
                        InfrastructureEventLog.Emit(
                            "recording_per_test_clip",
                            new
                            {
                                container = job.usage.Label,
                                containerId = job.usage.ContainerId,
                                fileName = job.fileName,
                                path,
                                source,
                                timelineOffsetSec = effectiveTimelineOffset,
                                wallDurationSec = job.wallDuration,
                                startSec = job.startEpoch,
                                durationSec = job.durationSec,
                                markRunMs = job.usage.MarkRunMs,
                                endRunMs = job.endRunMs,
                                recorderStartContainerEpoch = job.recorder.StartContainerEpoch,
                                markContainerEpoch = job.markEpoch,
                                actualFirstFramePts = result.ActualFirstFramePts,
                                seekSnapMs = result.ActualFirstFramePts is double p
                                    ? (long)Math.Round((p - job.markEpoch) * 1000)
                                    : (long?)null,
                                sizeBytes,
                                extractMs = elapsedMs,
                                retentionDeleted = false,
                                testFailed,
                                encoder = encodedWith,
                            }
                        );

                        // Success path: no annotation. The recording video itself is
                        // surfaced by the panel's recordings widget (with duration
                        // and inline playback); a textual annotation here would
                        // duplicate that and — because extraction runs deferred
                        // for passing tests in Mode=All — race against the test's
                        // Done log, producing nondeterministic ordering. The
                        // typed event above is the canonical record for tooling.
                    }
                    else
                    {
                        InfrastructureEventLog.Emit(
                            "recording_per_test_clip_failed",
                            new
                            {
                                container = job.usage.Label,
                                fileName = job.fileName,
                                source,
                                startSec = job.startEpoch,
                                durationSec = job.durationSec,
                                extractMs = elapsedMs,
                                testFailed,
                                encoder = encodedWith,
                            }
                        );
                        SetupEventBus.EmitRecordingSkipped(
                            testClass,
                            testClass,
                            displayName,
                            source,
                            RecordingSkipReason.ExtractionFailed
                        );

                        SetupEventBus.EmitTestAnnotation(
                            displayName,
                            AnnotationLevel.Error,
                            AnnotationSource.Recording,
                            $"{job.fileName} extraction returned null"
                        );
                    }
                }
                catch (Exception ex)
                {
                    InfrastructureEventLog.Emit(
                        "recording_per_test_clip_failed",
                        new
                        {
                            container = job.usage.Label,
                            fileName = job.fileName,
                            source,
                            startSec = job.startEpoch,
                            durationSec = job.durationSec,
                            exceptionType = ex.GetType().Name,
                            message = ex.Message,
                            testFailed,
                            encoder = encodedWith,
                        }
                    );
                    SetupEventBus.EmitRecordingSkipped(
                        testClass,
                        testClass,
                        displayName,
                        source,
                        RecordingSkipReason.ExtractionFailed
                    );

                    SetupEventBus.EmitTestAnnotation(
                        displayName,
                        AnnotationLevel.Error,
                        AnnotationSource.Recording,
                        $"{job.fileName} extraction threw {ex.GetType().Name}: {ex.Message}"
                    );
                }
            })
            .ToList();

        await Task.WhenAll(tasks);
    }
}
