---
paths:
  - "tests/**/Helpers/ContainerRecorder.cs"
  - "tests/**/Helpers/Recording*.cs"
---

# ContainerRecorder load-bearing invariants

The recorder's design exists because each load-bearing choice — wall-clock-honest PTS, MPEG-TS segments, segments.csv as anchor source, phase-locked launches, two-pass extraction, `-tune zerolatency` — was reached after observing the failure mode of its alternative. Reverting any one re-introduces the bug it fixed. This rule lists each invariant with the failure mode that motivates it, so future changes can be evaluated against the right risk.

Cross-clip alignment between two recorders on the same host is currently within ~2-5ms at fps=15 and ~1-4ms at fps=1 in production. Most of the residual is irreducible (x11grab sample-phase, sleep precision). Regressions tend to show up as cross-clip burn-in differences in the hundreds-of-ms range or per-test clips landing before/after the test body.

**Burn-in geometry is coupled to the in-game overlay — but its content/timing is not.** `BurnInFilter` renders `TIME %{pts}` as black text, `box=0`, `fontsize=24`, at `x=8/y=5` so it lands inside the white top row `TestOverlay` reserves (one coherent panel instead of two overlapping overlays); `y=5` puts DejaVu's glyphs in the same vertical band `Game1.smallFont` occupies in a `TestOverlay.RowHeight` (28px) row, so all three rows share a baseline. Changing the `x`/`y`/`fontsize`/`box` is governed by `test-overlay-pixel-contract.md` (must stay consistent with `TestOverlay.TextInsetX`/`RowHeight`/`ReservedPtsWidth` and keep pixel `(0,0)` white). The `TIME ` label is space-separated because a colon in drawtext `text=` breaks the avfilter parser (verified against this image's ffmpeg 8.1.1). The invariants below — `%{pts}` content, the timing flags, segment format, anchor source — are orthogonal to this styling and unaffected by it.

## Invariant 1 — `-tune zerolatency` on libx264

`ContainerRecorder.BuildEncoderArgs` (software path) and `BuildExtractCommand`'s re-encode codec must keep `-tune zerolatency` in the `libx264` argument string.

**Failure mode without it:** x264's default lookahead/sync-lookahead/B-frame queue holds ~10 frames in flight before emitting the first packet. At fps=1 that queue is ~10s deep, so every recorded segment's content is the screen as it was ~10s before its PTS/mtime. Per-test clips land ~10s before the test body — full-length and watchable, but showing pre-test filler (title screen, "Connecting…" dialog) instead of the actual gameplay.

**Evidence:** Playground prototype `tools/.playground/recording-validator/` (decode a recorded corner pixel → which colour-step → when that step was rendered): baseline lag **+9.9s**, with `-tune zerolatency` **≈0s** (−0.6 / −0.1 / +0.6 / +0.3s at T+25/65/105/145s, all within ±1s decode noise). Realtime preserved (`speed=1.01x`). `-tune zerolatency` sets `rc-lookahead=0 sync-lookahead=0 bframes=0 sliced-threads=1`.

**Don't:** drop the flag, switch to a `-tune` that re-introduces buffering, or raise x264's lookahead manually. No anchor math can compensate for a per-frame encoder delay.

**NVENC caveat:** `-preset p1` is NVENC's lowest-latency preset, and NVENC has no x264-style lookahead by default, but GPU-host recordings haven't been measured end-to-end. If a GPU host's clips are mis-anchored, prototype the NVENC command in the playground before assuming the encoder is innocent.

## Invariant 2 — MPEG-TS segments, not Matroska

`SegmentFormat = "mpegts"`, `SegmentExtension = "ts"`. Don't switch back to MKV.

**Failure mode without it:** MKV's `1/1000` time_base can't exactly represent 1/15 fps (66.67ms) — it quantizes to alternating 66ms/67ms intervals. The `-c copy + -f concat` stitching at per-test extraction accumulates ~5-15ms of drift per segment boundary, reaching ~500ms cross-clip differential on a 75s clip. TS's `1/90000` time_base represents 1/15 fps exactly as 6000-tick intervals — zero drift across concat-copy.

**Evidence:** `tools/.playground/recording-validator/vfr-absolute-pts-results/parallel/`: two parallel TS recorders, 20s clips, <100µs cross-clip differential; same setup with MKV sources, 60-500ms differential.

**Caveat:** MPEG-TS encapsulates PTS in a 33-bit wrapping field, which leads directly to Invariant 4 below.

