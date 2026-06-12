<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import {
    defaultFrameCount,
    THUMB_H,
    THUMB_W,
    type Thumbnail,
    useFilmstripCache,
} from "../composables/useFilmstripCache";
import { snapBreakpointSec, useVideoTimeline } from "../composables/useVideoTimeline";
import type { VideoItem } from "../types/video";
import { createLogger } from "../utils/logger";
import { DEFAULT_TARGET_TPS, METRIC_COLORS, TPS_PEAK_HEADROOM, TPS_TARGET_HEADROOM, tpsLevel } from "../utils/metrics";
import { formatTimePrecise, formatTimeRuler } from "../utils/time";
import TimelineControls from "./TimelineControls.vue";

const props = defineProps<{
    videos: VideoItem[];
    screenshotSrc: (path: string) => string;
    testDurationMs?: number | null;
    lifecycle?: { testMs: number; cleanupMs: number; artifactsMs: number } | null;
}>();

const emit = defineEmits<{
    "navigate-instance": [instanceId: string];
}>();

const log = createLogger("SyncedVideos");
let _mountId = 0;
/** Previous videos list -- used to snapshot filmstrip state for crossfade on video change. */
let _prevVideos: VideoItem[] = [];

// ── Video element refs ──
// videoRefs/previewVideoRefs stay index-based (assigned synchronously via :ref during render).
// All other per-video state is keyed by path (stable identity that survives props.videos changes).
const videoRefs = ref<(HTMLVideoElement | null)[]>([]);
const loadedCount = ref(0);
const errorPaths = ref<Set<string>>(new Set());
const videoLoadState = ref<Map<string, "loading" | "ready" | "error">>(new Map());

// ── Thumbnail filmstrip (backed by singleton composable) ──
const filmstrip = useFilmstripCache();
const prevThumbnails = shallowRef<Map<number, Thumbnail[]>>(new Map());
const crossfading = ref(false);
let crossfadeTimer: ReturnType<typeof setTimeout> | null = null;
/** How many thumbnails to generate/display for a clip. */
function frameCount(durationSec: number, clipPx: number = 0): number {
    const byWidth = clipPx > 0 ? Math.ceil(clipPx / THUMB_DISPLAY_W) : 0;
    return Math.max(defaultFrameCount(durationSec), byWidth);
}

/** Enqueue thumbnail generation for a video index. */
function queueThumbnails(index: number) {
    const v = props.videos[index];
    const displayEl = videoRefs.value[index];
    const src = displayEl?.src || (v ? props.screenshotSrc(v.path) : "");
    if (!src) return;
    filmstrip.enqueue({
        path: v.path,
        src,
        wallClockDuration: v.wallClockDuration,
        count: frameCount(effectiveDuration(index), clipWidthPx(index)),
    });
}

/** Whether this video index is currently being generated. */
function thumbIsGenerating(index: number): boolean {
    const v = props.videos[index];
    return v ? filmstrip.isGenerating(v.path) : false;
}

/** Return cached thumbnails for a clip (reactive via filmstrip.version). */
function filmstripThumbs(index: number): Thumbnail[] {
    // Touch version to establish reactive dependency
    void filmstrip.version.value;
    const v = props.videos[index];
    if (!v) return [];
    return filmstrip.get(v.path) ?? [];
}

function prevFilmstrip(index: number): Thumbnail[] {
    const cached = prevThumbnails.value.get(index);
    if (!cached || cached.length === 0) return [];
    return cached;
}

/** Pre-computed thumbnail state per video. Only recomputes when filmstrip.version bumps. */
const thumbState = computed(() => {
    void filmstrip.version.value;
    return props.videos.map((_vid, i) => ({
        generating: thumbIsGenerating(i),
        complete: filmstrip.isFullyComplete(props.videos[i].path),
        thumbs: filmstripThumbs(i),
    }));
});

/** Track layout */
const TRACK_H = 150;
const TRACK_PAD_TOP = 8; // px -- clip-track inset from top (matches sidebar mt-2)
const TRACK_PAD_BOT = 24; // px -- clip-track inset from bottom (matches sidebar label row)
const CLIP_H = TRACK_H - TRACK_PAD_TOP - TRACK_PAD_BOT;
const FILMSTRIP_H = Math.round(CLIP_H / 2);
/** Natural display width per thumbnail at FILMSTRIP_H */
const THUMB_DISPLAY_W = Math.round((THUMB_W / THUMB_H) * FILMSTRIP_H);
/** Hover preview display size (CSS pixels) */
const PREVIEW_DISPLAY_W = 480;
const PREVIEW_DISPLAY_H = 270;
/** Hover preview canvas resolution (1.5x display size for HiDPI) */
const PREVIEW_W = PREVIEW_DISPLAY_W * 1.5;
const PREVIEW_H = PREVIEW_DISPLAY_H * 1.5;
/** Gap between cursor and hover preview bottom edge */
const PREVIEW_GAP = 12;

/**
 * NLE-style layout: show as many thumbnails as fit at natural width,
 * evenly sampled from the generated pool.
 */
function thumbLayout(index: number, available?: number): { displayCount: number; widthPx: number; stride: number } {
    const clipPx = clipWidthPx(index);
    const pool = available ?? frameCount(effectiveDuration(index), clipPx);
    // Enough thumbnails at natural width to cover the clip (ceil to avoid trailing gap)
    const displayCount = Math.max(1, Math.ceil(clipPx / THUMB_DISPLAY_W));
    // Each thumbnail at its natural aspect-ratio width, no stretching
    const widthPx = THUMB_DISPLAY_W;
    // Stride through the pool. When pool < displayCount, stride < 1 repeats thumbnails.
    const stride = pool > 0 ? pool / displayCount : 1;
    return { displayCount, widthPx, stride };
}

interface VisibleThumb {
    url: string;
    leftPx: number;
    widthPx: number;
    key: number;
}

/** Scroll viewport pixel bounds with 1-viewport buffer on each side */
function viewportBounds(): { vpLeft: number; vpRight: number } {
    return {
        vpLeft: Math.max(0, scrollLeft.value - containerWidth.value),
        vpRight: scrollLeft.value + containerWidth.value * 2,
    };
}

/** Which display slots [start, end) fall within the viewport? */
function visibleSlotRange(clipLeftPx: number, slotWidth: number, slotCount: number): { start: number; end: number } {
    const { vpLeft, vpRight } = viewportBounds();
    return {
        start: Math.max(0, Math.floor((vpLeft - clipLeftPx) / slotWidth)),
        end: Math.min(slotCount, Math.ceil((vpRight - clipLeftPx) / slotWidth)),
    };
}

/** Snap slot to integer pixel bounds so adjacent slots tile without sub-pixel gaps */
function snapSlotPx(slot: number, slotWidth: number): { left: number; width: number } {
    const left = Math.round(slot * slotWidth);
    const right = Math.round((slot + 1) * slotWidth);
    return { left, width: right - left };
}

/**
 * Compute which thumbnails to render for a clip, considering:
 * - NLE-style sampling (stride through pool)
 * - Virtual rendering (only those near the scroll viewport)
 */
