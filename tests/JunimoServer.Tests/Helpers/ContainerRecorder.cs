using System.Diagnostics;
using System.Globalization;
using System.Linq;
using DotNet.Testcontainers.Containers;
using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Manages continuous ffmpeg recording inside a single container using the segment muxer.
/// Records the X11 display as a series of small (default 1s) self-contained MPEG-TS segments
/// written to /recordings inside the container.
///
/// Segments (rather than one monolithic file) keep clip extraction O(clip size): only the
/// segments covering the clip timespan are touched. See <see cref="SegmentFormat"/> for the
/// MPEG-TS choice and <see cref="StartContainerEpoch"/> for the wall-clock anchoring scheme.
///
/// Clips and full recordings are retrieved via Testcontainers' ReadFileAsync (Docker tar
/// API) — no bind mounts, so this works against remote Docker engines.
///
/// Error contract: no public method throws (except constructor). All failures are logged
/// via the callback and result in graceful degradation.
/// </summary>
internal sealed class ContainerRecorder : IAsyncDisposable
{
    private enum RecorderState
    {
        NotStarted,
        Recording,
        Stopped,
        Failed,
    }

    /// <summary>Parsed segment entry from segments.csv.</summary>
    private record SegmentInfo(string FileName, double StartTime, double EndTime);

    /// <summary>
    /// Result of an extraction attempt. <see cref="HostPath"/> is the local path of
    /// the extracted clip MP4 (or null on failure). <see cref="ActualFirstFramePts"/>
    /// is the absolute container Unix-epoch second of the seek-landing frame in the
    /// source recording — the orchestrator uses this (not the requested mark) as the
    /// basis for <c>timelineOffsetSec</c> so cross-clip alignment reflects actual
    /// content, not requested-but-quantized seek targets.
    /// </summary>
    public readonly record struct ExtractionResult(string? HostPath, double? ActualFirstFramePts);

    private readonly IContainer _container;
    private readonly DockerHost _host;
    private readonly string _displayLabel;
    private readonly int _fps;
    private readonly int _segmentTime;
    private readonly Action<string> _log;
    private readonly bool _useGpu;
    private readonly DockerExtractLimiter? _extractLimiter;

    /// <summary>Display name for the active video encoder (per-host).</summary>
    public string EncoderName => _useGpu ? "nvenc" : "libx264";
    private RecorderState _state = RecorderState.NotStarted;
    private volatile bool _containerDead;
    private int _containerDeadLogged; // 0 = not yet logged; flipped via Interlocked.Exchange in MarkContainerDead

    /// <summary>Container-side recording directory.</summary>
    private const string RecDir = "/recordings";

    /// <summary>
    /// Source-segment container format. MPEG-TS (rather than Matroska) because TS's
    /// time_base is <c>1/90000</c> — sub-microsecond precision that exactly represents
    /// 1/15 fps as 6000-tick frame intervals. Matroska's <c>1/1000</c> (ms) time_base
    /// quantizes 1/15 = 66.67ms into alternating 66ms/67ms intervals, accumulating
    /// ~5-15ms of drift per segment boundary during <c>-c copy + -f concat</c>
    /// stitching. Over a 75s per-test clip spanning many segments this drift reached
    /// ~500ms, visibly misaligning cross-clip burn-in timestamps. Verified end-to-end
    /// via <c>tools/.playground/recording-validator/vfr-absolute-pts-results/parallel/</c>:
    /// two parallel TS recorders showed &lt;100µs cross-clip differential over 20-second
    /// extracted clips (vs 60-500ms differential with MKV sources).
    /// </summary>
    private const string SegmentFormat = "mpegts";
    private const string SegmentExtension = "ts";

    /// <summary>
    /// Burned-in frame-PTS overlay drawn into every recorded frame so reviewers can read the
    /// exact capture wall-clock at any scrub position and verify cross-clip alignment.
    ///
    /// <para>
    /// Rendered as black text with no box (<c>box=0</c>) at <c>x=8/y=5</c>, so it lands inside
    /// the white top row the in-game <c>TestOverlay</c> reserves — flush-left with and on the
    /// same baseline as the in-game "TICK"/"NAME" rows, so the burn-in, tick counter, and farmer
    /// name read as one coherent top-left panel instead of two overlapping overlays. <c>y=5</c>
    /// places DejaVu's glyphs in the same vertical band that <c>Game1.smallFont</c> occupies in
    /// a <c>TestOverlay.RowHeight</c> (28px) row, so the three rows share a baseline. <c>x=8</c> /
    /// <c>y=5</c> / <c>fontsize=24</c> must stay consistent with <c>TestOverlay.TextInsetX</c> /
    /// <c>RowHeight</c> / <c>ReservedPtsWidth</c> (cross-process pixel contract, no shared symbol
    /// — keep the comments on both sides in sync).
    /// </para>
    ///
    /// <para>
    /// Uses <c>%{pts}</c> (the frame's own PTS) rather than <c>%{gmtime}</c>:
    /// <c>%{gmtime}</c> reads <c>time(NULL)</c> at filter-execution time, which can lag
    /// the frame's capture wall-clock by 90-140ms (varies per recorder under load), so
    /// two recorders' burn-ins disagree even when their PTS streams agree. <c>%{pts}</c>
    /// under <c>-use_wallclock_as_timestamps 1 -copyts</c> IS the container
    /// CLOCK_REALTIME at x11grab demux — identical across recorders on the same host.
    /// The displayed value is a Unix-epoch float (e.g. <c>1778915723.537</c>); reviewers
    /// who want <c>HH:MM:SS</c> pipe the integer part through <c>date -u -d @N</c>.
    /// </para>
    ///
    /// <para>
    /// Font path is verified present in the base image (Debian 11) by
    /// <c>tools/.playground/recording-validator/vfr-absolute-pts-probe.sh</c>'s A4 check.
    /// Monospace keeps the burn-in box width constant.
    /// </para>
    ///
    /// <para>
    /// <b>drawtext parser caveat — no literal colon in <c>text=</c>:</b> this image's ffmpeg
    /// 8.1.1 (BtbN gpl static) treats <c>:</c> as the avfilter option separator even inside a
    /// <c>text=</c> value, and the colon is unescapable. Verified shell-free via
    /// <c>-filter_script</c> (byte-checked input, no shell layer): a plain <c>text=A:B</c>
    /// fails with "No option name near 'B'" whether bare, backslash-escaped (<c>A\:B</c>,
    /// <c>A\\:B</c>), or single-quoted (<c>'A:B'</c>) — all four. It is the colon itself, not
    /// the <c>%{}</c> expansion (a colon-free <c>text='TIME %{pts}'</c> renders fine). This is
    /// also why argument-bearing expansion forms with internal colons (<c>%{pts\:hms}</c>) fail
    /// — only argument-less forms work (<c>%{pts}</c>, <c>%{gmtime}</c>, <c>%{n}</c>). So the
    /// label is space-separated (<c>TIME %{pts}</c>), matching the in-game overlay's
    /// "TICK {ticks}" / "NAME {farmer}" rows. Do not "fix" it back to <c>TIME:</c>.
    /// </para>
    /// </summary>
    private const string BurnInFilter =
        "drawtext=fontfile=/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf:"
        + "fontsize=24:fontcolor=black:box=0:x=8:y=5:"
        + "text=TIME %{pts}";

    private static readonly string[] ContainerDeadIndicators =
    {
        "No such container",
        "is not running",
        "is dead or marked for removal",
        "InternalServerError",
    };

    /// <summary>Effective recording FPS (after capping to game FPS).</summary>
    public int Fps => _fps;

    /// <summary>Whether the recorder is actively recording.</summary>
    public bool IsRecording => _state == RecorderState.Recording;

    /// <summary>True once a recorder exec has thrown a Docker error indicating the container
    /// is gone (no-such-container / not-running / dead / InternalServerError). Latched: stays
    /// true once set, so all remaining recorder operations short-circuit instead of throwing
    /// the same error repeatedly. Typically triggered by emergency cleanup during Ctrl+C.</summary>
    public bool IsContainerDead => _containerDead;

    /// <summary>Human-readable state for diagnostics.</summary>
    public string State => _state.ToString();

    /// <summary>
    /// Container wall-clock epoch (Unix seconds) of the first captured frame,
    /// read from <c>segments.csv</c> row 1's <c>start_time</c> column (which
    /// equals seg_0001's first-packet PTS, equivalently seg_0000's end_time
    /// since segments are contiguous). Under
    /// <c>-use_wallclock_as_timestamps 1 -copyts -fps_mode passthrough</c>
    /// (no <c>-reset_timestamps</c>), the segment muxer writes each row's
    /// timestamps in absolute Unix-epoch seconds.
    /// <para>
    /// Reading from segments.csv (rather than via <c>ffprobe</c> on the segment
    /// file itself) is format-agnostic: MPEG-TS encapsulates packet PTS in a
    /// 33-bit wrapping field at 1/90000 time_base, which would corrupt the
    /// wall-clock value if read back through ffprobe. The CSV sidecar carries
    /// the unwrapped value the muxer was handed.
    /// </para>
    /// </summary>
    public double StartContainerEpoch { get; private set; }

    /// <summary>
    /// Which mechanism provided <see cref="StartContainerEpoch"/> on this
    /// recorder. <c>"segments_csv"</c> is the production path (read row 1's
    /// <c>start_time</c> from segments.csv). Other values indicate fallback
    /// paths; emitted on <c>recording_started</c> so reviewers can see when
    /// the primary anchor source failed for a given recorder.
    /// </summary>
    public string FirstFramePtsSource { get; private set; } = "";