## Invariant 3 — wall-clock-honest PTS via `-use_wallclock_as_timestamps + -copyts + -fps_mode passthrough`, no `-reset_timestamps`

Each frame's PTS must equal its actual capture wall-clock (container CLOCK_REALTIME, Unix-epoch seconds), preserved unchanged from x11grab demux through encode/mux to disk.

**Failure mode under `-fps_mode cfr`:** the output PTS becomes a uniform grid that *lies* about which wall-clock moment each frame depicts. Two recorders' sample-phase jitter then bleeds invisibly into the time axis — cross-clip alignment drifts by the residual, and you can't detect it from the timestamps alone because they look "correct" (uniform). Passthrough preserves the jitter visibly in the PTS, so the orchestrator's `actualFirstFramePts` mechanism can compensate.

**Failure mode under `-copyts` removed:** ffmpeg rebases input PTS to start at 0. Wall-clock anchor is lost from the stream entirely; segments.csv columns become stream-relative and the cross-clip alignment math has nothing absolute to align ON.

**Failure mode under `-reset_timestamps 1`:** each segment's PTS restarts at 0, so extraction can't seek by absolute Unix epoch across segment boundaries — per-segment seek math would have to be reintroduced, and the row-N timestamps in segments.csv would no longer be the absolute wall-clocks the orchestrator depends on.

## Invariant 4 — Anchor and extraction use `segments.csv`, not `ffprobe` on the source TS

`StartContainerEpoch` is read from `segments.csv` row 1's `start_time`. `BuildExtractCommand` reads `coveringSegments[0].segStart` from the CSV (carried through `SelectCoveringSegments`).

**Failure mode under `ffprobe seg_NNNN.ts`:** MPEG-TS wraps PTS modulo 2^33 ticks at 1/90000 time_base (~26.5h). The wall-clock PTS the recorder writes (~1.78e9 in 2026) reads back from ffprobe as the wrapped value (~49254). Any Unix-epoch sanity check (`pts > 1e9`) then rejects the read, and any arithmetic between two wrapped values gives a meaningless result. The segment muxer's CSV sidecar records the unwrapped wall-clock the muxer was handed — format-agnostic across MKV/TS/etc.

**Direct empirical incident:** the startup polling loop hung the entire test harness for 23 minutes (until Ctrl-C) on the day this fix was made, because the polling check kept rejecting the wrapped PTS value, never advanced past attempt 1.

**Row 0 caveat:** `segments.csv` stores `seg_0000.ts`'s `start_time` as the degenerate stream-relative 0 (the muxer's first frame is at PTS=0 in stream time before `-copyts` kicks in). Row N≥1 has both columns as absolute Unix-epoch seconds. `SelectCoveringSegments` tolerates the 0 (since `0 < endEpoch + slack` is always true); `BuildExtractCommand` falls back to `StartContainerEpoch − segmentTime` when cover0 is row 0 — best-effort, the SelectCoveringSegments slack absorbs x11grab warmup overshoot.

## Invariant 5 — Phase-lock preamble synchronizes recorders to a CLOCK_REALTIME boundary