function visibleThumbs(clipIndex: number): VisibleThumb[] {
    const ts = thumbState.value[clipIndex];
    if (!ts || ts.thumbs.length === 0) return [];

    const clipPx = clipWidthPx(clipIndex);
    const displayCount = Math.max(1, Math.ceil(clipPx / THUMB_DISPLAY_W));
    const clipLeftPx = timeToPx(props.videos[clipIndex].timelineOffset);
    const { start, end } = visibleSlotRange(clipLeftPx, THUMB_DISPLAY_W, displayCount);
    const complete = !ts.generating;

    // Use the FINAL expected pool size for stride so each thumbnail maps to
    // its correct slot from the start. During progressive generation, slots
    // whose thumbnail hasn't been generated yet are simply skipped (skeleton
    // layer fills them). This prevents thumbnails appearing at wrong positions
    // then jumping when more arrive.
    const finalPool = complete ? ts.thumbs.length : frameCount(effectiveDuration(clipIndex), clipPx);
    const stride = finalPool / displayCount;
    const result: VisibleThumb[] = [];
    for (let slot = start; slot < end; slot++) {
        const poolIdx = Math.min(Math.round(slot * stride), finalPool - 1);
        if (poolIdx >= ts.thumbs.length) continue;
        const px = snapSlotPx(slot, THUMB_DISPLAY_W);
        result.push({ url: ts.thumbs[poolIdx].url, leftPx: px.left, widthPx: px.width, key: slot });
    }
    return result;
}

function visiblePrevThumbs(clipIndex: number): VisibleThumb[] {
    const thumbs = prevFilmstrip(clipIndex);
    if (thumbs.length === 0) return [];

    const clipPx = clipWidthPx(clipIndex);
    const clipLeftPx = timeToPx(props.videos[clipIndex].timelineOffset);
    const displayCount = Math.max(1, Math.ceil(clipPx / THUMB_DISPLAY_W));
    const widthPx = THUMB_DISPLAY_W;
    const stride = thumbs.length > 0 ? thumbs.length / displayCount : 1;
    const { start, end } = visibleSlotRange(clipLeftPx, widthPx, displayCount);

    const result: VisibleThumb[] = [];
    for (let slot = start; slot < end; slot++) {
        const poolIdx = Math.min(Math.round(slot * stride), thumbs.length - 1);
        const px = snapSlotPx(slot, widthPx);
        result.push({ url: thumbs[poolIdx].url, leftPx: px.left, widthPx: px.width, key: slot });
    }
    return result;
}

/** Visible skeleton slots with pre-computed pixel positions */
function visibleSkeletons(clipIndex: number): { left: number; width: number; key: number }[] {
    const layout = thumbLayout(clipIndex);
    const clipLeftPx = timeToPx(props.videos[clipIndex].timelineOffset);
    const { start, end } = visibleSlotRange(clipLeftPx, layout.widthPx, layout.displayCount);
    const result: { left: number; width: number; key: number }[] = [];
    for (let slot = start; slot < end; slot++) {
        const px = snapSlotPx(slot, layout.widthPx);
        result.push({ left: px.left, width: px.width, key: slot });
    }
    return result;
}

// ── Playback state ──
const playing = ref(false);
const stopped = ref(true);
const timelinePos = ref(0);
const playbackSpeed = ref(1);
const loopEnabled = ref(false);
const dragging = ref(false);

// Mirror internal state to shared composable so OutputPanel can read it
const {
    timelinePos: sharedTimelinePos,
    playing: sharedPlaying,
    breakpoints,
    hitBreakpointLine,
    hoveredBreakpointSec,
} = useVideoTimeline();
watch(timelinePos, (v) => {
    sharedTimelinePos.value = v;
});
watch(playing, (v) => {
    sharedPlaying.value = v;
});
/** Reactive version counter bumped by syncVideos to force template re-eval */
const syncVersion = ref(0);
let rafId = 0;

// ── Zoom + scroll state ──
const timelineInnerRef = ref<HTMLElement | null>(null);
const scrollContainerRef = ref<HTMLElement | null>(null);
const containerWidth = ref(800);
const zoomLevel = ref(1);
const scrollLeft = ref(0);
let resizeObserver: ResizeObserver | null = null;
const resizing = ref(false);
let resizeTimer = 0;

const timelineWidth = computed(() => containerWidth.value * zoomLevel.value);

// ── Computed ──
function effectiveDuration(index: number): number {
    const v = props.videos[index];
    // Prefer wallClockDuration from props (stable, available immediately).
    // Only use el.duration as fallback when wallClockDuration is unavailable.
    if (v.wallClockDuration > 0) return v.wallClockDuration;
    const el = videoRefs.value[index];
    return el?.duration && isFinite(el.duration) ? el.duration : 0;
}

const totalDuration = computed(() => {
    // Prefer lifecycle phase sum. Phases fill the timeline exactly.
    // Fall back to testDurationMs before lifecycle data arrives.
    const lc = props.lifecycle;
    let max =
        lc && lc.testMs + lc.artifactsMs + lc.cleanupMs > 0
            ? (lc.testMs + lc.artifactsMs + lc.cleanupMs) / 1000
            : props.testDurationMs != null && props.testDurationMs > 0
              ? props.testDurationMs / 1000
              : 0.1;
    for (let i = 0; i < props.videos.length; i++) {
        const end = props.videos[i].timelineOffset + effectiveDuration(i);
        if (end > max) max = end;
    }
    return max;
});

const allLoaded = computed(() => loadedCount.value + errorPaths.value.size >= props.videos.length);

// ── Pixel position helpers ──
function timeToPx(time: number): number {
    return totalDuration.value > 0 ? (time / totalDuration.value) * timelineWidth.value : 0;
}

function timeToPos(time: number): string {
    return timeToPx(time) + "px";
}

function timeToWidth(duration: number, offset: number): string {
    const width = Math.min(timeToPx(duration), timelineWidth.value - timeToPx(offset));
    return Math.max(width, 4) + "px";
}

function pxToTime(px: number): number {
    return timelineWidth.value > 0 ? (px / timelineWidth.value) * totalDuration.value : 0;
}

const playheadPx = computed(() => timeToPx(timelinePos.value));

// ── Visible range (for performance, only render ticks in viewport + buffer) ──
const visibleStart = computed(() => pxToTime(Math.max(0, scrollLeft.value - containerWidth.value)));
const visibleEnd = computed(() => pxToTime(scrollLeft.value + containerWidth.value * 2));

// ── Ruler ticks (adaptive to zoom and width) ──
const LABEL_MIN_SPACING_PX = 100; // minimum px between label centers
const rulerTicks = computed(() => {
    const dur = totalDuration.value;
    if (dur <= 0) return [];
    const tw = timelineWidth.value;
    const pxPerSec = tw / dur;

    // Pick the smallest 1-2-5 interval where labels won't overlap
    const minSec = LABEL_MIN_SPACING_PX / pxPerSec;
    const mag = 10 ** Math.floor(Math.log10(minSec));
    const norm = minSec / mag;
    const labelInterval = mag * (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10);
    const ticksPerLabel = 5;
    const tickInterval = labelInterval / ticksPerLabel;

    const ticks: { time: number; label: string | null; px: number; key: string }[] = [];
    const vStart = visibleStart.value;
    const vEnd = visibleEnd.value;
    const startStep = Math.max(0, Math.floor(vStart / tickInterval));
    const endStep = Math.ceil(Math.min(dur, vEnd) / tickInterval);
    for (let step = startStep; step <= endStep; step++) {
        const t = +(step * tickInterval).toFixed(10);
        const px = timeToPx(t);
        if (px >= tw) continue;
        const isLabel = step % ticksPerLabel === 0;
        ticks.push({ time: t, label: isLabel ? formatTimeRuler(t) : null, px, key: `${tickInterval}:${step}` });
    }

    return ticks;
});