    /// <summary>
    /// Constant additive offset between host monotonic clock (as
    /// <c>Stopwatch.GetTimestamp() / Stopwatch.Frequency</c> seconds) and container
    /// wall-clock epoch seconds. Mirrors <see cref="DockerHost.GetHostClockOffsetAsync"/>'s
    /// host-shared value: all recorders on the same host use the same number, so any
    /// residual calibration error (RTT/2 asymmetry) applies uniformly and cancels out
    /// in cross-clip comparisons. Per-recorder calibration would re-introduce ~tens of
    /// ms of inter-recorder differential — verified by the playground prototype, which
    /// is why the offset is host-shared.
    /// </summary>
    public double HostToContainerOffsetSeconds { get; private set; }

    /// <summary>
    /// Exec round-trip (ms) of the host's offset calibration. Per host, not per recorder.
    /// Bounds the absolute calibration error at ±RttMs/2 (see
    /// <see cref="HostToContainerOffsetSeconds"/> for why this is fine for cross-recorder
    /// comparisons). A growing trend across runs indicates the host's exec channel is
    /// degrading.
    /// </summary>
    public double CalibrationRttMs { get; private set; }

    /// <summary>
    /// Number of host-shared calibration samples observed (1 — one `date` read per host).
    /// Per host, not per recorder.
    /// </summary>
    public int CalibrationSamples { get; private set; }

    /// <summary>
    /// True when this recorder read the host's offset from cache (a prior
    /// recorder on the same host already calibrated). False on the first
    /// recorder per host, which paid the calibration cost.
    /// </summary>
    public bool ClockOffsetFromCache { get; private set; }

    /// <summary>
    /// Translates a host monotonic timestamp (from <c>Stopwatch.GetTimestamp()</c>) to a
    /// container wall-clock Unix-epoch second, using the host-shared offset. The result is
    /// in the same coordinate system as the recorder's source-segment PTS, so it can be
    /// passed directly to <c>ffmpeg -ss</c> as a seek target.
    /// </summary>
    public double ContainerEpochAtHostTicks(long hostTicks) =>
        (double)hostTicks / Stopwatch.Frequency + HostToContainerOffsetSeconds;