The shell preamble in `StartAsync` sleeps until `CLOCK_REALTIME mod (1/fps) == 0` before exec-ing `nohup ffmpeg`. All recorders on the same Docker host (which share the host kernel's CLOCK_REALTIME) then begin sampling x11grab on the same wall-clock grid.

**Failure mode without it:** each ffmpeg's x11grab polls on its own `(launch_time + N/fps)` schedule. Two recorders launched at different moments have sample-phase offsets up to `1/(2·fps)` — 33ms at fps=15, **500ms at fps=1**. With phase-lock: ~1-4ms residual sustained across 5min recording (verified empirically). Without it: ~400ms cross-recorder differential at fps=1 in the same test.

**Don't:** revert the preamble, "simplify" by removing the sleep, or assume CLOCK_REALTIME is per-container (it isn't on Linux — all containers on a host share the kernel's CLOCK_REALTIME). The preamble computes target inside the shell to avoid exec-RTT between target computation and sleep.

**Out of scope (known limitation):** cross-host recorders aren't phase-locked beyond each host's NTP-drift residual (typically 1-50ms). The `phaseLockTargetEpoch` field on `recording_started` makes drift across hosts visible — see InfrastructureEventLog's event catalog. Mixed-FPS recorders also can't share a phase grid; they lock to their own `1/fps` boundary each.

## Invariant 6 — Two-pass extraction (concat-copy → single -ss), not per-segment

`BuildExtractCommand` does pass 1 = concat-copy all covering segments into `merged.ts`, pass 2 = single-input `ffmpeg -ss REL_SS -i merged.ts -t DUR` with re-encode. Don't replace with per-segment extract + concat-copy.

**Failure mode of per-segment-extract:** (a) the first-part keyframe snap shifts output PTS=0 by up to ±keyframe-interval from the requested wall-clock, producing per-clip alignment quantization that compounds per part; (b) inter-part transitions accumulate frame-interval-sized source-PTS drift in the burn-in pixels because concat-copy preserves output-PTS spacing but not source-PTS continuity across parts. Two-pass leaves only a single keyframe snap (libx264 source: typically ≤1 frame, observed up to ~2 frames on ~3% of clips — see `seekSnapMs` notes below; NVENC source: up to `segmentTime` seconds because NVENC's `-g` is sparse — see `BuildEncoderArgs`), which the orchestrator compensates for via `actualFirstFramePts` (the seek-landing frame's source wall-clock, emitted by the shell on stdout as `ACTUAL_FIRST_FRAME_PTS=<epoch>`).

**`seekSnapMs` observed distribution (libx264, fps=15):** mean abs ~35ms, max ~125ms (~1.87 frames) on a small fraction of clips — larger than the ≤1-frame snap `-g 1` should bound. The orchestrator's `actualFirstFramePts` compensates for cross-clip alignment regardless of snap size, so these outliers are a per-clip anchoring concern, not a cross-clip one.

**Pass-2 gotchas:**
- `-ss` takes seconds-from-stream-start, not absolute pts_time. So `_rel_ss = startEpoch − cover0Epoch`.
- `-copyts` on pass 2 produces empty output (combined with `-ss`); the empty-output mode is not visible until the produced clip is inspected.
- `ffprobe -read_intervals N`, in contrast, takes N as *absolute* pts_time. The merged TS's first packet has a nonzero pts_time (concat-copy inherits the first source's PTS base), so the post-extract ffprobe seek is `_merged_first_rel + _rel_ss`. The diff `(_landing_rel − _merged_first_rel)` is wrap-immune because both share the same wrapped origin within the same file.

## Invariant 7 — Filename pattern `seg_%04d.ts`, not `seg_%s.ts` with `-strftime 1`

Resist the urge to use strftime-templated filenames.

**Failure mode under `-strftime 1` + `seg_%s.ts` (or any second-resolution template):** ffmpeg 8.1.1 in this image doesn't support sub-second strftime specifiers (`%N`/`%6N` pass through literally). At `fps=1, segment_time=1`, x11grab's startup serializes the first ~6 PTS-seconds of frames into ~1s of wall-clock during encoder warmup. The strftime expands at segment-open time, so the first 6 segments all open at the same wall-clock second and the muxer **overwrites them** — 3 files on disk where 8 should exist, segments 0-4 lost. Concrete evidence (8-frame test recording, `segments.csv`):

```
seg_1778763755.ts,0.000000,1.000000
seg_1778763755.ts,1.000000,2.000000    ← collision
seg_1778763755.ts,2.000000,3.000000    ← collision
seg_1778763755.ts,3.000000,4.000000    ← collision
seg_1778763755.ts,4.000000,5.000000    ← collision
seg_1778763755.ts,5.000000,6.000000    ← collision
seg_1778763756.ts,6.000000,7.000000    ← wall-clock ticks
seg_1778763757.ts,7.000000,8.000000
```

Sequence-number filenames are always unique and the anchor doesn't need filename-as-clock anyway (it reads segments.csv).

## Slack in `SelectCoveringSegments`

`5 * _segmentTime` slack on each side of the cover window. Phase-lock + segments.csv anchor + `-tune zerolatency` keep anchor accuracy within one frame. The remaining slack absorbs x11grab pacing jitter and the row-0 `cover0Epoch` warmup-overshoot fallback. Don't widen to mask future regressions — drift is an upstream symptom, fix the cause. Shrinking to `2 * _segmentTime` is plausible but unverified.

## Where to start debugging when cross-clip alignment regresses

Symptom→cause runbook: [`docs/developers/testing/test-harness.md` § Recording alignment debugging](../../docs/developers/testing/test-harness.md#recording-alignment-debugging). Playground reproducer scripts in `tools/.playground/recording-validator/` are the fastest way to isolate any of these without spinning up the full E2E.