// ── Video state ──
function videoState(index: number): "not_started" | "active" | "finished" | "error" {
    if (errorPaths.value.has(props.videos[index].path)) return "error";
    const v = props.videos[index];
    const dur = effectiveDuration(index);
    if (timelinePos.value < v.timelineOffset - 0.1) return "not_started";
    if (dur > 0 && timelinePos.value >= v.timelineOffset + dur) return "finished";
    return "active";
}

/** Whether the playhead is within this clip's time range (ignoring error state) */
function isPlayheadOverClip(index: number): boolean {
    const v = props.videos[index];
    const dur = effectiveDuration(index);
    if (dur <= 0) return true; // Unknown duration, treat as always active
    return timelinePos.value >= v.timelineOffset && timelinePos.value <= v.timelineOffset + dur;
}

function clipFullStyle(index: number) {
    void syncVersion.value;
    return clipStyle(index);
}

function clipStyle(index: number) {
    const v = props.videos[index];
    const dur = effectiveDuration(index);
    return { left: timeToPos(v.timelineOffset), width: timeToWidth(dur, v.timelineOffset) };
}

function clipWidthPx(index: number): number {
    const dur = effectiveDuration(index);
    return Math.max(timeToPx(dur), 4);
}

/** Lifecycle phases in execution order. Single source of truth for both output and timeline. */
const LIFECYCLE_PHASES: { key: keyof NonNullable<typeof props.lifecycle>; label: string; color: string }[] = [
    { key: "testMs", label: "test", color: "bg-info/40" },
    { key: "artifactsMs", label: "artifacts", color: "bg-secondary/35" },
    { key: "cleanupMs", label: "cleanup", color: "bg-warning/30" },
];

const lifecycleSegments = computed(() => {
    const lc = props.lifecycle;
    if (!lc) return [];
    const segments: { offset: number; duration: number; label: string; color: string }[] = [];
    let offset = 0;
    for (const phase of LIFECYCLE_PHASES) {
        const ms = lc[phase.key];
        if (ms > 0) {
            segments.push({ offset, duration: ms / 1000, label: phase.label, color: phase.color });
            offset += ms / 1000;
        }
    }
    return segments;
});

const GRAPH_H = 16; // SVG viewBox height for TPS graph

interface TpsGraphData {
    path: string;
    targetY: number;
    fillColor: string;
    strokeColor: string;
    peak: number;
    target: number;
}

/** Pre-compute TPS graph SVG data for a single video track. */
function tpsGraph(index: number): TpsGraphData | null {
    const vid = props.videos[index];
    const pts = vid?.statsPoints;
    if (!pts || pts.length === 0) return null;

    const values = pts.filter((p) => p.tps != null).map((p) => p.tps!);
    if (values.length === 0) return null;

    const target = vid.targetTps ?? DEFAULT_TARGET_TPS;
    const peak = Math.max(...values);
    const maxTps = Math.max(peak * TPS_PEAK_HEADROOM, target * TPS_TARGET_HEADROOM);
    const clipPx = clipWidthPx(index);
    const duration = effectiveDuration(index);
    if (clipPx <= 0 || duration <= 0) return null;

    const toX = (sec: number) => (sec / duration) * clipPx;
    const toY = (tps: number) => GRAPH_H - (tps / maxTps) * GRAPH_H;

    // Build SVG area path
    const segs: string[] = [];
    let firstX = 0,
        lastX = 0,
        started = false;
    for (const p of pts) {
        if (p.tps == null) continue;
        const x = toX(p.offsetSec),
            y = toY(p.tps);
        if (!started) {
            firstX = x;
            started = true;
        }
        lastX = x;
        segs.push(`${segs.length > 0 ? "L" : "M"} ${x.toFixed(1)} ${y.toFixed(1)}`);
    }
    if (!started) return null;
    segs.push(`L ${lastX.toFixed(1)} ${GRAPH_H} L ${firstX.toFixed(1)} ${GRAPH_H} Z`);

    // Target reference line Y. Uses same maxTps, guaranteed aligned.
    const targetY = GRAPH_H - (target / maxTps) * GRAPH_H;

    // Color: always use the canonical TPS teal for consistency with modal charts
    const base = METRIC_COLORS.tps;
    const fillColor = base.replace("rgb(", "rgba(").replace(")", ", 0.2)");
    const strokeColor = base.replace("rgb(", "rgba(").replace(")", ", 0.5)");

    return { path: segs.join(" "), targetY, fillColor, strokeColor, peak, target };
}

/** Pre-computed TPS graph data per video. */
const tpsGraphData = computed(() => props.videos.map((_, i) => tpsGraph(i)));

/** Snap to the nearest data point with non-null TPS for a given timeline time. */
function snappedTpsAt(index: number, timelineSec: number): { tps: number; target: number; offsetSec: number } | null {
    const vid = props.videos[index];
    const pts = vid?.statsPoints;
    if (!pts || pts.length === 0) return null;
    const offsetSec = timelineSec - vid.timelineOffset;
    if (offsetSec < 0 || offsetSec > vid.wallClockDuration) return null;
    let nearest: { offsetSec: number; tps: number } | null = null;
    let nearestDist = Number.POSITIVE_INFINITY;
    for (const p of pts) {
        if (p.tps == null) continue;
        const dist = Math.abs(p.offsetSec - offsetSec);
        if (dist < nearestDist) {
            nearestDist = dist;
            nearest = { offsetSec: p.offsetSec, tps: p.tps };
        }
    }
    if (!nearest) return null;
    return { tps: nearest.tps, target: vid.targetTps ?? DEFAULT_TARGET_TPS, offsetSec: nearest.offsetSec };
}

/** TPS at the currently hovered timeline position (snapped to nearest data point). */
const hoverTps = computed(() => {
    if (hoverTrack.value == null) return null;
    return snappedTpsAt(hoverTrack.value, hoverTime.value);
});

/** CSS percentage coordinates for the hover dot on a track's TPS graph. */
function tpsHoverPoint(index: number): { leftPct: number; topPct: number } | null {
    if (hoverTrack.value !== index) return null;
    const gd = tpsGraphData.value[index];
    if (!gd) return null;
    const dur = effectiveDuration(index);
    if (dur <= 0) return null;
    const snap = snappedTpsAt(index, hoverTime.value);
    if (!snap) return null;
    const maxTps = Math.max(gd.peak * TPS_PEAK_HEADROOM, gd.target * TPS_TARGET_HEADROOM);
    return {
        leftPct: (snap.offsetSec / dur) * 100,
        topPct: (1 - snap.tps / maxTps) * 100,
    };
}

/** Color class for a TPS value relative to its target. */
function tpsColorClass(value: number, target: number): string {
    const level = tpsLevel(value, target);
    if (level === "error") return "text-error";
    if (level === "warn") return "text-warning";
    return "text-success";
}