    public ContainerRecorder(
        IContainer container,
        DockerHost host,
        string displayLabel,
        int fps,
        Action<string> log,
        bool useGpu,
        DockerExtractLimiter? extractLimiter = null
    )
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _displayLabel = displayLabel ?? throw new ArgumentNullException(nameof(displayLabel));
        _fps = Math.Max(fps, 1);
        _segmentTime = RecordingPolicy.SegmentTime;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _useGpu = useGpu;
        _extractLimiter = extractLimiter;
    }

    /// <summary>
    /// Starts continuous ffmpeg recording of the container's X11 display using the segment muxer.
    /// Segments are written to /recordings inside the container.
    /// Waits until ffmpeg is confirmed writing frames before returning.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        // Single-call: each container creates one recorder. Guard against accidental
        // double-invocation, which would wipe the live recording's segments at the mkdir+rm
        // below and orphan the first ffmpeg.
        if (_state != RecorderState.NotStarted)
        {
            _log(
                $"[Recording] WARNING: StartAsync re-entered in {_displayLabel} (state={_state}); ignoring"
            );
            return _state == RecorderState.Recording;
        }

        try
        {
            string lastFailureReason = "exec_failed";
            long? lastExitCode = null;
            string? lastFfmpegStderr = null;

            // Retry recording start. X11 display may not be fully ready on first attempt
            // (e.g., display server still initializing after container health check passed).
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                // Per-frame PTS = container CLOCK_REALTIME, preserved through to disk:
                //   -use_wallclock_as_timestamps 1: stamp each captured frame with wall-clock.
                //   -copyts + -fps_mode passthrough: don't rebase or re-quantize. Each frame
                //     keeps its capture moment as its PTS, so clip-time T in any per-test clip
                //     corresponds to the same wall-clock moment regardless of recorder.
                //     `-fps_mode cfr` is NOT used: combined with -copyts + wall-clock PTS it
                //     interprets each input frame as "~56 years late on a uniform output grid"
                //     and silently drops all of them (empirically: 0 frames out, all dropped).
                //     Even if -copyts were also dropped to make CFR functional, the uniform
                //     output grid would lie about each frame's actual capture moment, hiding
                //     sample-phase jitter inside a fictionally precise time axis.
                //   no -reset_timestamps: each segment's PTS continues the previous segment's,
                //     so extraction can seek by absolute Unix epoch across boundaries.
                var input =
                    $"-f x11grab -video_size {RecordingPolicy.Width}x{RecordingPolicy.Height} "
                    + $"-framerate {_fps} -i :0";
                var encoder = BuildEncoderArgs();
                var segment =
                    $"-f segment -segment_time {_segmentTime} -segment_format {SegmentFormat} "
                    + $"-segment_list {RecDir}/segments.csv -segment_list_type csv";

                // Phase-lock preamble: sleep until CLOCK_REALTIME is at a 1/fps boundary,
                // so all recorders on the same Docker host (which share CLOCK_REALTIME)
                // begin sampling x11grab on the same wall-clock grid. Eliminates cross-
                // recorder sample-phase jitter that otherwise scales as 1/(2*fps) — up to
                // 500ms at fps=1, 33ms at fps=15. Empirically verified: 407ms differential
                // collapses to <2ms steady-state over a 5-minute recording.
                //
                // Target boundary and wait are computed inside the shell using the
                // container's own `date +%s.%N` (CLOCK_REALTIME). This way the sleep
                // applies to the exact same shell that immediately exec's `nohup ffmpeg`,
                // with no exec-RTT round-trip between target computation and sleep.
                // The realized target is echoed to stdout as PHASE_LOCK_TARGET=<value>
                // so the C# diagnostic emit can capture it on `recording_started`.
                //
                // Per-host fix only: cross-host alignment depends on inter-host NTP drift
                // and is out of scope here. When SERVER_FPS != CLIENT_FPS, recorders lock
                // to different grids (1/serverFps vs 1/clientFps) and don't share
                // boundaries — same alignment behavior as before this fix for that case.
                var periodArg = (1.0 / _fps).ToString("F9", CultureInfo.InvariantCulture);
                var phaseLockPreamble =
                    $"NOW=$(date +%s.%N); "
                    + $"TARGET=$(awk -v n=\"$NOW\" -v p={periodArg} 'BEGIN{{ t=(int(n/p)+1)*p; printf \"%.9f\", t }}'); "
                    + $"WAIT=$(awk -v n=\"$NOW\" -v t=\"$TARGET\" 'BEGIN{{ v=t-n; if (v<0) v=0; printf \"%.9f\", v }}'); "
                    + $"echo \"PHASE_LOCK_TARGET=$TARGET\"; "
                    + $"sleep \"$WAIT\"; ";

                // Prefix the directory prep into the launch exec so a cold start is one
                // round-trip (mkdir + clean stale segments + phase-lock + launch + emit PID)
                // rather than a separate mkdir+rm exec — exec round-trips are ~6s each under
                // Windows parallel-startup load. mkdir -p is idempotent; the rm clears any
                // stale/failed segments from a prior attempt.
                var ffmpegCmd =
                    $"mkdir -p {RecDir} && rm -f {RecDir}/seg_*.{SegmentExtension} {RecDir}/segments.csv {RecDir}/ffmpeg.pid {RecDir}/ffmpeg_err.log; "
                    + phaseLockPreamble
                    + $"nohup ffmpeg -use_wallclock_as_timestamps 1 {input} "
                    + $"-copyts -fps_mode passthrough {encoder} {segment} "
                    + $"{RecDir}/seg_%04d.{SegmentExtension} </dev/null >/dev/null 2>{RecDir}/ffmpeg_err.log & "
                    + $"echo $! > {RecDir}/ffmpeg.pid";

                // Launch ffmpeg. The detection block below derives StartContainerEpoch from
                // segments.csv (see that property's doc for the anchor rationale) and
                // resolves HostToContainerOffsetSeconds via the host-shared calibration
                // (DockerHost.GetHostClockOffsetAsync).
                var launchExecStart = Stopwatch.GetTimestamp();
                var result = await _container.ExecAsync(new[] { "sh", "-c", ffmpegCmd }, ct);
                var launchExecDoneTicks = Stopwatch.GetTimestamp();
                if (result.ExitCode != DockerExitCodes.Success)
                {
                    _log(
                        $"[Recording] WARNING: ffmpeg launch failed in {_displayLabel} (attempt {attempt}/3): exit={result.ExitCode}"
                    );
                    lastFailureReason = "exec_failed";
                    lastExitCode = result.ExitCode;
                    if (attempt < 3)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }
                    break;
                }

                // Parse `PHASE_LOCK_TARGET=<epoch>` emitted by the preamble. Best-effort:
                // absence (e.g. future image where `date` semantics changed) leaves
                // phaseLockTargetEpoch as NaN, which the recording_started emit handles.
                double phaseLockTargetEpoch = double.NaN;
                foreach (var rawLine in result.Stdout.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (
                        line.StartsWith("PHASE_LOCK_TARGET=", StringComparison.Ordinal)
                        && double.TryParse(
                            line.AsSpan("PHASE_LOCK_TARGET=".Length),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var pt
                        )
                        && pt > 1e9
                    )
                    {
                        phaseLockTargetEpoch = pt;
                        break;
                    }
                }

                // Wait for segments.csv row 2 in a single blocking exec rather than an
                // 80-iteration C# poll. We wait for row 2 (segments.csv line 2 = seg_0001's
                // entry) rather than row 1 because row 1's start_time is the degenerate
                // stream-relative 0 — see the coordinate-system notes in ReadSegmentListAsync.
                // Row 2's column-2 start_time is the first absolute Unix-epoch value in the
                // file, which becomes StartContainerEpoch (the load-bearing anchor).
                //
                // The loop runs IN-CONTAINER (sub-second `sleep 0.1`, no exec RTT per
                // iteration), so the whole wait is one round-trip instead of up to 80. Two
                // guards are load-bearing and BOTH must be in the loop's accept condition:
                //   - `wc -l >= 2`: counts newline-terminated lines, so row 2 is only seen
                //     once its trailing newline (hence all prior bytes, including start_time)
                //     is flushed. This is the torn-line guard — the >1e9 check below does NOT
                //     catch a truncated epoch (e.g. "1780074861." still parses as >1e9).
                //   - start_time > 1e9: rejects a too-early/garbage value and keeps waiting,
                //     mirroring the prior C# retry that the segments.csv-anchor fix installed.
                // Anchor value comes from segments.csv text only — never ffprobe the .ts
                // (MPEG-TS PTS wraps mod 2^33). Iteration budget is sized to cover ~60s wall
                // (600 × 0.1s) — generous because in-shell iterations are nearly free, vs
                // the prior ~8s sleep budget that was inflated by per-iteration exec RTT.
                const int waitIters = 600;
                // .WaitAsync(ct) makes cancellation prompt: Testcontainers' ExecAsync does
                // not poll the CT mid-exec, so without this a cancel would block up to the
                // full in-container budget (~60s). On cancel we abandon the exec; the orphaned
                // shell harmlessly exits on its own (it either detects seg_0001 or times out).
                var detectResult = await _container
                    .ExecAsync(
                        new[]
                        {
                            "sh",
                            "-c",
                            $"for _i in $(seq 1 {waitIters}); do "
                                + $"if [ -f {RecDir}/segments.csv ] && [ \"$(wc -l < {RecDir}/segments.csv 2>/dev/null)\" -ge 2 ]; then "
                                + $"_row2=$(sed -n '2p' {RecDir}/segments.csv); "
                                + $"_epoch=$(printf '%s' \"$_row2\" | cut -d, -f2); "
                                + $"if awk -v e=\"$_epoch\" 'BEGIN{{ exit !(e+0 > 1000000000) }}'; then "
                                + $"printf '%s\\nREADY\\n' \"$_row2\"; exit 0; "
                                + $"fi; "
                                + $"fi; "
                                + $"sleep 0.1; "
                                + $"done; "
                                + $"echo TIMEOUT",
                        },
                        ct
                    )
                    .WaitAsync(ct);
                var detectStopwatchTicks = Stopwatch.GetTimestamp();

                double firstFramePts = double.NaN;
                bool gotFirstFramePts = false;

                var detectLines = detectResult
                    .Stdout.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToArray();
                if (detectLines.Length >= 2 && detectLines[^1] == "READY")
                {
                    // detectLines[^2] = segments.csv row 2, format
                    // "seg_0001.ext,<start_time>,<end_time>". Column [1] is seg_0001's
                    // first-frame Unix-epoch second. C# re-validates >1e9 as defense in
                    // depth (the in-shell loop already gated on it).
                    var cols = detectLines[^2].Split(',');
                    if (
                        cols.Length >= 2
                        && double.TryParse(
                            cols[1],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var pts
                        )
                        && pts > 1e9
                    ) // Unix epoch sanity floor (year 2001+)
                    {
                        firstFramePts = pts;
                        gotFirstFramePts = true;
                    }
                }

                if (gotFirstFramePts && !double.IsNaN(firstFramePts))
                {
                    // Host-shared offset. First recorder per host pays a single
                    // calibration exec; later recorders read the cache. Either way,
                    // every recorder on this host gets the SAME value, which is what
                    // cross-clip alignment requires.
                    var offset = await _host.GetHostClockOffsetAsync(_container, ct);
                    HostToContainerOffsetSeconds = offset.OffsetSec;
                    CalibrationRttMs = offset.CalibrationRttMs;
                    CalibrationSamples = offset.CalibrationSamples;
                    ClockOffsetFromCache = offset.FromCache;

                    StartContainerEpoch = firstFramePts;
                    FirstFramePtsSource = "segments_csv";

                    var launchExecMs = (long)
                        Stopwatch
                            .GetElapsedTime(launchExecStart, launchExecDoneTicks)
                            .TotalMilliseconds;
                    // detectMs now spans launch-exec-done → the single blocking detect exec
                    // returning, so it includes the full in-container wait for seg_0001 to
                    // appear (not just one poll's RTT as before). It is a diagnostic only.
                    var detectMs = (long)
                        Stopwatch
                            .GetElapsedTime(launchExecDoneTicks, detectStopwatchTicks)
                            .TotalMilliseconds;

                    _state = RecorderState.Recording;
                    _log(
                        $"[Recording] Started in {_displayLabel} ({_fps}fps, {_segmentTime}s segments, encoder: {EncoderName}{(attempt > 1 ? $", attempt {attempt}" : "")})"
                    );
                    // Field semantics live on the corresponding properties + the
                    // InfrastructureEventLog event-catalog comment. `phaseLockOvershootMs`
                    // is the realized first-frame wall-clock minus the phase-lock target:
                    // `StartContainerEpoch` is read from segments.csv row 2 (= seg_0001's
                    // start_time = first_frame + segmentTime, since segments are contiguous),
                    // so the `-_segmentTime` term recovers `first_frame - target` — how
                    // late after the grid-boundary ffmpeg's first captured frame actually
                    // landed. Healthy: ~100-200ms (sleep precision + x11grab open + libx264
                    // init); investigate if >300ms. Null when the preamble's PHASE_LOCK_TARGET
                    // line was missing from stdout (e.g. a future image where `date +%s.%N`
                    // semantics changed) — the recorder still works, just without phase-lock
                    // synchronization.
                    InfrastructureEventLog.Emit(
                        "recording_started",
                        new
                        {
                            container = _displayLabel,
                            fps = _fps,
                            segmentTime = _segmentTime,
                            encoder = EncoderName,
                            attempt,
                            launchExecMs,
                            detectMs,
                            startContainerEpoch = StartContainerEpoch,
                            firstFramePtsSource = FirstFramePtsSource,
                            hostToContainerOffsetMs = (long)
                                Math.Round(HostToContainerOffsetSeconds * 1000),
                            calibrationRttMs = (long)Math.Round(CalibrationRttMs),
                            calibrationSamples = CalibrationSamples,
                            clockOffsetFromCache = ClockOffsetFromCache,
                            phaseLockTargetEpoch,
                            phaseLockOvershootMs = double.IsNaN(phaseLockTargetEpoch)
                                ? (long?)null
                                : (long)
                                    Math.Round(
                                        (StartContainerEpoch - phaseLockTargetEpoch - _segmentTime)
                                            * 1000
                                    ),
                        }
                    );
                    return true;
                }

                // ffmpeg didn't produce output; read its stderr for the actual error
                var ffmpegErr = "";
                try
                {
                    var errResult = await _container.ExecAsync(
                        new[] { "sh", "-c", $"cat {RecDir}/ffmpeg_err.log 2>/dev/null | tail -5" },
                        ct
                    );
                    ffmpegErr = string.Join(
                        " | ",
                        errResult
                            .Stdout.Split('\n')
                            .Select(l => l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                    );
                }
                catch
                { /* ignore read errors */
                }
                _log(
                    $"[Recording] WARNING: ffmpeg not producing output in {_displayLabel} (attempt {attempt}/3): {ffmpegErr}"
                );
                lastFailureReason = "no_output";
                lastFfmpegStderr = string.IsNullOrEmpty(ffmpegErr) ? null : ffmpegErr;

                // Kill the failed ffmpeg before retrying
                await SignalFfmpeg("9", ct);

                if (attempt < 3)
                {
                    await Task.Delay(1000, ct);
                }
            }

            _state = RecorderState.Failed;
            InfrastructureEventLog.Emit(
                "recording_start_failed",
                new
                {
                    container = _displayLabel,
                    reason = lastFailureReason,
                    exitCode = lastExitCode,
                    attempts = 3,
                    ffmpegStderr = lastFfmpegStderr,
                }
            );
            return false;
        }
        catch (Exception ex)
        {
            _log($"[Recording] WARNING: Failed to start in {_displayLabel}: {ex.Message}");
            _state = RecorderState.Failed;
            InfrastructureEventLog.Emit(
                "recording_start_failed",
                new
                {
                    container = _displayLabel,
                    reason = "exception",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                }
            );
            return false;
        }
    }

    /// <summary>
    /// Stops ffmpeg recording. On the happy path, all segments are finalized on disk and
    /// ready for clip extraction or full-recording retrieval. Escalates SIGINT → SIGTERM →
    /// SIGKILL on unresponsive ffmpeg; under SIGKILL, the active segment is re-muxed to
    /// drop partial trailing packets. No-ops when not in Recording state or when the
    /// container is already dead.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_state != RecorderState.Recording)
        {
            _state = RecorderState.Stopped;
            return;
        }

        if (_containerDead)
        {
            _state = RecorderState.Stopped;
            return;
        }

        try
        {
            // SIGINT triggers ffmpeg's graceful shutdown: finalize current segment, flush
            // segments.csv, and exit. Prior segments are already on disk; only the active
            // segment needs the trailer written.
            await SignalFfmpeg("INT", ct);

            for (var i = 0; i < 20; i++) // up to 10s for graceful finalization
            {
                if (_containerDead)
                {
                    _state = RecorderState.Stopped;
                    return;
                }
                await Task.Delay(500, ct);
                var check = await _container.ExecAsync(
                    new[]
                    {
                        "sh",
                        "-c",
                        $"pid=$(cat {RecDir}/ffmpeg.pid 2>/dev/null); "
                            + $"[ -n \"$pid\" ] && kill -0 $pid 2>/dev/null && echo RUNNING || echo DONE",
                    },
                    ct
                );
                if (check.Stdout.Trim().Contains("DONE"))
                {
                    _state = RecorderState.Stopped;
                    _log($"[Recording] Stopped in {_displayLabel}");
                    InfrastructureEventLog.Emit(
                        "recording_stopped",
                        new { container = _displayLabel, via = "sigint" }
                    );
                    return;
                }
            }

            if (_containerDead)
            {
                _state = RecorderState.Stopped;
                return;
            }
            _log(
                $"[Recording] WARNING: ffmpeg not responding to SIGINT in {_displayLabel} after 10s, trying SIGTERM"
            );
            await SignalFfmpeg("TERM", ct);

            for (var i = 0; i < 6; i++) // up to 3s more
            {
                if (_containerDead)
                {
                    _state = RecorderState.Stopped;
                    return;
                }
                await Task.Delay(500, ct);
                var check = await _container.ExecAsync(
                    new[]
                    {
                        "sh",
                        "-c",
                        $"pid=$(cat {RecDir}/ffmpeg.pid 2>/dev/null); "
                            + $"[ -n \"$pid\" ] && kill -0 $pid 2>/dev/null && echo RUNNING || echo DONE",
                    },
                    ct
                );
                if (check.Stdout.Trim().Contains("DONE"))
                {
                    _state = RecorderState.Stopped;
                    _log($"[Recording] Stopped in {_displayLabel} (after SIGTERM)");
                    InfrastructureEventLog.Emit(
                        "recording_stopped",
                        new { container = _displayLabel, via = "sigterm" }
                    );
                    return;
                }
            }

            if (_containerDead)
            {
                _state = RecorderState.Stopped;
                return;
            }
            _log($"[Recording] WARNING: ffmpeg not responding in {_displayLabel}, sending kill -9");
            await SignalFfmpeg("9", ct);
            await Task.Delay(500, ct);

            if (_containerDead)
            {
                _state = RecorderState.Stopped;
                return;
            }
            // kill -9 may leave the active segment with a truncated trailer / partial final
            // packet. TS itself decodes from any byte position, but a clean re-mux drops the
            // partial packets so downstream concat-copy doesn't error on them.
            _log($"[Recording] Remuxing last segment after kill -9 in {_displayLabel}");
            var remux = await _container.ExecAsync(
                new[]
                {
                    "sh",
                    "-c",
                    $"last=$(ls -1 {RecDir}/seg_*.{SegmentExtension} 2>/dev/null | sort | tail -1); "
                        + $"[ -n \"$last\" ] && ffmpeg -y -i \"$last\" -c copy \"${{last%.{SegmentExtension}}}_fixed.{SegmentExtension}\" "
                        + $"&& mv \"${{last%.{SegmentExtension}}}_fixed.{SegmentExtension}\" \"$last\" || true",
                },
                ct
            );
            var remuxFixed = remux.ExitCode == DockerExitCodes.Success;
            if (!remuxFixed)
            {
                _log(
                    $"[Recording] WARNING: Last segment remux failed in {_displayLabel}: exit={remux.ExitCode}"
                );
                LogStderr(remux.Stderr);
            }

            _state = RecorderState.Stopped;
            InfrastructureEventLog.Emit(
                "recording_stopped",
                new
                {
                    container = _displayLabel,
                    via = "kill9",
                    remuxFixed,
                }
            );
        }
        catch (Exception ex)
        {
            if (IsContainerDeadError(ex))
            {
                MarkContainerDead();
            }
            else
            {
                _log($"[Recording] WARNING: Error stopping in {_displayLabel}: {ex.Message}");
            }

            _state = RecorderState.Stopped;
        }
    }

    /// <summary>
    /// Extracts a clip from the live (still-recording) segmented output via the two-pass
    /// concat-copy + re-encode scheme (see <see cref="BuildExtractCommand"/>). Safe to
    /// call concurrently: each invocation uses Guid-based temp file IDs for the per-clip
    /// concat list and merged file.
    ///
    /// <para>
    /// <paramref name="startEpoch"/> is the container Unix-epoch second at the desired
    /// clip-start moment (= the orchestrator's <c>markEpoch</c>); BuildExtractCommand
    /// translates it to stream-relative seconds against the merged TS before passing
    /// to <c>ffmpeg -ss</c>.
    /// </para>
    /// </summary>
    public async Task<ExtractionResult> ExtractClipFromLiveAsync(
        double startEpoch,
        double durationSec,
        string hostDestPath,
        CancellationToken ct = default
    )
    {
        if (_containerDead)
        {
            return new ExtractionResult(null, null);
        }

        if (_state != RecorderState.Recording)
        {
            _log(
                $"[Recording] WARNING: ExtractClipFromLive called but state is {_state} (expected Recording)"
            );
            InfrastructureEventLog.Emit(
                "recording_clip_failed",
                new
                {
                    container = _displayLabel,
                    mode = "live",
                    stage = "wrong_state",
                    observedState = _state.ToString(),
                }
            );
            return new ExtractionResult(null, null);
        }
        if (durationSec <= 0)
        {
            _log(
                FormattableString.Invariant(
                    $"[Recording] WARNING: Zero/negative duration ({durationSec:F2}s) for live extraction in {_displayLabel}"
                )
            );
            InfrastructureEventLog.Emit(
                "recording_clip_failed",
                new
                {
                    container = _displayLabel,
                    mode = "live",
                    stage = "zero_duration",
                    durationSec,
                }
            );
            return new ExtractionResult(null, null);
        }

        using var extractCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Budget scales with clip duration to cover pathological cases where concat-copy of
        // a large cover set is slow. max(30s, 5×durationSec) holds a 30s floor for short
        // clips and ~6 minutes for a 75s clip.
        var budgetSec = Math.Max(30.0, durationSec * 5.0);
        extractCts.CancelAfter(TimeSpan.FromSeconds(budgetSec));

        var id = Guid.NewGuid().ToString("N")[..8];
        var clipName = $"clip_{id}.mp4";
        var endEpoch = startEpoch + durationSec;
        var encodedWith = EncoderName;

        try
        {
            // Discover segments and select which ones cover the clip (1 docker exec)
            List<SegmentInfo> segments;
            string? activeFile;
            try
            {
                (segments, activeFile) = await ReadSegmentListAsync(extractCts.Token);
            }
            catch (Exception ex)
            {
                _log(
                    $"[Recording] WARNING: Segment discovery failed in {_displayLabel}: {ex.GetType().Name}: {ex.Message}"
                );
                InfrastructureEventLog.Emit(
                    "recording_clip_failed",
                    new
                    {
                        container = _displayLabel,
                        mode = "live",
                        stage = "segment_discovery_failed",
                        exceptionType = ex.GetType().Name,
                        message = ex.Message,
                    }
                );
                return new ExtractionResult(null, null);
            }
            if (segments.Count == 0 && activeFile == null)
            {
                _log($"[Recording] WARNING: No segments found in {_displayLabel}");
                InfrastructureEventLog.Emit(
                    "recording_clip_failed",
                    new
                    {
                        container = _displayLabel,
                        mode = "live",
                        stage = "no_segments",
                    }
                );
                return new ExtractionResult(null, null);
            }

            var (finalized, covering, needsActive) = SelectCoveringSegments(
                segments,
                activeFile,
                startEpoch,
                endEpoch
            );

            if (covering.Count == 0)
            {
                _log(
                    FormattableString.Invariant(
                        $"[Recording] WARNING: No segments cover timespan {startEpoch:F2}--{endEpoch:F2} (epoch) in {_displayLabel}"
                    )
                );
                InfrastructureEventLog.Emit(
                    "recording_clip_failed",
                    new
                    {
                        container = _displayLabel,
                        mode = "live",
                        stage = "no_coverage",
                        offsetSec = startEpoch,
                        durationSec,
                    }
                );
                return new ExtractionResult(null, null);
            }

            // When the clip falls entirely within the active segment (0 finalized covering),
            // the extraction shell script will wait for segment rotation (finalization) using
            // local grep on segments.csv inside the container. No C# polling needed.
            if (needsActive && finalized.Count == 0)
            {
                _log(
                    $"[Recording] {_displayLabel}: active-only clip, rotation wait embedded in extraction"
                );
            }

            var segDesc = needsActive
                ? $"{finalized.Count} finalized + active segment"
                : $"{covering.Count} segment{(covering.Count != 1 ? "s" : "")}";
            _log(
                $"[Recording] Extracting clip from {_displayLabel}: "
                    + FormattableString.Invariant(
                        $"epoch {startEpoch:F2}, duration {durationSec:F2}s, {segDesc} "
                    )
                    + $"(encoded with: {encodedWith})"
            );

            // Extract via two-pass concat-copy then re-encode; see BuildExtractCommand.
            var result = await RunExtractionAsync(
                clipName,
                hostDestPath,
                covering,
                startEpoch,
                durationSec,
                needsActive,
                id,
                "",
                encodedWith,
                mode: "live",
                finalizedSegmentCount: finalized.Count,
                extractCts.Token
            );

            if (result.HostPath != null)
            {
                return result;
            }

            // Extraction failed. Re-read segments: the active segment may have rotated
            // (become finalized) since our initial discovery, so a fresh cover set may
            // succeed where the previous one couldn't (e.g. the active file was still
            // unwritable when pass-1 concat-copy ran).
            try
            {
                var (retrySegs, retryActive) = await ReadSegmentListAsync(extractCts.Token);
                var (retryFinalized, retryCovering, retryNeedsActive) = SelectCoveringSegments(
                    retrySegs,
                    retryActive,
                    startEpoch,
                    endEpoch
                );

                if (
                    retryCovering.Count > 0
                    && (retryFinalized.Count > finalized.Count || !retryNeedsActive)
                )
                {
                    _log(
                        $"[Recording] Re-discovered {retryFinalized.Count} finalized segments for {_displayLabel} (was {finalized.Count}), retrying"
                    );
                    result = await RunExtractionAsync(
                        clipName,
                        hostDestPath,
                        retryCovering,
                        startEpoch,
                        durationSec,
                        retryNeedsActive,
                        id + "_rd",
                        "",
                        encodedWith,
                        mode: "live",
                        finalizedSegmentCount: retryFinalized.Count,
                        extractCts.Token
                    );
                    return result;
                }
            }
            catch (Exception ex)
            {
                _log(
                    $"[Recording] WARNING: Segment re-discovery failed in {_displayLabel}: {ex.GetType().Name}: {ex.Message}"
                );
            }

            return new ExtractionResult(null, null);
        }
        catch (Exception ex)
        {
            if (IsContainerDeadError(ex))
            {
                MarkContainerDead();
            }
            else
            {
                _log(
                    $"[Recording] WARNING: Live clip extraction error in {_displayLabel}: {ex.GetType().Name}: {ex.Message}"
                );
            }

            InfrastructureEventLog.Emit(
                "recording_clip_failed",
                new
                {
                    container = _displayLabel,
                    mode = "live",
                    stage = "exception",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                }
            );
            return new ExtractionResult(null, null);
        }
    }

    /// <summary>
    /// Concatenates all segments into a single MP4 for the full container recording.
    /// Must be called after StopAsync. Browsers play MP4 inline.
    ///
    /// This only runs once at container disposal (not per-test), so O(total recording size)
    /// is acceptable. Stream copy (no re-encoding, just remuxing into a single container).
    /// -movflags +faststart moves the moov atom to the start for browser streaming.
    /// </summary>
    public async Task ConvertToMp4Async(CancellationToken ct = default)
    {
        if (_containerDead)
        {
            return;
        }

        _log($"[Recording] {_displayLabel}: concatenating TS segments -> MP4 (stream copy)");
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await _container.ExecAsync(
                new[]
                {
                    "sh",
                    "-c",
                    $"ls -1 {RecDir}/seg_*.{SegmentExtension} 2>/dev/null | sort | sed \"s|.*|file '&'|\" > {RecDir}/concat_all.txt; "
                        + $"if [ ! -s {RecDir}/concat_all.txt ]; then "
                        + $"echo 'No segments found for concatenation' >&2; rm -f {RecDir}/concat_all.txt; exit 1; "
                        + $"fi; "
                        + $"ffmpeg -y -f concat -safe 0 -i {RecDir}/concat_all.txt -c copy -movflags +faststart {RecDir}/recording.mp4; "
                        + $"rc=$?; rm -f {RecDir}/concat_all.txt; exit $rc",
                },
                ct
            );
            sw.Stop();

            if (result.ExitCode != DockerExitCodes.Success)
            {
                _log(
                    $"[Recording] WARNING: TS->MP4 failed in {_displayLabel}: exit={result.ExitCode}, took {sw.ElapsedMilliseconds}ms"
                );
                LogStderr(result.Stderr);
                InfrastructureEventLog.Emit(
                    "recording_full_convert_failed",
                    new
                    {
                        container = _displayLabel,
                        convertMs = sw.ElapsedMilliseconds,
                        exitCode = result.ExitCode,
                    }
                );
            }
            else
            {
                _log($"[Recording] {_displayLabel}: TS->MP4 done in {sw.ElapsedMilliseconds}ms");
                InfrastructureEventLog.Emit(
                    "recording_full_converted",
                    new { container = _displayLabel, convertMs = sw.ElapsedMilliseconds }
                );
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            if (IsContainerDeadError(ex))
            {
                MarkContainerDead();
                return;
            }
            _log(
                $"[Recording] WARNING: TS->MP4 error in {_displayLabel}: {ex.Message}, took {sw.ElapsedMilliseconds}ms"
            );
            InfrastructureEventLog.Emit(
                "recording_full_convert_failed",
                new
                {
                    container = _displayLabel,
                    convertMs = sw.ElapsedMilliseconds,
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                }
            );
        }
    }

    /// <summary>
    /// Retrieves the full MP4 recording from the container via Docker tar API.
    /// Must be called after ConvertToMp4Async.
    /// </summary>
    public async Task RetrieveFullRecordingAsync(
        string hostDestPath,
        CancellationToken ct = default
    )
    {
        try
        {
            var bytes = await _container.ReadFileAsync($"{RecDir}/recording.mp4", ct);
            if (bytes.Length == 0)
            {
                _log($"[Recording] WARNING: Full recording is empty in {_displayLabel}");
                InfrastructureEventLog.Emit(
                    "recording_full_retrieve_failed",
                    new { container = _displayLabel, reason = "empty" }
                );
                return;
            }

            var dir = Path.GetDirectoryName(hostDestPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(hostDestPath, bytes, ct);
            InfrastructureEventLog.Emit(
                "recording_full_retrieved",
                new
                {
                    container = _displayLabel,
                    path = hostDestPath,
                    sizeBytes = (long)bytes.Length,
                }
            );
        }
        catch (FileNotFoundException)
        {
            _log($"[Recording] WARNING: Full recording not found in container {_displayLabel}");
            InfrastructureEventLog.Emit(
                "recording_full_retrieve_failed",
                new { container = _displayLabel, reason = "not_found" }
            );
        }
        catch (Exception ex)
        {
            _log($"[Recording] WARNING: Failed to retrieve full recording: {ex.Message}");
            InfrastructureEventLog.Emit(
                "recording_full_retrieve_failed",
                new
                {
                    container = _displayLabel,
                    reason = "exception",
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                }
            );
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch
        { /* swallow: StopAsync already handles its own errors */
        }
    }

    // Private helpers

    /// <summary>
    /// Builds ffmpeg video filter + encoder arguments for continuous segment recording.
    /// Prepends the <see cref="BurnInFilter"/> drawtext overlay (wall-clock burned
    /// into every frame as visual ground-truth) to both encoder paths.
    /// NVENC: accepts native bgr0 from x11grab; drawtext inserts a CPU pixel-format
    /// conversion before NVENC, irrelevant at recording fps≤5.
    /// Software: libx264 with explicit yuv420p conversion and all-keyframe GOP.
    /// See https://developer.nvidia.com/video-encode-decode-support-matrix
    /// </summary>
    private string BuildEncoderArgs()
    {
        if (_useGpu)
        {
            // NVENC with P-frames: hardware motion estimation produces dramatically smaller
            // segments than all-I-frame mode (38x smaller on static content, tested).
            // Keyframes only at segment boundaries (1 per segmentTime seconds) — fine for
            // segment splitting itself, but pass-2 extraction's `ffmpeg -ss` on the merged
            // TS can snap back up to segmentTime seconds (vs ~one frame on the libx264 path).
            // Acceptable trade-off given the file-size win; no GPU host is in production
            // configuration today, so this is a latent caveat. To narrow it on a future GPU
            // host, drop `-g {gopSize}` to `-g 1` (each frame a keyframe) at the cost of
            // ~38x larger segments.
            var gopSize = _fps * _segmentTime;
            return $"-vf \"{BurnInFilter}\" "
                + $"-c:v h264_nvenc -preset p1 -rc constqp -qp 28 "
                + $"-g {gopSize} -force_key_frames 'expr:gte(t,n_forced*{_segmentTime})'";
        }

        // Software: -g 1 makes every frame a keyframe for clean segment splitting.
        // -pix_fmt yuv420p required explicitly (libx264 doesn't accept bgr0 natively).
        // -tune zerolatency disables x264's lookahead/sync-lookahead/B-frame queue. Without it
        // x264 holds ~10 frames in flight before emitting; at the recorder's low fps that queue
        // is ~10s deep, so a segment's content lags its PTS/mtime/strftime-epoch by ~10s and
        // per-test clips land that far before the test body. With it the recording's timestamps
        // match its content. (Measured: ~9.9s -> ~0s; see RecordingPolicy fps notes.)
        return $"-vf \"{BurnInFilter}\" "
            + $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 28 -pix_fmt yuv420p -g 1";
    }

    /// <summary>
    /// Reads segment metadata from inside the container via a single docker exec.
    /// One docker exec (~200ms) reads both the CSV and the directory listing.
    /// </summary>
    private async Task<(List<SegmentInfo> finalized, string? activeFile)> ReadSegmentListAsync(
        CancellationToken ct = default
    )
    {
        var finalized = new List<SegmentInfo>();
        var csvFileNames = new HashSet<string>(StringComparer.Ordinal);
        string? activeFile = null;

        if (_containerDead)
        {
            return (finalized, activeFile);
        }

        try
        {
            // Single docker exec: cat CSV + list segment files, separated by "---"
            var result = await _container.ExecAsync(
                new[]
                {
                    "sh",
                    "-c",
                    $"cat {RecDir}/segments.csv 2>/dev/null; echo '---'; ls -1 {RecDir}/seg_*.{SegmentExtension} 2>/dev/null | sort",
                },
                ct
            );

            var inSegList = false;
            var nonCsvFiles = new List<string>();
            foreach (var rawLine in result.Stdout.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line == "---")
                {
                    inSegList = true;
                    continue;
                }
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (!inSegList)
                {
                    var fields = line.Split(',');
                    if (
                        fields.Length >= 3
                        && double.TryParse(
                            fields[1],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var start
                        )
                        && double.TryParse(
                            fields[2],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var end
                        )
                    )
                    {
                        var fileName = Path.GetFileName(fields[0].Trim());
                        finalized.Add(new SegmentInfo(fileName, start, end));
                        csvFileNames.Add(fileName);
                    }
                }
                else
                {
                    var fileName = Path.GetFileName(line);
                    if (fileName.StartsWith("seg_") && !csvFileNames.Contains(fileName))
                    {
                        nonCsvFiles.Add(fileName);
                    }
                }
            }

            // The segment CSV may lag behind actual segment rotation due to buffered writes.
            // Files present on disk but not in the CSV are either: (a) finalized segments whose
            // CSV entry hasn't been flushed yet, or (b) the truly active (being-written) segment.
            // Only the LAST non-CSV file (by sorted name = chronological order) is active.
            // Earlier non-CSV files are finalized but not yet listed -- infer their timestamps
            // from the last CSV entry using _segmentTime increments. Without this, the active
            // segment's start time is underestimated by (nonCsvFiles.Count - 1) * _segmentTime,
            // causing seek offsets that overshoot the segment's actual data.
            //
            // Coordinate system: under the recorder's -copyts + -use_wallclock_as_timestamps
            // flags (no -reset_timestamps), segments.csv columns are container Unix-epoch
            // seconds for everything EXCEPT row 0's start_time, which is stream-relative 0.
            // Row N≥1 has both columns absolute. The inferred-timestamps math here adds
            // deltas of _segmentTime to lastCsvEnd (also absolute), so all StartTime/EndTime
            // values stored on SegmentInfo are in the same absolute coordinate system —
            // EXCEPT the parsed row 0's StartTime, which stays 0 (degenerate but harmless,
            // see SelectCoveringSegments).
            if (nonCsvFiles.Count > 0)
            {
                var lastCsvEnd = finalized.Count > 0 ? finalized[^1].EndTime : 0;
                // All but the last are inferred-finalized
                for (var i = 0; i < nonCsvFiles.Count - 1; i++)
                {
                    var inferredStart = lastCsvEnd + i * _segmentTime;
                    var inferredEnd = inferredStart + _segmentTime;
                    finalized.Add(new SegmentInfo(nonCsvFiles[i], inferredStart, inferredEnd));
                }
                activeFile = nonCsvFiles[^1];
            }
        }
        catch (Exception ex)
        {
            if (IsContainerDeadError(ex))
            {
                MarkContainerDead();
                return (finalized, activeFile);
            }
            _log(
                $"[Recording] WARNING: ReadSegmentList exec failed in {_displayLabel}: {ex.Message}"
            );
        }

        return (finalized, activeFile);
    }

    /// <summary>
    /// Selects which segments cover a clip's timespan.
    /// Returns both the finalized-only list and the full list (finalized + active live file).
    /// The active segment is included by its live container path (read directly, no snapshot).
    ///
    /// <para>
    /// Both <paramref name="startEpoch"/> and <paramref name="endEpoch"/> are container
    /// Unix-epoch seconds (= <c>markEpoch</c> values from the orchestrator). The
    /// <see cref="SegmentInfo.StartTime"/>/<see cref="SegmentInfo.EndTime"/> values
    /// are in the same coordinate system per <see cref="ReadSegmentListAsync"/>'s
    /// comment block — except row 0's StartTime, which is 0 (degenerate but harmless:
    /// <c>0 &lt; endEpoch + slack</c> is always true in production, so seg_0000 is
    /// conservatively included whenever the orchestrator's window touches it).
    /// </para>
    /// <para>
    /// <b>Slack:</b> the overlap test is widened by <c>5 * _segmentTime</c> on each side.
    /// At <c>SERVER_FPS=1</c> the slack absorbs x11grab pacing jitter (~tens of ms) and the
    /// host↔container offset calibration residual (~RTT/2; ~ms locally, tens of ms on VPS).
    /// </para>
    /// </summary>
    private (
        List<(string containerPath, double segStart)> finalized,
        List<(string containerPath, double segStart)> all,
        bool needsActive
    ) SelectCoveringSegments(
        List<SegmentInfo> segments,
        string? activeFile,
        double startEpoch,
        double endEpoch
    )
    {
        double activeStartTime = segments.Count > 0 ? segments[^1].EndTime : 0;
        double slack = 5 * _segmentTime;

        var finalized = new List<(string containerPath, double segStart)>();
        foreach (var seg in segments)
        {
            if (seg.EndTime > (startEpoch - slack) && seg.StartTime < (endEpoch + slack))
            {
                finalized.Add(($"{RecDir}/{seg.FileName}", seg.StartTime));
            }
        }

        var needsActive = activeFile != null && (endEpoch + slack) > activeStartTime;
        var all = new List<(string containerPath, double segStart)>(finalized);
        if (needsActive)
        {
            all.Add(($"{RecDir}/{activeFile}", activeStartTime));
        }

        all.Sort((a, b) => a.segStart.CompareTo(b.segStart));
        return (finalized, all, needsActive);
    }

    /// <summary>
    /// Executes a single extraction attempt: builds the ffmpeg command, runs it via docker exec,
    /// verifies output size, retrieves the clip via ReadFileAsync, and writes to the final path.
    /// </summary>
    /// <returns><see cref="ExtractionResult"/> with the host path (or null on failure) and
    /// the actual seek-landing-frame source-PTS (or null if unavailable).</returns>
    private async Task<ExtractionResult> RunExtractionAsync(
        string clipName,
        string hostDestPath,
        IReadOnlyList<(string containerPath, double segStart)> coveringSegments,
        double startEpoch,
        double durationSec,
        bool hasActiveSegment,
        string id,
        string idSuffix,
        string encodedWith,
        string mode,
        int finalizedSegmentCount,
        CancellationToken ct
    )
    {
        var clipPath = $"{RecDir}/{clipName}";
        var tArg = durationSec.ToString("F2", CultureInfo.InvariantCulture);

        var command = BuildExtractCommand(
            clipPath,
            coveringSegments,
            tArg,
            startEpoch,
            hasActiveSegment,
            $"{id}{idSuffix}"
        );

        var fullCommand = command + $"; stat -c%s {clipPath} 2>/dev/null || echo 0";

        // Backpressure: gate the docker exec on the host's ExtractLimiter so N
        // concurrent per-test extractions during broker disposal don't saturate
        // the daemon. Shared with the full-recording-on-dispose path — both are
        // docker exec on the same daemon, so they compete for the same slots.
        // The limiter is optional so the type stays usable in non-host contexts
        // (e.g., reproducer scripts) without forcing a fake.
        var sw = Stopwatch.StartNew();
        DotNet.Testcontainers.Containers.ExecResult execResult;
        if (_extractLimiter != null)
        {
            await _extractLimiter.WaitAsync(ct);
            try
            {
                execResult = await _container.ExecAsync(new[] { "sh", "-c", fullCommand }, ct);
            }
            finally
            {
                _extractLimiter.Release();
            }
        }
        else
        {
            execResult = await _container.ExecAsync(new[] { "sh", "-c", fullCommand }, ct);
        }
        sw.Stop();

        // Parse stdout. Shell emits:
        //   ACTUAL_FIRST_FRAME_PTS=<unix_epoch_float>  (single line, may not appear if pass-1 failed)
        //   <file_size>                                 (always last; from `stat -c%s` appended to command)
        var stdoutLines = execResult.Stdout.TrimEnd().Split('\n');
        var sizeStr = stdoutLines[^1].Trim();
        double? actualFirstFramePts = null;
        foreach (var rawLine in stdoutLines)
        {
            var line = rawLine.Trim();
            if (
                line.StartsWith("ACTUAL_FIRST_FRAME_PTS=", StringComparison.Ordinal)
                && double.TryParse(
                    line.AsSpan("ACTUAL_FIRST_FRAME_PTS=".Length),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var pts
                )
                && pts > 1e9
            )
            {
                actualFirstFramePts = pts;
                break;
            }
        }
        if (!long.TryParse(sizeStr, out var clipSize) || clipSize < 1024)
        {
            _log(
                FormattableString.Invariant(
                    $"[Recording] WARNING: Extraction failed for {_displayLabel}: epoch {startEpoch:F6}, duration {tArg}s, size={clipSize}B, exit={execResult.ExitCode}, took {sw.ElapsedMilliseconds}ms"
                )
            );
            LogStderr(execResult.Stderr);
            await TryExec($"rm -f {clipPath}");
            InfrastructureEventLog.Emit(
                "recording_clip_failed",
                new
                {
                    container = _displayLabel,
                    mode,
                    stage = "ffmpeg_failed",
                    offsetSec = startEpoch,
                    durationSec,
                    exitCode = execResult.ExitCode,
                    sizeBytes = clipSize,
                    extractMs = sw.ElapsedMilliseconds,
                }
            );
            return new ExtractionResult(null, null);
        }

        _log(
            $"[Recording] Clip extracted from {_displayLabel}: "
                + $"{clipSize / 1024}KB in {sw.ElapsedMilliseconds}ms (encoded with: {encodedWith})"
        );

        // Retrieve clip from container via Docker tar API.
        // Don't use the extraction CT here: extraction already succeeded inside
        // the container, we just need to retrieve the bytes.
        try
        {
            var clipBytes = await _container.ReadFileAsync($"{RecDir}/{clipName}");
            if (clipBytes.Length < 1024)
            {
                _log(
                    $"[Recording] WARNING: Retrieved clip too small ({clipBytes.Length}B) in {_displayLabel}"
                );
                InfrastructureEventLog.Emit(
                    "recording_clip_failed",
                    new
                    {
                        container = _displayLabel,
                        mode,
                        stage = "retrieve_failed",
                        offsetSec = startEpoch,
                        durationSec,
                        sizeBytes = (long)clipBytes.Length,
                        reason = "too_small",
                    }
                );
                return new ExtractionResult(null, null);
            }

            var dir = Path.GetDirectoryName(hostDestPath);
            if (dir != null)
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(hostDestPath, clipBytes);
            InfrastructureEventLog.Emit(
                "recording_clip_extracted",
                new
                {
                    container = _displayLabel,
                    mode,
                    offsetSec = startEpoch,
                    durationSec,
                    finalizedSegments = finalizedSegmentCount,
                    usedActiveSegment = hasActiveSegment,
                    sizeBytes = (long)clipBytes.Length,
                    extractMs = sw.ElapsedMilliseconds,
                    encoder = encodedWith,
                    path = hostDestPath,
                    actualFirstFramePts = actualFirstFramePts,
                }
            );
            return new ExtractionResult(hostDestPath, actualFirstFramePts);
        }
        catch (Exception ex)
        {
            _log(
                $"[Recording] WARNING: Failed to retrieve clip from container {_displayLabel}: {ex.Message}"
            );
            InfrastructureEventLog.Emit(
                "recording_clip_failed",
                new
                {
                    container = _displayLabel,
                    mode,
                    stage = "retrieve_failed",
                    offsetSec = startEpoch,
                    durationSec,
                    exceptionType = ex.GetType().Name,
                    message = ex.Message,
                }
            );
            return new ExtractionResult(null, null);
        }
    }

    /// <summary>
    /// Builds the shell command that produces a per-test clip from the covering source
    /// segments. Two passes:
    ///
    /// <list type="number">
    ///   <item>Pass 1: <c>concat-copy</c> the covering source segments into one contiguous
    ///   TS (<c>merged.ts</c>). No re-encoding; frame-to-frame intervals preserved exactly
    ///   under TS's 1/90000 time_base.</item>
    ///   <item>Pass 2: single-input <c>ffmpeg -ss REL_SS -i merged.ts -t DUR …
    ///   -avoid_negative_ts make_zero</c> trims to the requested window and rebases output
    ///   to PTS=0 for browser playback. Seeks to the keyframe at-or-before REL_SS — with
    ///   the libx264 recording path (<c>-g 1</c>), that's typically within one frame
    ///   (observed up to ~2 frames on a small fraction of clips — see
    ///   <c>recorder-anchor-first-frame.md</c> for the unresolved outlier discussion);
    ///   with the NVENC recording path (keyframes at segment boundaries only), the snap
    ///   can be up to <c>segmentTime</c> seconds. <c>actualFirstFramePts</c> (below)
    ///   reports where the seek actually landed so the orchestrator can compensate.</item>
    /// </list>
    ///
    /// <para>
    /// <b>REL_SS computation:</b> <c>ffmpeg -ss</c> takes seconds-from-stream-start, not
    /// absolute PTS. So <c>REL_SS = startEpoch − coveringSegments[0].segStart</c> (with a
    /// fallback for row 0; see <c>cover0Epoch</c> below). The merged file's first packet
    /// pts_time is nonzero (concat-copy inherits the first source's PTS base), so the
    /// post-extraction ffprobe at <c>REL_SS + _merged_first_rel</c> recovers the actual
    /// landing PTS in stream coordinates, and the burn-in delta to <c>cover0Epoch</c>
    /// yields the absolute Unix-epoch landing time for <c>ACTUAL_FIRST_FRAME_PTS</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Always re-encode pass 2:</b> <c>-c copy</c> can't trim mid-segment and ignores
    /// <c>-t</c> when source data ends earlier, producing wrong-duration / wrong-frame-count
    /// output. The full-recording remux (<see cref="ConvertToMp4Async"/>) keeps <c>-c copy</c>
    /// because it never trims.
    /// </para>
    ///
    /// <para>
    /// <b>actualFirstFramePts side-channel:</b> ffmpeg's <c>-ss</c> snaps to the keyframe
    /// at-or-before the requested time, so the landing frame's PTS may differ from REL_SS
    /// by typically one frame interval (libx264 source, occasional ~2-frame outliers) or
    /// up to <c>segmentTime</c> seconds (NVENC source — see <see cref="BuildEncoderArgs"/>).
    /// The shell emits the
    /// landing-frame PTS via <c>ACTUAL_FIRST_FRAME_PTS=&lt;value&gt;</c> so the orchestrator
    /// can compute content-aligned <c>timelineOffsetSec</c> (cross-clip alignment by actual
    /// landing frames, not requested marks).
    /// </para>
    ///
    /// <para>
    /// <b>Active segment wait:</b> when the last covering segment is still being written,
    /// a pre-pass wait (CSV-finalization or file-size growth) ensures it has data before
    /// pass-1 reads it.
    /// </para>
    ///
    /// <para>
    /// <b>Alternative not used — per-segment extract + concat-copy:</b> rejected because
    /// (a) the first-part keyframe snap shifts output PTS=0 by up to ±frame-interval from
    /// the requested wall-clock, producing per-clip alignment quantization, and
    /// (b) inter-part transitions accumulate frame-interval-sized source-PTS drift in the
    /// burn-in pixels because concat-copy preserves output-PTS spacing but not source-PTS
    /// continuity across parts. Two-pass avoids both.
    /// </para>
    /// </summary>
    private string BuildExtractCommand(
        string clipPath,
        IReadOnlyList<(string containerPath, double segStart)> coveringSegments,
        string duration,
        double startEpoch,
        bool hasActiveSegment,
        string intermediateId
    )
    {
        // -g 1 -keyint_min 1: every output frame is an IDR keyframe, so browser seeks
        // anywhere in the clip decode in O(1) instead of from frame 0 forward. libx264's
        // default GOP 250 (NVENC similar) would leave only a single keyframe per clip.
        // -fps_mode passthrough preserves per-frame PTS from the merged source (the
        // wall-clock honesty of the recorder propagates into the clip); the orchestrator
        // and UI rely on this for content-aligned timeline placement.
        // -copyts is NOT used on pass-2: combined with -ss it produces empty output.
        // -avoid_negative_ts make_zero (applied at the pass-2 ffmpeg below) rebases the
        // output to PTS=0 so the browser <video> element treats currentTime=0 as clip-start.
        var codec = _useGpu
            ? "-c:v h264_nvenc -preset p1 -rc constqp -qp 28 -g 1 -keyint_min 1 -fps_mode passthrough"
            : "-c:v libx264 -preset ultrafast -tune zerolatency -crf 28 -pix_fmt yuv420p -g 1 -keyint_min 1 -fps_mode passthrough";

        var listPath = $"{RecDir}/concat_{intermediateId}.txt";
        var mergedPath = $"{RecDir}/merged_{intermediateId}.{SegmentExtension}";
        var durationArg = duration; // already formatted F2 by caller

        var script = new List<string>();

        // When the last covering segment is the active (still-being-written) one, wait for
        // it to have data before pass-1 concat-copy reads it. Two-phase wait: CSV-
        // finalization (segment rotated) → file-size growth (data flushed). Diagnostic line
        // emitted to stderr. No wait needed when the cover is entirely finalized.
        if (hasActiveSegment)
        {
            var lastSeg = coveringSegments[^1].containerPath;
            var lastSegName = Path.GetFileName(lastSeg);
            script.Add(
                $"_fin=0; _phase=''; "
                    + $"for _i in $(seq 1 15); do "
                    + $"grep -qF '{lastSegName}' {RecDir}/segments.csv 2>/dev/null && "
                    + $"{{ _fin=1; _phase=\"csv:$_i\"; break; }}; "
                    + $"sleep 0.2; "
                    + $"done; "
                    + $"if [ \"$_fin\" = \"0\" ]; then "
                    + $"_fsz=0; "
                    + $"for _j in $(seq 1 10); do "
                    + $"_fsz=$(stat -c%s {lastSeg} 2>/dev/null || echo 0); "
                    + $"[ \"$_fsz\" -gt 2048 ] && {{ _phase=\"size:$_j/$_fsz\"; break; }}; "
                    + $"sleep 0.2; "
                    + $"done; "
                    + $"if [ -z \"$_phase\" ]; then _phase=\"timeout/fsz=$_fsz\"; fi; "
                    + $"fi; "
                    + $"_pid=$(cat {RecDir}/ffmpeg.pid 2>/dev/null); "
                    + $"_alive=0; [ -n \"$_pid\" ] && kill -0 $_pid 2>/dev/null && _alive=1; "
                    + $"echo \"WAIT_RESULT:fin=$_fin,phase=$_phase,alive=$_alive\" >&2"
            );
        }

        // Cleanup any prior intermediates with the same id (re-run safety).
        script.Add($"rm -f {listPath} {mergedPath}");

        // Pass 1: stream-copy-merge the covering source segments into merged.{ext}.
        var listLines = string.Join(
            "\\n",
            coveringSegments.Select(s => $"file '{s.containerPath}'")
        );
        script.Add($"printf '{listLines}\\n' > {listPath}");
        script.Add(
            $"ffmpeg -hide_banner -y -f concat -safe 0 -i {listPath} -c copy {mergedPath} >/dev/null 2>&1"
        );

        // cover0Epoch = absolute wall-clock of the first frame of the first covering
        // segment. Read from segments.csv (carried as segStart on each tuple); see
        // BuildExtractCommand's <summary> for why we don't ffprobe the source TS.
        // The `cover0SegStart > 1e9` fallback fires on either of two paths that both
        // produce a segStart of 0:
        //   (a) `coveringSegments[0]` is row 0 of segments.csv — that row stores
        //       seg_0000's start_time as the degenerate stream-relative 0 (the muxer's
        //       first frame is at PTS=0 in stream time before -copyts kicks in).
        //   (b) `segments.Count == 0` and the cover is active-only — then
        //       `activeStartTime` from SelectCoveringSegments collapses to 0 (no prior
        //       finalized row to chain off), and that 0 is the active file's segStart.
        // Fallback is StartContainerEpoch − segmentTime. x11grab warmup can stretch
        // seg_0000 by tens of ms, which the SelectCoveringSegments slack absorbs.
        var cover0SegStart = coveringSegments[0].segStart;
        var cover0Epoch =
            cover0SegStart > 1e9 ? cover0SegStart : StartContainerEpoch - _segmentTime;
        var cover0EpochArg = cover0Epoch.ToString("F6", CultureInfo.InvariantCulture);
        var startEpochArg = startEpoch.ToString("F6", CultureInfo.InvariantCulture);

        // Pass 2 seek target in stream-relative seconds (ffmpeg's -ss convention).
        script.Add(
            $"_rel_ss=$(awk -v a={startEpochArg} -v s={cover0EpochArg} "
                + $"'BEGIN {{ v=a-s; if (v<0) v=0; printf \"%.6f\", v }}')"
        );

        // Pass 2: extract + re-encode. Snaps to keyframe at-or-before _rel_ss
        // (every frame is a keyframe per -g 1).
        script.Add(
            $"if [ -s {mergedPath} ]; then "
                + $"ffmpeg -hide_banner -y -ss \"$_rel_ss\" -i {mergedPath} -t {durationArg} "
                + $"{codec} -avoid_negative_ts make_zero -movflags +faststart {clipPath} >/dev/null 2>&1; "
                + $"else "
                + $"echo 'EXTRACT_FAILED: pass-1 merged.ts missing or empty' >&2; "
                + $"fi"
        );

        // Emit ACTUAL_FIRST_FRAME_PTS = absolute wall-clock of the seek-landing frame.
        // ffprobe's -read_intervals takes an *absolute* pts_time in stream coords, so the
        // equivalent of `ffmpeg -ss _rel_ss` is `_merged_first_rel + _rel_ss`. The
        // difference `(_landing_rel − _merged_first_rel)` is the landing's stream-relative
        // offset; adding cover0Epoch gives the absolute Unix epoch. The subtraction is
        // immune to MPEG-TS PTS wrap because both values share the same wrapped origin.
        script.Add(
            $"_merged_first_rel=$(ffprobe -v error -select_streams v:0 -show_entries packet=pts_time "
                + $"-of csv=p=0 -read_intervals %+#1 {mergedPath} 2>/dev/null | head -1 | tr -d ',')"
        );
        script.Add(
            $"_probe_seek=$(awk -v r=\"$_rel_ss\" -v m=\"$_merged_first_rel\" "
                + $"'BEGIN {{ printf \"%.6f\", m + r }}')"
        );
        script.Add(
            $"_landing_rel=$(ffprobe -v error -select_streams v:0 -show_entries packet=pts_time "
                + $"-of csv=p=0 -read_intervals \"${{_probe_seek}}%+#1\" {mergedPath} 2>/dev/null | head -1 | tr -d ',')"
        );
        // TODO: when either ffprobe returns empty (e.g. no packet at the requested seek
        // interval), the guard below silently skips the emit and the downstream event
        // carries actualFirstFramePts=null with no reason attached. To surface the cause,
        // emit ACTUAL_FIRST_FRAME_PTS_SKIPPED=<reason> here and add an
        // actualFirstFramePtsReason field to recording_clip_extracted.
        script.Add(
            $"if [ -n \"$_landing_rel\" ] && [ -n \"$_merged_first_rel\" ]; then "
                + $"_actual_pts=$(awk -v r=\"$_landing_rel\" -v f=\"$_merged_first_rel\" -v c={cover0EpochArg} "
                + $"'BEGIN {{ printf \"%.6f\", c + (r - f) }}'); "
                + $"echo \"ACTUAL_FIRST_FRAME_PTS=$_actual_pts\"; "
                + $"fi"
        );

        // Cleanup intermediates regardless of success.
        script.Add($"rm -f {listPath} {mergedPath}");

        return string.Join("; ", script);
    }

    /// <summary>
    /// Logs the last few meaningful lines from ffmpeg stderr on extraction failure.
    /// Filters out the version/config noise that ffmpeg always prints.
    /// </summary>
    private void LogStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return;
        }

        var meaningful = stderr
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l =>
                l.Length > 0
                && !l.StartsWith("ffmpeg version", StringComparison.Ordinal)
                && !l.StartsWith("built with", StringComparison.Ordinal)
                && !l.StartsWith("configuration:", StringComparison.Ordinal)
                && !l.StartsWith("libav", StringComparison.Ordinal)
                && !l.StartsWith("libsw", StringComparison.Ordinal)
                && !l.StartsWith("libpostproc", StringComparison.Ordinal)
            )
            .TakeLast(10)
            .ToList();

        if (meaningful.Count > 0)
        {
            _log($"[Recording] ffmpeg stderr ({_displayLabel}): {string.Join(" | ", meaningful)}");
        }
    }

    /// <summary>
    /// Sends a signal to the recording ffmpeg process by reading its PID file.
    /// Returns true if the signal was delivered. Logs on unexpected failures;
    /// silently returns false if the process already exited ("No such process").
    /// </summary>
    private async Task<bool> SignalFfmpeg(string signal, CancellationToken ct = default)
    {
        if (_containerDead)
        {
            return false;
        }

        try
        {
            var result = await _container.ExecAsync(
                new[]
                {
                    "sh",
                    "-c",
                    $"pid=$(cat {RecDir}/ffmpeg.pid 2>/dev/null); "
                        + $"if [ -z \"$pid\" ]; then echo 'NO_PID' >&2; exit 1; fi; "
                        + $"kill -{signal} $pid",
                },
                ct
            );
            if (result.ExitCode != DockerExitCodes.Success)
            {
                var stderr = result.Stderr.Trim();
                // "No such process" is expected during StopAsync escalation (process already exited)
                if (!stderr.Contains("No such process"))
                {
                    _log(
                        $"[Recording] WARNING: kill -{signal} failed in {_displayLabel}: {stderr}"
                    );
                }

                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            if (IsContainerDeadError(ex))
            {
                MarkContainerDead();
            }

            return false;
        }
    }

    private async Task TryExec(string command)
    {
        if (_containerDead)
        {
            return;
        }

        try
        {
            await _container.ExecAsync(new[] { "sh", "-c", command });
        }
        catch (Exception ex)
        {
            if (IsContainerDeadError(ex))
            {
                MarkContainerDead();
            }
            else
            {
                _log(
                    $"[Recording] TryExec failed in {_displayLabel}: {ex.GetType().Name}: {ex.Message} (cmd: {command})"
                );
            }
        }
    }

    private static bool IsContainerDeadError(Exception ex)
    {
        return ContainerDeadIndicators.Any(ex.Message.Contains);
    }

    private void MarkContainerDead()
    {
        _containerDead = true;
        // Interlocked guard so concurrent dead-detections (multiple in-flight execs all
        // failing at once) emit the diagnostic exactly once instead of racing duplicates.
        if (Interlocked.Exchange(ref _containerDeadLogged, 1) == 0)
        {
            _log(
                $"[Recording] Container {_displayLabel} is dead, skipping remaining recording operations"
            );
            InfrastructureEventLog.Emit(
                "recording_container_dead",
                new { container = _displayLabel }
            );
        }
    }
}