function onLoadedMetadata(path: string) {
    const index = props.videos.findIndex((v) => v.path === path);
    if (index === -1) return;
    const el = videoRefs.value[index];
    log.log(`onLoadedMetadata[${index}]`, {
        alreadyReady: videoLoadState.value.get(path) === "ready",
        src: el?.src?.slice(0, 80),
        duration: el?.duration,
        readyState: el?.readyState,
        networkState: el?.networkState,
        loadedCount: loadedCount.value,
        totalVideos: props.videos.length,
    });
    if (videoLoadState.value.get(path) === "ready") return;
    loadedCount.value++;
    videoLoadState.value.set(path, "ready");

    // Enqueue filmstrip generation (resumes from partial cache if available)
    queueThumbnails(index);
    syncVideos();
}
function onError(path: string) {
    const index = props.videos.findIndex((v) => v.path === path);
    if (index === -1) return;
    const el = videoRefs.value[index];
    log.log(`onError[${index}]`, {
        alreadyError: videoLoadState.value.get(path) === "error",
        src: el?.src?.slice(0, 80),
        error: el?.error,
        networkState: el?.networkState,
    });
    if (videoLoadState.value.get(path) === "error") return;
    errorPaths.value.add(path);
    videoLoadState.value.set(path, "error");
}
function isVideoLoading(index: number): boolean {
    const state = videoLoadState.value.get(props.videos[index].path);
    return state !== "ready" && state !== "error";
}

// ── Breakpoint markers (for timeline ruler) ──
const breakpointMarkers = computed(() => {
    const seen = new Set<number>();
    const markers: { sec: number; pos: string }[] = [];
    for (const sec of breakpoints.value.values()) {
        const snapped = snapBreakpointSec(sec);
        if (seen.has(snapped)) continue;
        seen.add(snapped);
        markers.push({ sec, pos: timeToPos(sec) });
    }
    return markers;
});

// ── Breakpoint detection ──
/** Line number of the breakpoint we just resumed from. Skip only this exact line, not others at the same timestamp. */
let _lastHitLine: number | null = null;

/** Find the earliest breakpoint in [prevPos, nextPos], skipping only _lastHitLine. */
function findNextBreakpoint(prevPos: number, nextPos: number): { lineNum: number; sec: number } | null {
    let best: { lineNum: number; sec: number } | null = null;
    for (const [lineNum, sec] of breakpoints.value) {
        if (lineNum === _lastHitLine) continue;
        if (sec >= prevPos && sec <= nextPos) {
            if (best === null || sec < best.sec || (sec === best.sec && lineNum < best.lineNum))
                best = { lineNum, sec };
        }
    }
    return best;
}

// ── Playback loop ──
function startPlaybackLoop() {
    let lastTime = performance.now();
    function tick(now: number) {
        if (!playing.value) return;
        const dt = (now - lastTime) / 1000;
        lastTime = now;
        if (!dragging.value) {
            const prevPos = timelinePos.value;
            const nextPos = Math.min(prevPos + dt * playbackSpeed.value, totalDuration.value);

            const hitBp = findNextBreakpoint(prevPos, nextPos);
            if (hitBp !== null) {
                timelinePos.value = hitBp.sec;
                _lastHitLine = hitBp.lineNum;
                hitBreakpointLine.value = hitBp.lineNum;
                syncVideos();
                autoScrollToPlayhead();
                pauseAll();
                return;
            }

            timelinePos.value = nextPos;
            syncVideos();
            autoScrollToPlayhead();
            if (timelinePos.value >= totalDuration.value) {
                if (loopEnabled.value && totalDuration.value > 0) {
                    timelinePos.value = 0;
                    _lastHitLine = null;
                    hitBreakpointLine.value = null;
                    syncVideos();
                    lastTime = now;
                } else {
                    pauseAll();
                    return;
                }
            }
        }
        rafId = requestAnimationFrame(tick);
    }
    lastTime = performance.now();
    rafId = requestAnimationFrame(tick);
}

function syncVideos() {
    const pos = timelinePos.value;
    for (let i = 0; i < props.videos.length; i++) {
        const v = props.videos[i];
        const el = videoRefs.value[i];
        if (!el || errorPaths.value.has(v.path)) continue;
        const state = videoState(i);
        const targetTime = Math.max(0, pos - v.timelineOffset);
        if (state === "active") {
            const threshold = dragging.value ? 0 : 0.1;
            if (Math.abs(el.currentTime - targetTime) > threshold) el.currentTime = targetTime;
            if (el.playbackRate !== playbackSpeed.value) el.playbackRate = playbackSpeed.value;
            if (el.paused && playing.value && !dragging.value) el.play().catch(() => {});
        } else {
            if (!el.paused) el.pause();
            if (state === "not_started") el.currentTime = 0;
            else if (state === "finished") el.currentTime = el.duration || 0;
        }
    }
    syncVersion.value++;
}

// ── Auto-scroll to keep playhead visible during playback ──
function autoScrollToPlayhead() {
    const container = scrollContainerRef.value;
    if (!container) return;
    const px = playheadPx.value;
    const left = container.scrollLeft;
    const width = container.clientWidth;
    const margin = width * 0.15;
    if (px < left + margin) {
        container.scrollLeft = px - margin;
    } else if (px > left + width - margin) {
        container.scrollLeft = px - width + margin;
    }
}

// ── Transport ──
function togglePlay() {
    if (playing.value) {
        playing.value = false;
        stopped.value = false;
        cancelAnimationFrame(rafId);
        for (const el of videoRefs.value) {
            if (el && !el.paused) el.pause();
        }
    } else {
        stopped.value = false;
        _lastHitLine = hitBreakpointLine.value;
        hitBreakpointLine.value = null;
        playing.value = true;
        syncVideos();
        startPlaybackLoop();
    }
}

function pauseAll() {
    playing.value = false;
    cancelAnimationFrame(rafId);
    for (const el of videoRefs.value) {
        if (el && !el.paused) el.pause();
    }
}

function stopPlayback() {
    pauseAll();
    _lastHitLine = null;
    hitBreakpointLine.value = null;
    stopped.value = true;
    timelinePos.value = 0;
    syncVideos();
}

function setSpeed(speed: number) {
    playbackSpeed.value = speed;
    for (const el of videoRefs.value) {
        if (el) el.playbackRate = speed;
    }
}

// ── Timeline seek (accounts for scroll) ──
function getTimeFromMouse(e: MouseEvent): number {
    const container = scrollContainerRef.value;
    if (!container) return 0;
    const rect = container.getBoundingClientRect();
    const x = e.clientX - rect.left + container.scrollLeft;
    return Math.max(0, Math.min(pxToTime(x), totalDuration.value));
}

// ── Hover preview ──
const hoverTrack = ref<number | null>(null);
const hoverTime = ref(0);
const hoverClientX = ref(0);
const hoverClientY = ref(0);
const previewVideoRefs = ref<(HTMLVideoElement | null)[]>([]);
const previewReady = ref<Set<string>>(new Set());

/** Hover position, follows cursor time for seamless scrubbing. */
const hoverInfo = computed<{ trackIndex: number; positionPx: number } | null>(() => {
    if (dragging.value || panning.value) return null;
    const idx = hoverTrack.value;
    if (idx == null) return null;
    const vid = props.videos[idx];
    const dur = effectiveDuration(idx);
    if (dur <= 0) return null;
    if (hoverTime.value < vid.timelineOffset || hoverTime.value > vid.timelineOffset + dur) return null;
    return { trackIndex: idx, positionPx: Math.round(timeToPx(hoverTime.value)) };
});

function onTrackMouseMove(e: MouseEvent, trackIndex: number) {
    hoverTrack.value = trackIndex;
    hoverTime.value = getTimeFromMouse(e);
    hoverClientX.value = e.clientX;
    hoverClientY.value = e.clientY;

    // Highlight the breakpoint marker (and its log row) when the cursor is within the
    // snap window. Derived from hoverTime so the markers themselves stay pointer-events-none.
    const snap = snapBreakpointSec(hoverTime.value);
    let matched: number | null = null;
    for (const bp of breakpointMarkers.value) {
        if (snapBreakpointSec(bp.sec) === snap) {
            matched = bp.sec;
            break;
        }
    }
    hoveredBreakpointSec.value = matched;

    // Seek preview video. Canvas repaints on @seeked (onPreviewSeeked → drawPreviewFrame).
    if (previewReady.value.has(props.videos[trackIndex].path)) {
        const vid = props.videos[trackIndex];
        const el = previewVideoRefs.value[trackIndex];
        if (el) {
            el.currentTime = Math.max(0, hoverTime.value - vid.timelineOffset);
        }
    }
}

function onTrackMouseLeave() {
    hoverTrack.value = null;
    hoveredBreakpointSec.value = null;
}

function onPreviewLoaded(path: string) {
    if (props.videos.findIndex((v) => v.path === path) === -1) return;
    previewReady.value.add(path);
    previewReady.value = new Set(previewReady.value);
}

function onPreviewSeeked(path: string) {
    const index = props.videos.findIndex((v) => v.path === path);
    if (index !== -1 && previewCanvasTrack === index) drawPreviewFrame();
}

/** Draw the preview video's current frame onto the hover canvas (called via ref callback) */
let previewCanvasCtx: CanvasRenderingContext2D | null = null;
let previewCanvasTrack = -1;

function mountPreviewCanvas(canvas: HTMLCanvasElement | null, trackIndex: number) {
    if (!canvas) {
        previewCanvasCtx = null;
        previewCanvasTrack = -1;
        return;
    }
    previewCanvasCtx = canvas.getContext("2d")!;
    previewCanvasTrack = trackIndex;
    drawPreviewFrame();
}

function drawPreviewFrame() {
    if (!previewCanvasCtx || previewCanvasTrack < 0) return;
    const el = previewVideoRefs.value[previewCanvasTrack];
    if (el && el.readyState >= 2) {
        previewCanvasCtx.drawImage(el, 0, 0, PREVIEW_W, PREVIEW_H);
    }
}

// ── Sidebar video controls ──
function requestFullscreen(index: number) {
    const el = videoRefs.value[index];
    if (!el) return;
    el.requestFullscreen?.().catch(() => {});
}

function navigateToInstance(vid: VideoItem) {
    if (vid.instanceId) emit("navigate-instance", vid.instanceId);
}

// ── Auto-scroll during drag ──
let dragRafId = 0;
let lastDragEvent: MouseEvent | null = null;
const panning = ref(false);
let cleanupDrag: (() => void) | null = null;

function dragAutoScroll() {
    const container = scrollContainerRef.value;
    if (!container || !lastDragEvent || !dragging.value) return;
    const rect = container.getBoundingClientRect();
    const cursorX = lastDragEvent.clientX;
    const scrollSpeed = 0.05;

    if (cursorX < rect.left) {
        const dist = Math.min(rect.left - cursorX, 200);
        container.scrollLeft -= dist * scrollSpeed;
    } else if (cursorX > rect.right) {
        const dist = Math.min(cursorX - rect.right, 200);
        container.scrollLeft += dist * scrollSpeed;
    }

    // Update position from current scroll + cursor
    timelinePos.value = getTimeFromMouse(lastDragEvent);
    syncVideos();
    dragRafId = requestAnimationFrame(dragAutoScroll);
}

function onTimelineMouseDown(e: MouseEvent) {
    e.preventDefault();
    // Middle mouse: pan
    if (e.button === 1) {
        e.preventDefault();
        panning.value = true;
        const container = scrollContainerRef.value;
        if (!container) return;
        const initialScrollLeft = container.scrollLeft;
        const initialX = e.clientX;
        document.body.style.cursor = "grabbing";
        const onMove = (ev: MouseEvent) => {
            container.scrollLeft = initialScrollLeft - (ev.clientX - initialX);
        };
        const onUp = () => {
            panning.value = false;
            document.body.style.cursor = "";
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            cleanupDrag = null;
        };
        document.addEventListener("mousemove", onMove);
        document.addEventListener("mouseup", onUp);
        cleanupDrag = onUp;
        return;
    }

    // Left mouse: seek + drag with auto-scroll
    if (e.button !== 0) return;
    dragging.value = true;
    _lastHitLine = null;
    hitBreakpointLine.value = null;
    lastDragEvent = e;

    const onMove = (ev: MouseEvent) => {
        lastDragEvent = ev;
        timelinePos.value = getTimeFromMouse(ev);
    };
    const onUp = () => {
        dragging.value = false;
        lastDragEvent = null;
        cancelAnimationFrame(dragRafId);
        seekTo(timelinePos.value);
        document.removeEventListener("mousemove", onMove);
        document.removeEventListener("mouseup", onUp);
        cleanupDrag = null;
    };
    document.addEventListener("mousemove", onMove);
    document.addEventListener("mouseup", onUp);
    cleanupDrag = onUp;
    seekTo(getTimeFromMouse(e));
    dragRafId = requestAnimationFrame(dragAutoScroll);
}

function seekTo(pos: number) {
    stopped.value = false;
    timelinePos.value = Math.max(0, Math.min(pos, totalDuration.value));
    syncVideos();
}

// ── Wheel: Ctrl+wheel = zoom, normal wheel = horizontal scroll ──
function onWheel(e: WheelEvent) {
    e.preventDefault();
    const container = scrollContainerRef.value;
    if (!container) return;

    if (e.ctrlKey || e.metaKey) {
        // Zoom centered on cursor
        const rect = container.getBoundingClientRect();
        const cursorX = e.clientX - rect.left;
        const cursorTime = pxToTime(cursorX + container.scrollLeft);

        const delta = e.deltaY > 0 ? 0.8 : 1.25;
        zoomLevel.value = Math.max(1, Math.min(20, zoomLevel.value * delta));

        nextTick(() => {
            const newPx = timeToPx(cursorTime);
            container.scrollLeft = newPx - cursorX;
        });
    } else {
        // Normal scroll → horizontal pan
        container.scrollLeft -= (e.deltaY || e.deltaX) * 1.5;
    }
}

function onScroll() {
    const container = scrollContainerRef.value;
    if (container) scrollLeft.value = container.scrollLeft;
}

// ── Download ──
async function downloadVideo(index: number) {
    const vid = props.videos[index];
    try {
        const res = await fetch(props.screenshotSrc(vid.path));
        const blob = await res.blob();
        const a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = `${vid.source}_recording.mp4`;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    } catch (e) {
        console.error("[Download] Failed for video", index, e);
    }
}

function downloadAll() {
    props.videos.forEach((_, i) => {
        downloadVideo(i);
    });
}

// ── Debug: detect stuck videos ──
let _stuckCheckTimer: ReturnType<typeof setInterval> | null = null;
function startStuckCheck() {
    if (_stuckCheckTimer) clearInterval(_stuckCheckTimer);
    _stuckCheckTimer = setInterval(() => {
        for (let i = 0; i < props.videos.length; i++) {
            const state = videoLoadState.value.get(props.videos[i].path);
            if (state === "ready" || state === "error") continue;
            const el = videoRefs.value[i];
            log.log(`STUCK CHECK[${i}]`, {
                state,
                hasElement: !!el,
                src: el?.src?.slice(0, 80) || "(no src)",
                readyState: el?.readyState,
                networkState: el?.networkState,
                paused: el?.paused,
                error: el?.error ? { code: el.error.code, message: el.error.message } : null,
                currentSrc: el?.currentSrc?.slice(0, 80),
            });
        }
    }, 5000);
}

// ── Keyboard: Space toggles play/pause ──
function onSpaceKey(e: KeyboardEvent) {
    if (e.key !== " " && e.code !== "Space") return;
    const target = e.target as HTMLElement | null;
    const tag = target?.tagName;
    if (tag === "INPUT" || tag === "TEXTAREA" || target?.isContentEditable) return;
    e.preventDefault();
    togglePlay();
}

// ── Lifecycle ──
onMounted(() => {
    _mountId++;
    startStuckCheck();
    window.addEventListener("keydown", onSpaceKey);
    log.log(`MOUNTED (gen=${_mountId})`, {
        videoCount: props.videos.length,
        paths: props.videos.map((v) => v.path?.slice(-40)),
        srcs: props.videos.map((v) => props.screenshotSrc(v.path)?.slice(0, 80)),
    });
    if (scrollContainerRef.value) {
        containerWidth.value = scrollContainerRef.value.clientWidth;
        resizeObserver = new ResizeObserver((entries) => {
            for (const entry of entries) containerWidth.value = entry.contentRect.width;
            resizing.value = true;
            clearTimeout(resizeTimer);
            resizeTimer = window.setTimeout(() => {
                resizing.value = false;
            }, 100);
        });
        resizeObserver.observe(scrollContainerRef.value);
    }
    if (timelineInnerRef.value) {
        timelineInnerRef.value.addEventListener("mousedown", onTimelineMouseDown);
    }
});

watch(
    () => props.videos,
    () => {
        _mountId++;
        log.log(`WATCH videos changed (gen=${_mountId})`, {
            videoCount: props.videos.length,
            paths: props.videos.map((v) => v.path?.slice(-40)),
            currentLoadState: Object.fromEntries(videoLoadState.value),
            currentRefCount: videoRefs.value.filter(Boolean).length,
        });
        cleanupDrag?.();

        // Prioritize thumbnails for the videos being viewed
        filmstrip.prioritize(props.videos.map((v) => v.path));

        // Reset preview state
        previewReady.value = new Set();
        previewCanvasCtx = null;
        previewCanvasTrack = -1;

        // Snapshot current filmstrip state for crossfade animation
        if (crossfadeTimer) clearTimeout(crossfadeTimer);
        const oldPrevVideos = _prevVideos;
        if (oldPrevVideos.length > 0) {
            const snapshot = new Map<number, Thumbnail[]>();
            for (let i = 0; i < oldPrevVideos.length; i++) {
                const cached = filmstrip.get(oldPrevVideos[i].path);
                if (cached?.length) snapshot.set(i, cached);
            }
            prevThumbnails.value = snapshot;
        }
        _prevVideos = [...props.videos];
        crossfading.value = true;
        nextTick(() => {
            crossfading.value = false;
        });
        crossfadeTimer = setTimeout(() => {
            prevThumbnails.value = new Map();
        }, 350);

        const newLoadState = new Map<string, "loading" | "ready" | "error">();
        const newErrors = new Set<string>();
        let newLoadedCount = 0;

        loadedCount.value = newLoadedCount;
        errorPaths.value = newErrors;
        videoLoadState.value = newLoadState;

        playing.value = false;
        stopped.value = true;
        timelinePos.value = 0;
        playbackSpeed.value = 1;
        cancelAnimationFrame(rafId);
        cancelAnimationFrame(dragRafId);
    },
);

onUnmounted(() => {
    log.log(`UNMOUNTED (gen=${_mountId})`, {
        loadState: Object.fromEntries(videoLoadState.value),
    });
    window.removeEventListener("keydown", onSpaceKey);
    if (_stuckCheckTimer) {
        clearInterval(_stuckCheckTimer);
        _stuckCheckTimer = null;
    }
    cleanupDrag?.();
    if (timelineInnerRef.value) {
        timelineInnerRef.value.removeEventListener("mousedown", onTimelineMouseDown);
    }
    cancelAnimationFrame(rafId);
    cancelAnimationFrame(dragRafId);
    resizeObserver?.disconnect();
    previewCanvasCtx = null;
    previewCanvasTrack = -1;
    sharedTimelinePos.value = 0;
    sharedPlaying.value = false;
});
</script>

<template>
  <div class="not-prose"
       :style="{ '--track-h': TRACK_H + 'px', '--track-pad-top': TRACK_PAD_TOP + 'px', '--track-pad-bot': TRACK_PAD_BOT + 'px' }">
    <!-- Hidden preview videos for hover frame inspection -->
    <template v-for="(vid, i) in videos" :key="'preview-' + vid.path">
      <video :ref="(el: any) => { previewVideoRefs[i] = el as HTMLVideoElement }"
             :src="screenshotSrc(vid.path)"
             preload="auto" muted class="hidden"
             @loadeddata="onPreviewLoaded(vid.path)"
             @seeked="onPreviewSeeked(vid.path)" />
    </template>
    <!-- NLE Timeline Editor -->
    <div v-if="videos.length > 0" class="rounded-lg overflow-hidden"
         @mouseleave="onTrackMouseLeave">

      <div class="flex">
        <!-- Left column (resizable: current time + video previews) -->
        <div class="w-[200px] flex-none bg-base-300">
          <div class="flex items-center justify-center bg-base-300 h-7">
            <span class="text-xs font-mono font-semibold text-primary tabular-nums">
              {{ formatTimePrecise(timelinePos) }}
            </span>
          </div>
          <div v-if="lifecycleSegments.length > 0"
               class="h-5 flex items-center px-2 border-t border-base-content/5 bg-base-300/80">
            <span class="text-[9px] text-base-content/30 font-medium tracking-wide uppercase">phases</span>
          </div>
          <div v-for="(vid, i) in videos" :key="'label-' + vid.path"
               class="h-[var(--track-h)] flex flex-col border-t border-base-content/5"
               :class="i % 2 === 0 ? 'bg-base-200' : 'bg-neutral'">
            <!-- Video preview -->
            <div class="video-preview relative flex-1 mx-2 mt-2 rounded overflow-hidden bg-black cursor-pointer group"
                 @click="videoState(i) !== 'error' && togglePlay()">
              <video :ref="(el: any) => { videoRefs[i] = el as HTMLVideoElement; if (el && (el as HTMLVideoElement).readyState >= 1) onLoadedMetadata(vid.path) }"
                     :src="screenshotSrc(vid.path)"
                     preload="metadata" muted
                     class="w-full h-full object-contain"
                     :class="videoState(i) === 'error' ? 'invisible' : ''"
                     @loadedmetadata="onLoadedMetadata(vid.path)"
                     @error="onError(vid.path)" />
              <!-- Sidebar toolbar (appears on hover) -->
              <div class="absolute top-1 right-1 flex gap-0.5 opacity-0 group-hover:opacity-100 transition-opacity z-10">
                <button @click.stop="requestFullscreen(i)"
                        class="w-5 h-5 flex items-center justify-center rounded bg-black/60 text-white/60 hover:text-white hover:bg-black/80"
                        title="Fullscreen">
                  <Icon icon="lucide:maximize" class="w-3 h-3" />
                </button>
              </div>
              <!-- State overlays -->
              <div v-if="videoState(i) === 'error'"
                   class="absolute inset-0 flex items-center justify-center pointer-events-none">
                <Icon icon="lucide:video-off" class="w-4 h-4 text-base-content/20" />
              </div>
              <div v-else-if="isVideoLoading(i) && !thumbState[i].thumbs.length"
                   class="absolute inset-0 flex items-center justify-center pointer-events-none">
                <div class="w-4 h-4 border-2 border-primary/30 border-t-primary rounded-full animate-spin gpu-accel" />
              </div>
              <div v-else-if="videoState(i) === 'not_started'"
                   class="absolute inset-0 bg-black/50 flex items-center justify-center pointer-events-none">
                <span class="text-[9px] text-white/40 font-medium tracking-wide uppercase">Not started</span>
              </div>
              <div v-else-if="videoState(i) === 'finished'"
                   class="absolute inset-0 bg-black/50 flex items-center justify-center pointer-events-none">
                <span class="text-[9px] text-white/40 font-medium tracking-wide uppercase">Finished</span>
              </div>
              <div v-else-if="!playing && !dragging && (allLoaded || thumbState[i].thumbs.length > 0)"
                   class="absolute inset-0 flex items-center justify-center pointer-events-none">
                <div class="w-7 h-7 rounded-full bg-black/40 flex items-center justify-center">
                  <Icon icon="lucide:play" class="w-3.5 h-3.5 text-white/80 ml-px" />
                </div>
              </div>

            </div>
            <!-- Label -->
            <div class="flex items-center px-2 py-1">
              <button class="badge badge-sm gap-1 cursor-pointer hover:badge-primary transition-colors"
                      :title="vid.instanceId ? `Navigate to ${vid.label || vid.source}` : undefined"
                      @click.stop="navigateToInstance(vid)">
                <Icon :icon="vid.source === 'server' ? 'lucide:server' : vid.source === 'client' ? 'lucide:monitor' : 'lucide:video'" class="w-2.5 h-2.5 flex-none" />
                {{ vid.label || vid.source }}
              </button>
            </div>
          </div>
        </div>

        <!-- Right column (scroll container for zoom) -->
        <div ref="scrollContainerRef"
             class="flex-1 border-l border-base-content/5 bg-base-200 overflow-x-scroll timeline-scroll"
             @wheel="onWheel"
             @scroll="onScroll">
          <!-- Wide inner div, width changes with zoom -->
          <div ref="timelineInnerRef"
               :style="{ width: (zoomLevel * 100) + '%' }"
               class="relative select-none overflow-clip"
               :class="[panning ? 'cursor-grabbing' : 'cursor-pointer', (resizing || dragging) ? 'no-timeline-transition' : '']">

            <!-- Ruler -->
            <div class="relative h-7 bg-base-300">
              <TransitionGroup name="tick">
                <div v-for="tick in rulerTicks" :key="tick.key"
                     class="absolute top-0 h-full ruler-tick"
                     :style="{ left: tick.px + 'px' }">
                  <span v-if="tick.label"
                        class="absolute top-[2px] text-[10px] text-base-content/60 tabular-nums whitespace-nowrap font-mono font-bold"
                        :class="tick.px === 0 ? 'left-0' : '-translate-x-1/2'">
                    {{ tick.label }}
                  </span>
                  <div class="absolute bottom-0 w-px bg-base-content/30" :class="tick.label ? 'h-2.5' : 'h-1.5'" />
                </div>
              </TransitionGroup>
            </div>

            <!-- Lifecycle bar -->
            <div v-if="lifecycleSegments.length > 0"
                 class="relative h-5 border-t border-base-content/5 bg-base-300/80">
              <div v-for="(seg, si) in lifecycleSegments" :key="si"
                   class="lifecycle-seg absolute top-1 bottom-1 rounded-sm"
                   :class="seg.color"
                   :style="{ left: timeToPos(seg.offset), width: timeToWidth(seg.duration, seg.offset) }"
                   :title="`${seg.label}: ${(seg.duration).toFixed(2)}s`" />
            </div>

            <!-- Tracks -->
            <div v-for="(vid, i) in videos" :key="'track-' + vid.path"
                 class="relative h-[var(--track-h)] border-t border-base-content/5"
                 :class="i % 2 === 0 ? 'bg-base-200' : 'bg-neutral'"
                 @mousemove="onTrackMouseMove($event, i)"
                 @mouseleave="onTrackMouseLeave">
              <!-- Clip container (waveform background + filmstrip overlay) -->
              <div class="clip-track absolute top-[var(--track-pad-top)] bottom-[var(--track-pad-bot)] rounded-md overflow-hidden flex flex-col"
                   :class="videoState(i) === 'error' ? 'bg-base-content/5' : 'bg-black/40'"
                   :style="clipFullStyle(i)">
                <!-- TPS graph (top half) -->
                <div class="relative flex-1 min-h-0 overflow-hidden">
                  <svg v-if="tpsGraphData[i]" class="absolute inset-0 w-full h-full" preserveAspectRatio="none"
                       :viewBox="`0 0 ${Math.max(1, Math.round(clipWidthPx(i)))} ${GRAPH_H}`">
                    <path :d="tpsGraphData[i]!.path"
                          :fill="tpsGraphData[i]!.fillColor" :stroke="tpsGraphData[i]!.strokeColor" stroke-width="0.5" />
                    <line :x1="0" :y1="tpsGraphData[i]!.targetY"
                          :x2="Math.round(clipWidthPx(i))" :y2="tpsGraphData[i]!.targetY"
                          class="stroke-base-content/15" stroke-width="0.3" stroke-dasharray="4 3" />
                  </svg>
                  <!-- TPS hover dot (matches Chart.js: hoverRadius 4, hoverBorderWidth 2, transparent center) -->
                  <div v-if="tpsHoverPoint(i)"
                       class="absolute rounded-full pointer-events-none"
                       :style="{
                         left: tpsHoverPoint(i)!.leftPct + '%',
                         top: tpsHoverPoint(i)!.topPct + '%',
                         transform: 'translate(-50%, -50%)',
                         width: '10px', height: '10px',
                         border: '2px solid ' + METRIC_COLORS.tps,
                         background: 'transparent',
                       }" />
                </div>
                <!-- Filmstrip (bottom half) -->
                <div class="relative flex-1 min-h-0 overflow-hidden">
                  <!-- Previous thumbnails fading out during crossfade -->
                  <div v-if="prevFilmstrip(i).length" class="absolute inset-0 thumb-layer"
                       :style="{ opacity: crossfading ? 1 : 0 }">
                    <div v-for="thumb in visiblePrevThumbs(i)" :key="'prev-' + thumb.key"
                         class="filmstrip-thumb-wrap"
                         :style="{ left: thumb.leftPx + 'px', width: thumb.widthPx + 'px' }">
                      <img :src="thumb.url" class="h-full w-full filmstrip-thumb" />
                    </div>
                  </div>
                  <!-- Current thumbnails -->
                  <div class="absolute inset-0 thumb-layer"
                       :style="{ opacity: crossfading ? 0 : 1 }">
                    <!-- Skeleton layer: visible during generation to fill gaps between partial thumbnails -->
                    <template v-if="thumbState[i].generating || !thumbState[i].thumbs.length">
                      <div v-for="skel in visibleSkeletons(i)" :key="'skel-' + skel.key"
                           class="absolute top-0 h-full bg-base-content/8"
                           :style="{ left: skel.left + 'px', width: skel.width + 'px' }" />
                    </template>
                    <!-- Thumbnail layer: each thumbnail renders at its correct timeline position -->
                    <div v-for="thumb in visibleThumbs(i)" :key="thumb.key"
                         class="filmstrip-thumb-wrap"
                         :style="{ left: thumb.leftPx + 'px', width: thumb.widthPx + 'px' }">
                      <img :src="thumb.url" class="h-full w-full filmstrip-thumb" />
                    </div>
                  </div>
                  <!-- Blur overlay: covers filmstrip until all thumbnails are complete -->
                  <div class="absolute inset-0 filmstrip-blur-overlay pointer-events-none"
                       :class="thumbState[i].complete ? 'is-complete' : ''" />
                  <div v-if="thumbState[i].generating"
                       class="absolute inset-0 flex items-center justify-center gap-2 pointer-events-none">
                    <div class="w-3 h-3 border-2 border-primary/30 border-t-primary rounded-full animate-spin gpu-accel" />
                    <span class="text-[10px] text-base-content/40 font-medium tracking-wide">Generating filmstrip…</span>
                  </div>
                </div>
                <!-- Active clip border overlay -->
                <div v-if="isPlayheadOverClip(i)"
                     class="absolute inset-0 rounded-md border-2 border-white pointer-events-none z-10" />
              </div>
            </div>

            <!-- Breakpoint markers (pointer-events-none so the cursor reaches the filmstrip
                 track underneath; hoveredBreakpointSec is derived from hoverTime in onTrackMouseMove). -->
            <div v-for="bp in breakpointMarkers" :key="bp.sec"
                 class="bp-marker absolute top-0 bottom-0 z-10 flex flex-col items-center pointer-events-none"
                 :style="{ left: bp.pos, transform: 'translateX(-50%)' }">
              <div class="w-1.5 h-1.5 rounded-full bg-primary flex-none mt-1" />
              <div class="flex-1 bp-marker-line" />
            </div>

            <!-- Playhead -->
            <div class="playhead absolute top-0 bottom-0 z-20 pointer-events-none flex flex-col items-center"
                 :style="{ left: timeToPos(timelinePos), transform: 'translateX(-50%)' }">
              <svg class="flex-none" width="14" height="18" viewBox="0 0 14 18">
                <path d="M 2 0 L 12 0 Q 14 0 14 2 L 14 12 L 7 18 L 0 12 L 0 2 Q 0 0 2 0 Z" fill="#ef4444" />
              </svg>
              <div class="w-0.5 flex-1 bg-red-500/80" />
            </div>
          </div>
        </div>
      </div>

      <!-- Controls bar -->
      <TimelineControls
        :playing="playing"
        :stopped="stopped"
        :loop="loopEnabled"
        :playback-speed="playbackSpeed"
        :zoom-level="zoomLevel"
        :timeline-pos="timelinePos"
        :total-duration="totalDuration"
        @toggle-play="togglePlay"
        @stop="stopPlayback"
        @toggle-loop="loopEnabled = !loopEnabled"
        @set-speed="setSpeed"
        @zoom-in="zoomLevel = Math.min(20, zoomLevel * 1.25)"
        @zoom-out="zoomLevel = Math.max(1, zoomLevel * 0.8)"
        @download-all="downloadAll"
      />
    </div>

    <!-- Hover preview, rendered at root level with fixed positioning to escape all overflow clipping -->
    <div v-if="hoverInfo && previewReady.has(videos[hoverInfo.trackIndex]?.path)"
         class="filmstrip-hover-preview pointer-events-none"
         :style="{ left: hoverClientX + 'px', top: (hoverClientY - PREVIEW_DISPLAY_H - PREVIEW_GAP) + 'px' }">
      <canvas :ref="(el: any) => mountPreviewCanvas(el as HTMLCanvasElement, hoverInfo?.trackIndex ?? -1)"
              class="w-full h-full" :width="PREVIEW_W" :height="PREVIEW_H" />
      <div class="absolute bottom-0 left-0 right-0 text-center pb-1">
        <span class="inline-block px-1.5 py-0.5 rounded bg-black/70 text-[10px] font-mono tabular-nums">
          <span class="text-base-content/80">{{ formatTimePrecise(hoverTime) }}</span>
          <template v-if="hoverTps">
            <span class="text-base-content/30 mx-1">|</span>
            <span :class="tpsColorClass(hoverTps.tps, hoverTps.target)">{{ hoverTps.tps.toFixed(1) }}</span>
            <span class="text-base-content/40"> tps</span>
          </template>
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.filmstrip-blur-overlay {
  backdrop-filter: blur(6px);
  background: rgba(0, 0, 0, 0.15);
  transition: opacity 400ms ease-out, backdrop-filter 400ms ease-out;
}
.filmstrip-blur-overlay.is-complete { opacity: 0; backdrop-filter: blur(0); }
.clip-track, .lifecycle-seg { transition: left 300ms ease-out, width 300ms ease-out; }
.ruler-tick { transition: left 300ms ease-out, opacity 300ms ease-out; }
.bp-marker { transition: left 300ms ease-out; }
.tick-enter-from { opacity: 0; }
.tick-leave-active { display: none; }
.no-timeline-transition .ruler-tick,
.no-timeline-transition .clip-track,
.no-timeline-transition .lifecycle-seg,
.no-timeline-transition .bp-marker { transition: none; }
.thumb-layer { transition: opacity 300ms ease-out; }
.filmstrip-thumb {
  image-rendering: pixelated;
  transform: translateZ(0); /* force own GPU compositing layer so pixelated is baked in */
}
.filmstrip-thumb-wrap {
  position: absolute;
  top: 0;
  height: 100%;
  overflow: hidden;
}
.filmstrip-hover-preview {
  position: fixed;
  width: v-bind(PREVIEW_DISPLAY_W + 'px');
  height: v-bind(PREVIEW_DISPLAY_H + 'px');
  z-index: 50;
  transform: translateX(-50%);
  border-radius: 6px;
  overflow: hidden;
  box-shadow: 0 8px 30px rgba(0, 0, 0, 0.7);
  border: 1px solid rgba(255, 255, 255, 0.1);
}

/* Custom thin scrollbar, no arrow buttons, matches dark theme */
.timeline-scroll {
  scrollbar-width: thin;
  scrollbar-color: rgba(198, 224, 222, 0.15) transparent;
}
.timeline-scroll::-webkit-scrollbar {
  height: 6px;
}
.timeline-scroll::-webkit-scrollbar-track {
  background: transparent;
}
.timeline-scroll::-webkit-scrollbar-thumb {
  background: rgba(198, 224, 222, 0.15);
  border-radius: 3px;
}
.timeline-scroll::-webkit-scrollbar-thumb:hover {
  background: rgba(198, 224, 222, 0.25);
}
.timeline-scroll::-webkit-scrollbar-button {
  display: none;
}

</style>

