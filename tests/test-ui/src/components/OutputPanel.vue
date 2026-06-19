<script setup lang="ts">
import { Icon } from "@iconify/vue";
import type { Ref } from "vue";
import { computed, nextTick, onMounted, onUnmounted, ref, watch, watchEffect } from "vue";
import { useAnnotationFilter } from "../composables/useAnnotationFilter";
import { useTestUI } from "../composables/useTestUI";
import { snapBreakpointSec, useVideoTimeline } from "../composables/useVideoTimeline";
import { useVncInteractive } from "../composables/useVncInteractive";
import type { AnnotationSource, InstanceSnapshot, OutputEntry } from "../types/state";
import type { VideoItem, VideoStatsPoint } from "../types/video";
import { formatDuration, shortTestName } from "../utils/format";
import { instanceStatusDotClass, instanceStatusLabel } from "../utils/instance-status";
import {
    anchorTimestampMs,
    annotationLevelClass,
    annotationSourceClass,
    annotationSourceIcon,
    formatEntryTimestamp,
    groupOutputEntries,
    isSuccessLine,
    nextTimestampMode,
    type OutputSegment,
    segmentLineOffset,
    type TimestampMode,
} from "../utils/output";
import { statusBgClass } from "../utils/status";
import EmptyState from "./EmptyState.vue";
import ImageLightbox from "./ImageLightbox.vue";
import MediaCard from "./MediaCard.vue";
import StatusIcon from "./StatusIcon.vue";
import SyncedVideos from "./SyncedVideos.vue";
import VncTile from "./VncTile.vue";

const { store, inspect } = useTestUI();
const { interactive: vncInteractive } = useVncInteractive();
const { timelinePos, breakpoints, hitBreakpointLine, hoveredBreakpointSec } = useVideoTimeline();
const {
    filter: sourceFilter,
    sources: filterSources,
    toggle: toggleSourceFilter,
    reset: resetSourceFilter,
    isFiltered: isSourceFiltered,
} = useAnnotationFilter();

const sourceLabels: Record<AnnotationSource, string> = {
    body: "Body",
    broker: "Broker",
    recording: "Recording",
    mod: "Mod",
    setup: "Setup",
};

/** Drop entries whose source isn't currently enabled in the filter chip bar. */
function applySourceFilter(entries: OutputEntry[] | null | undefined): OutputEntry[] {
    if (!entries?.length) {
        return [];
    }
    return entries.filter((e) => e.type !== "annotation" || sourceFilter[e.source]);
}
const copiedOutput = ref(false);
const copiedError = ref(false);
const copiedRepro = ref(false);
const copiedStep = ref(false);
const failedImages = ref<Set<string>>(new Set());
const loadedImages = ref<Set<string>>(new Set());
const timestampMode = ref<TimestampMode>("off");
const showInlineScreenshots = ref(true);
const copyTimers: ReturnType<typeof setTimeout>[] = [];
const outputSearch = ref("");
const outputSearchRef = ref<HTMLInputElement | null>(null);
const hoveredSource = ref<AnnotationSource | null>(null);

/** Check if a message matches the search query (case-insensitive). */
function lineMatchesSearch(message: string): boolean {
    if (!outputSearch.value) {
        return true;
    }
    return message.toLowerCase().includes(outputSearch.value.toLowerCase());
}

/** Whether to dim a line (doesn't match search). */
function searchDimClass(message: string): string {
    if (!outputSearch.value) {
        return "";
    }
    return lineMatchesSearch(message) ? "bg-primary/5" : "opacity-30";
}

function resolveInstance(id: string): InstanceSnapshot | undefined {
    return (
        (store.state.instances ?? []).find((i) => i.instanceId === id) ??
        store.stoppedInstances.find((i) => i.instanceId === id)
    );
}

function resolveInstanceLabel(id: string): string {
    return resolveInstance(id)?.label ?? id;
}

function resolveInstanceType(id: string): "server" | "client" {
    return resolveInstance(id)?.instanceType ?? (id.startsWith("server") ? "server" : "client");
}

function navigateToInstance(id: string) {
    inspect.openInspect(id);
}

function inlineConnectionCount(inst: InstanceSnapshot): number | undefined {
    if (inst.instanceType !== "server") {
        return undefined;
    }
    const all = store.state.instances ?? [];
    let n = 0;
    for (const c of all) {
        if (c.instanceType === "client" && c.connectedServerId === inst.instanceId) {
            n++;
        }
    }
    return n;
}

function containerBadgeDotClass(id: string): string {
    const inst = resolveInstance(id);
    if (!inst) {
        return instanceStatusDotClass("idle", false, true, "sm");
    }
    const stopped = store.runDone || inst.disposed;
    return instanceStatusDotClass(inst.status, inst.connected, stopped, "sm");
}

function containerBadgeStatusLabel(id: string): string {
    const inst = resolveInstance(id);
    if (!inst) {
        return "unknown";
    }
    const stopped = store.runDone || inst.disposed;
    return instanceStatusLabel(inst.status, inst.connected, stopped, true);
}

function inlineConnectedServerLabel(inst: InstanceSnapshot): string | null | undefined {
    if (inst.instanceType === "server") {
        return undefined;
    }
    const id = inst.connectedServerId;
    if (!id) {
        return null;
    }
    const all = [...(store.state.instances ?? []), ...store.stoppedInstances];
    return all.find((i) => i.instanceId === id && i.instanceType === "server")?.label ?? id;
}

// The test objects inside collections are NOT deeply reactive (shallowRef).
// selectedTestVersion bumps whenever the selected test's properties change.
// Reading it here makes `test` re-evaluate on every content change, so all
// downstream computeds (output, videos, errors, etc.) react automatically.
const test = computed(() => {
    void store.selectedTestVersion.value;
    return store.selectedTest;
});
const contentVersion = computed(() => `${test.value?.displayName ?? ""}:${store.selectedTestVersion.value}`);
const step = computed(() => store.selectedStep);
const error = computed(() => store.selectedError);

const filteredTestOutput = computed(() => applySourceFilter(test.value?.output));
const filteredStepOutput = computed(() => applySourceFilter(step.value?.output));

const outputAnchorMs = computed(() => anchorTimestampMs(test.value?.output));
const stepAnchorMs = computed(() => anchorTimestampMs(step.value?.output));
const timestampTooltip = computed(() =>
    timestampMode.value === "off"
        ? "Show absolute timestamps"
        : timestampMode.value === "absolute"
          ? "Show relative timestamps"
          : "Hide timestamps",
);

const testVncInstances = computed((): InstanceSnapshot[] => {
    const ids = test.value?.usedInstances ?? [];
    return ids.map((id) => resolveInstance(id)).filter((i): i is InstanceSnapshot => !!i && !!i.vncUrl);
});

const vncButtonEnabled = computed(() => testVncInstances.value.length > 0);
const vncButtonTooltip = computed(() =>
    vncButtonEnabled.value ? "Toggle live VNC viewers" : "No VNC available for this test",
);

// VNC viewer state
const vncExpanded = ref(false);

// Collapse inline VNC when run ends
watchEffect(() => {
    if (store.runDone) {
        vncExpanded.value = false;
    }
});

const hasError = computed(() => {
    void contentVersion.value;
    return test.value?.errorMessage || test.value?.stackTrace;
});

/** True when recording is enabled in the run (any instance has a completed recording setup step). */
const recordingEnabled = computed(() => {
    const allInstances = [...(store.state.instances ?? []), ...store.stoppedInstances];
    return allInstances.some((i) =>
        i.setupSteps?.some((s) => s.step === "Starting video recording" && s.status === "completed"),
    );
});

// Default inline screenshots off when video recording is enabled (screenshots are redundant).
// Keep watching until recordingEnabled becomes true (instances may not exist at mount time).
let stopScreenshotWatch: ReturnType<typeof watch> | undefined;
stopScreenshotWatch = watch(
    recordingEnabled,
    (enabled) => {
        if (enabled) {
            showInlineScreenshots.value = false;
            stopScreenshotWatch?.();
        }
    },
    { immediate: true },
);

/** True when the test should show a recording placeholder (test done, no recordings yet, recording enabled). */
const showRecordingPlaceholder = computed(() => {
    // Read directly from store to bypass stale shallow-ref issues
    const t = store.selectedTest;
    if (!t) {
        return false;
    }
    const isDone = t.status === "passed" || t.status === "failed" || t.status === "canceled";
    const hasRecordings = t.recordings && t.recordings.length > 0;
    if (hasRecordings) {
        return false;
    }
    // If we have skip reasons, we *want* to show the placeholder (so the user
    // sees the explanation) even if recording is globally disabled — the skip
    // event itself is the contract that something happened.
    if (t.recordingSkipReasons && Object.keys(t.recordingSkipReasons).length > 0) {
        return isDone;
    }
    return isDone && recordingEnabled.value;
});

/**
 * Per-source placeholder cards. Reads `recordingSkipReasons` and produces one
 * entry per source slug (`'server'`, `'client'`, `'client_2'`, …). Indexed
 * client lookups fall back to the un-indexed `'client'` entry — class-level
 * skips (artifacts_opted_out, retention_passed) only emit the un-indexed key
 * but apply to every client card.
 */
type PlaceholderCard = { source: string; label: string; reason: string | null };
const placeholderCards = computed((): PlaceholderCard[] => {
    const t = store.selectedTest;
    if (!t) {
        return [];
    }
    const reasons = t.recordingSkipReasons ?? {};
    const sources = Object.keys(reasons);
    // No skip events — fall back to a single generic card. The placeholder
    // template's text picks the wording (eternal-pending / lost / etc.).
    if (sources.length === 0) {
        return [{ source: "server", label: "server · client", reason: null }];
    }
    // Stable order: server first, then clients in numeric order.
    const sorted = [...sources].sort((a, b) => {
        if (a === "server") {
            return -1;
        }
        if (b === "server") {
            return 1;
        }
        return a.localeCompare(b, undefined, { numeric: true });
    });
    return sorted.map((source) => ({
        source,
        label: source.replace("_", " "),
        reason: lookupSkipReason(reasons, source),
    }));
});

/** Per-source lookup: indexed first, then un-indexed `'client'` fallback. */
function lookupSkipReason(reasons: Record<string, string>, source: string): string | null {
    if (reasons[source]) {
        return reasons[source];
    }
    if (source.startsWith("client") && reasons.client) {
        return reasons.client;
    }
    return null;
}

/**
 * Skip cards rendered alongside captured videos (partial-skip case).
 * Filters `placeholderCards` down to sources that don't already have a
 * recording — relevant when only some sources were skipped (e.g. server
 * opted-out via Artifacts=false but client was leased and recorded).
 */
const missingSourcePlaceholders = computed((): PlaceholderCard[] => {
    const t = store.selectedTest;
    if (!t?.recordingSkipReasons) {
        return [];
    }
    const captured = new Set((t.recordings ?? []).map((r) => r.source));
    return placeholderCards.value.filter((card) => !captured.has(card.source) && card.reason !== null);
});

type PlaceholderCopy = { text: string; code?: string; detail?: string };

/** Map a snake_case skip reason to UI-facing copy. */
function placeholderCopy(reason: string | null, source: string): PlaceholderCopy {
    switch (reason) {
        case "artifacts_opted_out":
            return {
                text: "No recording",
                code: "[TestServer(Artifacts = false)]",
                detail: "Screenshots and video skipped on pass; available on failure.",
            };
        case "retention_passed":
            return { text: "Recording skipped", detail: "Only saved on failure." };
        case "end_time_missing":
        case "recorder_never_started":
        case "recorder_missing":
        case "zero_duration":
            return { text: "Recording lost", detail: "See infrastructure log." };
        case "extraction_failed":
        case "finalize_deferred_failed":
            return { text: "Recording extraction failed", detail: "See infrastructure log." };
        case null:
            return placeholderFallbackCopy(source);
        default:
            return { text: "Recording missing", detail: "See infrastructure log." };
    }
}

/** When there's no skip event at all, decide between pending and lost based on time. */
function placeholderFallbackCopy(_source: string): PlaceholderCopy {
    const t = store.selectedTest;
    if (!t) {
        return { text: "Recording pending…" };
    }
    // If the test finished < 30s ago, treat as still-pending; otherwise as lost.
    const finishedRaw = t.runningStartTime ? new Date(t.runningStartTime).getTime() + (t.durationMs ?? 0) : null;
    if (finishedRaw == null) {
        return { text: "Recording pending…" };
    }
    const ageMs = Date.now() - finishedRaw;
    return ageMs < 30_000
        ? { text: "Recording pending…" }
        : { text: "Recording missing", detail: "See infrastructure log." };
}

// Reset copy state when test selection changes (VNC state is kept global)
watch(test, () => {
    copiedOutput.value = false;
    copiedError.value = false;
    copiedRepro.value = false;
    lightboxOpen.value = false;
});

// Reset copy state when step selection changes
watch(step, () => {
    copiedStep.value = false;
});

function screenshotSrc(path: string): string {
    return store.screenshotSrc(path);
}

// Plain text output for copy-to-clipboard. Includes timestamps when the toggle is on.
// Honors the active source-filter chips so the copy matches what's on screen.
const plainOutput = computed(() => {
    const entries = filteredTestOutput.value;
    if (!entries.length) {
        return "";
    }
    return entries
        .filter((e): e is Extract<OutputEntry, { type: "annotation" }> => e.type === "annotation")
        .map((e) => {
            const tag = formatEntryTimestamp(e.ts, timestampMode.value, outputAnchorMs.value);
            return tag ? `${tag} ${e.message}` : e.message;
        })
        .join("\n");
});

const errorText = computed(() => {
    const parts: string[] = [];
    if (test.value?.errorType) {
        parts.push(test.value.errorType);
    }
    if (test.value?.errorMessage) {
        parts.push(test.value.errorMessage);
    }
    if (test.value?.stackTrace) {
        parts.push("");
        parts.push(test.value.stackTrace);
    }
    return parts.join("\n");
});

function scheduleCopyReset(flag: Ref<boolean>) {
    flag.value = true;
    const id = setTimeout(() => (flag.value = false), 2000);
    copyTimers.push(id);
}

async function copyOutput() {
    await navigator.clipboard.writeText(plainOutput.value);
    scheduleCopyReset(copiedOutput);
}

async function copyError() {
    await navigator.clipboard.writeText(errorText.value);
    scheduleCopyReset(copiedError);
}

async function copyRepro() {
    if (!test.value?.reproCommand) {
        return;
    }
    await navigator.clipboard.writeText(test.value.reproCommand);
    scheduleCopyReset(copiedRepro);
}

async function copyStepOutput() {
    await navigator.clipboard.writeText(stepPlainOutput.value);
    scheduleCopyReset(copiedStep);
}

onUnmounted(() => {
    for (const id of copyTimers) {
        clearTimeout(id);
    }
});

const outputSegments = computed((): OutputSegment[] => {
    // Read contentVersion to re-evaluate when output is appended.
    // test.value is a shallowRef object -- same reference even after mutation --
    // so Vue's computed cache won't invalidate without this explicit dependency.
    void contentVersion.value;
    return groupOutputEntries(filteredTestOutput.value);
});

/** Seconds elapsed from test body start (runningStartTime) to the given ISO timestamp.
 *  Returns null if either timestamp is unavailable. */
function secSinceTestStart(isoTimestamp: string | undefined | null): number | null {
    const start = test.value?.runningStartTime;
    if (!isoTimestamp || !start) {
        return null;
    }
    return (new Date(isoTimestamp).getTime() - new Date(start).getTime()) / 1000;
}

/** Find the instance that produced a video recording, matching by source name to instanceType. */
function findInstanceForVideo(video: VideoItem): string | null {
    const ids = test.value?.usedInstances ?? [];
    for (const id of ids) {
        const inst = resolveInstance(id);
        if (inst && inst.instanceType === video.source) {
            return id;
        }
        if (id.startsWith(video.source)) {
            return id;
        }
    }
    return null;
}

/** Extract TPS stats points for a video's time window from instance stats history.
 *  Extends to clip edges via sample-and-hold so the graph spans the full width. */
function extractVideoStats(
    video: VideoItem,
    instanceId: string,
): { points: VideoStatsPoint[]; targetTps: number | null } {
    const history = store.instanceStatsHistory.get(instanceId) ?? [];
    if (history.length === 0) {
        return { points: [], targetTps: null };
    }

    const videoStartSec = video.timelineOffset;
    const videoEndSec = videoStartSec + video.wallClockDuration;

    let targetTps: number | null = null;
    let lastBefore: { tps: number | null } | null = null;
    const points: VideoStatsPoint[] = [];

    for (const entry of history) {
        const entrySec = secSinceTestStart(entry.timestamp);
        if (entrySec === null) {
            continue;
        }
        if (entry.targetTps != null) {
            targetTps = entry.targetTps;
        }

        if (entrySec <= videoStartSec) {
            lastBefore = entry;
            continue;
        }
        if (entrySec > videoEndSec) {
            break; // history is chronological
        }

        points.push({ offsetSec: entrySec - videoStartSec, tps: entry.tps });
    }

    if (points.length === 0 && lastBefore === null) {
        return { points: [], targetTps: null };
    }

    // Extend to left edge (0s) using last value before window or first in-range value
    const leftTps = lastBefore?.tps ?? points[0]?.tps ?? null;
    if (points.length === 0 || points[0].offsetSec > 0) {
        points.unshift({ offsetSec: 0, tps: leftTps });
    }

    // Extend to right edge (full duration) using last in-range value
    const lastPoint = points[points.length - 1];
    if (lastPoint.offsetSec < video.wallClockDuration) {
        points.push({ offsetSec: video.wallClockDuration, tps: lastPoint.tps });
    }

    return { points, targetTps };
}

/** Video recordings from test metadata. Sorted by timeline offset (server first).
 *  Enriched with TPS stats from instance stats history for timeline graph display.
 *  Keeps the previous test's videos during switches so SyncedVideos stays mounted
 *  and can crossfade instead of unmount→remount. Clears only when switching to a
 *  test that will never have recordings (no recording enabled). */
const videoItems = computed((): VideoItem[] => {
    const t = test.value;
    // Only react to stats updates while the test is still active.
    // For finished tests, stats are frozen. Avoid recomputation from other instances' stats.
    const status = t?.status;
    if (status === "running" || status === "queued") {
        void store.statsVersion.value;
    }

    const recs = t?.recordings;
    if (!recs?.length) {
        return [];
    }
    return [...recs]
        .sort((a, b) => a.timelineOffset - b.timelineOffset)
        .map((rec) => {
            const instanceId = findInstanceForVideo(rec);
            if (!instanceId) {
                return rec;
            }
            const enriched = { ...rec, instanceId, label: resolveInstanceLabel(instanceId) };
            const { points, targetTps } = extractVideoStats(rec, instanceId);
            if (points.length === 0) {
                return enriched;
            }
            return { ...enriched, statsPoints: points, targetTps };
        });
});
const stableVideoItems = ref<VideoItem[]>([]);
const stableTestDurationMs = ref<number | null>(null);
const stableLifecycle = ref<{ testMs: number; cleanupMs: number; artifactsMs: number } | null>(null);
watch(
    videoItems,
    (items) => {
        const prev = stableVideoItems.value;
        if (
            prev.length === items.length &&
            prev.every((p, i) => p.path === items[i].path && p.statsPoints?.length === items[i].statsPoints?.length)
        ) {
            return; // No structural change, skip update
        }
        stableVideoItems.value = items;
        stableTestDurationMs.value = test.value?.durationMs ?? null;
    },
    { immediate: true },
);
// Lifecycle arrives after recordings; watch separately
watch(
    () => test.value?.lifecycle,
    (lc) => {
        stableLifecycle.value = lc ?? null;
    },
    { immediate: true },
);

// ── Timeline-linked output dots ──
const hasVideos = computed(() => stableVideoItems.value.length > 0);

/** Convert an output entry's ISO timestamp to seconds since test body start (runningStartTime). */
function lineTimelineSec(entry: { ts?: string }): number | null {
    const sec = secSinceTestStart(entry.ts);
    return sec !== null ? Math.max(0, sec) : null;
}

/** Whether the timeline playhead has reached this output line's timestamp. */
function lineReached(entry: { ts?: string }): boolean {
    const sec = lineTimelineSec(entry);
    if (sec === null) {
        return false;
    }
    // Strict > for lines at position 0: lines emitted before or at runningStartTime
    // get clamped to sec=0 by lineTimelineSec, so >= would mark them as reached
    // even when the playhead hasn't moved from its initial 0 position.
    return sec === 0 ? timelinePos.value > 0 : timelinePos.value >= sec;
}

// ── Breakpoints ──

function toggleBreakpoint(lineNum: number, entry: { ts?: string }) {
    const sec = lineTimelineSec(entry);
    if (sec === null) {
        return;
    }
    const next = new Map(breakpoints.value);
    if (next.has(lineNum)) {
        next.delete(lineNum);
        if (hitBreakpointLine.value === lineNum) {
            hitBreakpointLine.value = null;
        }
    } else {
        next.set(lineNum, sec);
    }
    breakpoints.value = next;
}

function toggleAllBreakpoints() {
    if (breakpoints.value.size > 0) {
        breakpoints.value = new Map();
        hitBreakpointLine.value = null;
        return;
    }
    const next = new Map<number, number>();
    let lineNum = 0;
    for (const seg of outputSegments.value) {
        if (seg.type === "lines") {
            for (const entry of seg.items) {
                lineNum++;
                const sec = lineTimelineSec(entry);
                if (sec !== null) {
                    next.set(lineNum, sec);
                }
            }
        }
    }
    breakpoints.value = next;
}

const hasAnyBreakpoints = computed(() => breakpoints.value.size > 0);

const outputSegmentOffsets = computed(() => {
    const offsets: number[] = [];
    let offset = 0;
    for (const seg of outputSegments.value) {
        offsets.push(offset);
        if (seg.type === "lines") {
            offset += seg.items.length;
        }
    }
    return offsets;
});

const lineBreakpointState = computed(() => {
    const map = new Map<number, "hit" | "hovered" | "set">();
    const hoverSnap = hoveredBreakpointSec.value !== null ? snapBreakpointSec(hoveredBreakpointSec.value) : null;
    for (const [lineNum, sec] of breakpoints.value) {
        if (hitBreakpointLine.value === lineNum) {
            map.set(lineNum, "hit");
        } else if (hoverSnap !== null && snapBreakpointSec(sec) === hoverSnap) {
            map.set(lineNum, "hovered");
        } else {
            map.set(lineNum, "set");
        }
    }
    return map;
});

function resetBreakpoints() {
    breakpoints.value = new Map();
    hitBreakpointLine.value = null;
}
watch(test, resetBreakpoints);
// Toggling a source chip changes the line numbering; reset breakpoints so they
// don't end up pointing at the wrong rows.
watch(sourceFilter, resetBreakpoints, { deep: true });

watch(hitBreakpointLine, (ln) => {
    if (ln === null) {
        return;
    }
    nextTick(() => {
        document.querySelector("[data-breakpoint-hit]")?.scrollIntoView({ behavior: "smooth", block: "center" });
    });
});

/** Step output grouped into segments. Setup-step "details" are stored as
 *  annotations with source='setup', so the same grouping/rendering path applies. */
const stepSegments = computed((): OutputSegment[] => {
    return groupOutputEntries(filteredStepOutput.value);
});

const stepPlainOutput = computed(() => {
    const entries = filteredStepOutput.value;
    if (!entries.length) {
        return "";
    }
    return entries
        .filter((e): e is Extract<OutputEntry, { type: "annotation" }> => e.type === "annotation")
        .map((e) => {
            const tag = formatEntryTimestamp(e.ts, timestampMode.value, stepAnchorMs.value);
            return tag ? `${tag} ${e.message}` : e.message;
        })
        .join("\n");
});

// Lightbox state
const lightboxOpen = ref(false);
const lightboxInitialIndex = ref(0);

// Flat list of all screenshots for arrow navigation
const allScreenshots = computed(() => {
    const list: { src: string; alt: string; source: string }[] = [];
    for (const seg of outputSegments.value) {
        if (seg.type === "images") {
            for (const img of seg.items) {
                list.push({ src: screenshotSrc(img.path), alt: `${img.source} screenshot`, source: img.source });
            }
        }
    }
    return list;
});

function openLightbox(src: string) {
    lightboxInitialIndex.value = allScreenshots.value.findIndex((s) => s.src === src);
    if (lightboxInitialIndex.value < 0) {
        lightboxInitialIndex.value = 0;
    }
    lightboxOpen.value = true;
}

function onSearchShortcut(e: KeyboardEvent) {
    if (lightboxOpen.value) {
        return;
    }
    const tag = (e.target as HTMLElement)?.tagName;
    if (e.key === "/" && tag !== "INPUT" && tag !== "TEXTAREA") {
        e.preventDefault();
        outputSearchRef.value?.focus();
    }
}

onMounted(() => window.addEventListener("keydown", onSearchShortcut));
onUnmounted(() => window.removeEventListener("keydown", onSearchShortcut));
</script>

<template>
  <div class="flex flex-col h-full">
    <!-- Empty state -->
    <EmptyState v-if="!test && !step && !error" label="Select a test to view details" class="flex-1" />

    <!-- Run error details -->
    <template v-else-if="error">
      <div class="flex items-center gap-2.5 px-5 py-2.5 bg-base-200/50 border-b border-base-content/5 flex-none">
        <StatusIcon status="failed" :size="16" />
        <div class="flex-1 min-w-0">
          <span class="font-mono text-[13px] font-medium truncate block">Run Error</span>
          <span class="font-mono text-[11px] text-base-content/40 truncate block">
            {{ error.timestamp }}
          </span>
        </div>
      </div>
      <div class="flex-1 overflow-auto p-6">
        <div class="bg-error/6 border border-error/10 rounded-lg p-4">
          <div class="text-sm text-error font-mono whitespace-pre-wrap leading-relaxed">
            {{ error.message }}
          </div>
          <pre v-if="error.stackTrace"
               class="font-mono code-block whitespace-pre-wrap text-error/40 mt-3 leading-relaxed">{{ error.stackTrace }}</pre>
        </div>
      </div>
    </template>

    <!-- Setup step output -->
    <template v-else-if="step">
      <div class="flex items-center gap-4 px-8 py-5 flex-none bg-base-200/30">
        <StatusIcon :status="step.status" :size="32" />
        <div class="flex-1 min-w-0">
          <span class="text-xl font-bold truncate block" :title="step.step">
            {{ step.step }}
          </span>
          <span class="text-[11px] text-base-content/40 truncate block mt-0.5">
            Setup step
          </span>
        </div>
      </div>

      <div class="status-stripe" :class="statusBgClass(step.status)" />

      <div class="flex-1 overflow-auto p-6">
        <div v-if="stepSegments.length > 0" class="rounded-lg border border-base-content/5 bg-base-200 overflow-hidden">
          <div class="flex items-center gap-2 px-4 py-2 border-b border-base-content/5">
            <Icon icon="lucide:terminal" class="w-3.5 h-3.5 text-base-content/40" />
            <span class="text-xs text-base-content/40 flex-1">Step Output</span>
            <button class="btn btn-ghost btn-xs px-1.5 text-base-content/30 hover:text-base-content/60"
                    :class="timestampMode !== 'off' ? 'text-info' : ''"
                    :title="timestampTooltip"
                    @click="timestampMode = nextTimestampMode(timestampMode)">
              <Icon icon="lucide:clock" class="w-3.5 h-3.5" />
            </button>
            <button v-if="stepPlainOutput"
                    class="btn btn-ghost btn-xs gap-1 text-base-content/30 hover:text-base-content/60"
                    @click="copyStepOutput">
              <Icon v-if="!copiedStep" icon="lucide:copy" class="w-3.5 h-3.5" />
              <Icon v-else icon="lucide:check" class="w-3.5 h-3.5 text-success" />
              <span :class="copiedStep ? 'text-success font-medium' : ''">{{ copiedStep ? 'Copied!' : 'Copy' }}</span>
            </button>
          </div>
          <div class="console-block font-mono code-block p-4 overflow-x-auto">
            <table class="w-full border-collapse">
              <template v-for="(seg, i) in stepSegments" :key="i">
                <template v-if="seg.type === 'lines'">
                  <tr v-for="(entry, li) in seg.items" :key="`${i}-${li}`">
                    <td v-if="hasVideos" class="w-5 min-w-5 max-w-5 pr-0 align-top text-center">
                      <span class="inline-block w-1.5 h-1.5 rounded-full mt-[7px] transition-colors duration-300"
                            :class="lineReached(entry) ? 'bg-success' : 'bg-base-content/15'" />
                    </td>
                    <td class="text-right pr-4 select-none text-base-content/20 align-top w-[1%] whitespace-nowrap">{{ segmentLineOffset(stepSegments, i) + li + 1 }}</td>
                    <td class="pr-2 align-top w-[1%]">
                      <Icon :icon="annotationSourceIcon(entry.source)" class="w-3 h-3 mt-[3px]" :class="annotationSourceClass(entry.source)" />
                    </td>
                    <td v-if="timestampMode !== 'off'" class="text-right pr-1 text-base-content/40 align-top whitespace-nowrap tabular-nums">{{ formatEntryTimestamp(entry.ts, timestampMode, stepAnchorMs) }}</td>
                    <td class="whitespace-pre-wrap break-words" :class="[annotationLevelClass(entry.level), isSuccessLine(entry.message) ? 'text-success' : '']">{{ entry.message }}</td>
                  </tr>
                </template>
              </template>
            </table>
          </div>
        </div>
        <div v-else class="text-sm text-base-content/30 italic">No output captured</div>
      </div>
    </template>

    <!-- Test details -->
    <template v-else-if="test">
      <!-- Test header bar -->
      <div class="flex items-center gap-4 px-8 py-5 flex-none bg-base-200/30">
        <StatusIcon :status="test.status" :size="32" />
        <div class="flex-1 min-w-0">
          <span class="text-xl font-bold truncate block" :title="test.displayName">
            {{ shortTestName(test.displayName) }}
          </span>
          <span class="text-[11px] text-base-content/40 truncate block mt-0.5">
            {{ test.className }}
          </span>
        </div>
        <span v-if="test.durationMs != null"
              class="flex-none flex items-center gap-1.5 text-xs tabular-nums text-base-content/50 font-medium">
          <Icon icon="lucide:clock" class="w-3.5 h-3.5" />
          {{ formatDuration(test.durationMs) }}
          <span v-if="test.queueDurationMs" class="text-base-content/30">(+{{ formatDuration(test.queueDurationMs) }} queued)</span>
        </span>
        <button class="flex-none px-3 py-1 rounded border border-base-content/15 text-xs font-medium transition-colors flex items-center gap-1.5"
                :class="vncButtonEnabled
                  ? 'text-base-content/70 hover:bg-base-content/5'
                  : 'text-base-content/30 cursor-not-allowed'"
                :disabled="!vncButtonEnabled"
                :title="vncButtonTooltip"
                @click="vncExpanded = !vncExpanded">
          <Icon icon="lucide:monitor" class="w-3.5 h-3.5" />
          VNC
        </button>
      </div>

      <!-- Infrastructure: containers used for this test -->
      <div v-if="test.usedInstances?.length"
           class="flex items-center gap-2 px-8 py-1.5 bg-base-200/20 border-t border-base-content/5 flex-none">
        <span class="text-[10px] uppercase tracking-widest text-base-content/25 font-semibold flex-none">Containers</span>
        <button v-for="instId in test.usedInstances" :key="instId"
                class="badge badge-sm gap-1 cursor-pointer hover:badge-primary transition-colors"
                :title="`Inspect ${resolveInstanceLabel(instId)} — ${containerBadgeStatusLabel(instId)}`"
                @click="navigateToInstance(instId)">
          <Icon :icon="resolveInstanceType(instId) === 'server' ? 'lucide:server' : 'lucide:monitor'" class="w-2.5 h-2.5 flex-none" />
          <span :class="containerBadgeDotClass(instId)" aria-hidden="true" />
          {{ resolveInstanceLabel(instId) }}
        </button>
      </div>

      <!-- Status stripe -->
      <div class="status-stripe" :class="statusBgClass(test.status)" />

      <!-- Inline VNC viewers (collapsible) -->
      <div v-if="vncExpanded && vncButtonEnabled"
           class="bg-base-300/30 flex-none">
        <div class="flex items-center justify-between px-4 py-1.5">
          <div class="flex items-center gap-2">
            <span class="text-[11px] font-semibold uppercase tracking-widest text-base-content/40">Live View</span>
            <span v-if="!vncInteractive"
                  class="text-[10px] text-base-content/30">(view only)</span>
          </div>
          <button class="btn btn-ghost btn-xs gap-1 text-[11px]"
                  :class="vncInteractive ? 'text-warning' : 'text-base-content/40'"
                  :title="vncInteractive ? 'Disable interactive mode' : 'Enable interactive mode (mouse + keyboard)'"
                  aria-label="Toggle interactive mode"
                  :aria-pressed="vncInteractive"
                  @click="vncInteractive = !vncInteractive">
            <Icon :icon="vncInteractive ? 'lucide:mouse-pointer' : 'lucide:eye'" class="w-3 h-3" />
            {{ vncInteractive ? 'Interactive' : 'View Only' }}
          </button>
        </div>
        <div class="inline-vnc-grid px-2 pb-2">
          <VncTile v-for="inst in testVncInstances" :key="inst.instanceId"
                   :instance="inst"
                   :spinner-color="inst.instanceType === 'server' ? 'text-success' : 'text-info'"
                   :connection-count="inlineConnectionCount(inst)"
                   :connected-server-label="inlineConnectedServerLabel(inst)"
                   :stats="store.instanceStats.get(inst.instanceId)"
                   :setup-steps="inst.setupSteps"
                   :setup-status="inst.setupStatus"
                   :instance-count="testVncInstances.length"
                   :stopped="store.runDone"
                   :retained="inst.disposed"
                   :show-expand="false"
                   @inspect="navigateToInstance(inst.instanceId)" />
        </div>
      </div>

      <!-- Scrollable content area -->
      <div class="flex-1 overflow-auto p-6">
        <!-- Test recordings section (outside keyed block to survive content updates) -->
        <div v-if="stableVideoItems.length > 0" class="mb-6">
          <SyncedVideos :videos="stableVideoItems" :screenshot-src="screenshotSrc" :test-duration-ms="stableTestDurationMs" :lifecycle="stableLifecycle" @navigate-instance="navigateToInstance" />
          <!-- Partial-skip placeholders: some sources captured, others were skipped
               (e.g. asymmetric [TestServer(Artifacts=false)] + client lease).
               Render the skip cards beside the captured videos so the user sees
               both the available footage and the explanation for what's missing. -->
          <div v-if="missingSourcePlaceholders.length > 0" class="mt-3 flex gap-3 flex-wrap">
            <MediaCard v-for="card in missingSourcePlaceholders" :key="card.source"
                       :label="card.label" icon="lucide:video"
                       :loading="false" :unavailable="true"
                       unavailable-icon="lucide:film-off"
                       :unavailable-text="placeholderCopy(card.reason, card.source).text"
                       :unavailable-code="placeholderCopy(card.reason, card.source).code"
                       :unavailable-detail="placeholderCopy(card.reason, card.source).detail" />
          </div>
        </div>
        <div v-else-if="showRecordingPlaceholder" class="mb-6">
          <div class="flex gap-3 flex-wrap">
            <MediaCard v-for="card in placeholderCards" :key="card.source"
                       :label="card.label" icon="lucide:video"
                       :loading="false" :unavailable="true"
                       :unavailable-icon="card.reason ? 'lucide:film-off' : 'lucide:film'"
                       :unavailable-text="placeholderCopy(card.reason, card.source).text"
                       :unavailable-code="placeholderCopy(card.reason, card.source).code"
                       :unavailable-detail="placeholderCopy(card.reason, card.source).detail" />
          </div>
        </div>

        <div :key="contentVersion" v-if="outputSegments.length > 0 || hasError">
          <!-- Terminal Output block -->
          <div v-if="outputSegments.length > 0" class="rounded-lg border border-base-content/5 bg-base-200 overflow-hidden">
            <!-- Terminal header -->
            <div class="flex items-center gap-2 px-4 py-2 border-b border-base-content/5">
              <Icon icon="lucide:terminal" class="w-3.5 h-3.5 text-base-content/40" />
              <span class="text-xs text-base-content/40">Test Output</span>
              <!-- Inline search -->
              <div class="flex-1 flex justify-end">
                <div class="relative max-w-[200px]">
                  <input ref="outputSearchRef"
                         v-model="outputSearch"
                         type="text"
                         placeholder="Search... (/)"
                         class="w-full h-6 pl-6 pr-6 text-[11px] bg-base-300/40 border border-base-content/10 rounded
                                focus:border-primary/30 focus:outline-none text-base-content/60 placeholder:text-base-content/25"
                         @keydown.escape.prevent="outputSearch = ''; ($event.target as HTMLElement).blur()" />
                  <Icon icon="lucide:search" class="w-3 h-3 absolute left-2 top-1.5 text-base-content/25" />
                  <button v-if="outputSearch"
                          class="absolute right-1 top-1 text-base-content/30 hover:text-base-content/60"
                          @click="outputSearch = ''">
                    <Icon icon="lucide:x" class="w-3.5 h-3.5" />
                  </button>
                </div>
              </div>
              <button v-if="hasVideos"
                      class="btn btn-ghost btn-xs px-1.5 text-base-content/30 hover:text-base-content/60"
                      :class="hasAnyBreakpoints ? 'text-primary' : ''"
                      :title="hasAnyBreakpoints ? 'Clear all breakpoints' : 'Set breakpoints on all lines'"
                      @click="toggleAllBreakpoints">
                <Icon :icon="hasAnyBreakpoints ? 'lucide:list-x' : 'lucide:list-plus'" class="w-3.5 h-3.5" />
              </button>
              <button class="btn btn-ghost btn-xs px-1.5 text-base-content/30 hover:text-base-content/60"
                      :class="showInlineScreenshots ? 'text-info' : ''"
                      title="Toggle inline screenshots"
                      @click="showInlineScreenshots = !showInlineScreenshots">
                <Icon icon="lucide:image" class="w-3.5 h-3.5" />
              </button>
              <button class="btn btn-ghost btn-xs px-1.5 text-base-content/30 hover:text-base-content/60"
                      :class="timestampMode !== 'off' ? 'text-info' : ''"
                      :title="timestampTooltip"
                      @click="timestampMode = nextTimestampMode(timestampMode)">
                <Icon icon="lucide:clock" class="w-3.5 h-3.5" />
              </button>
              <button v-if="plainOutput"
                      class="btn btn-ghost btn-xs gap-1 text-base-content/30 hover:text-base-content/60"
                      @click="copyOutput">
                <Icon v-if="!copiedOutput" icon="lucide:copy" class="w-3.5 h-3.5" />
                <Icon v-else icon="lucide:check" class="w-3.5 h-3.5 text-success" />
                <span :class="copiedOutput ? 'text-success font-medium' : ''">{{ copiedOutput ? 'Copied!' : 'Copy' }}</span>
              </button>
            </div>
            <!-- Source filter chip bar -->
            <div class="flex items-center gap-1.5 px-4 py-1.5 border-b border-base-content/5 bg-base-200/40">
              <span class="text-[10px] uppercase tracking-widest text-base-content/30 font-semibold mr-1">Sources</span>
              <button v-for="source in filterSources" :key="source"
                      class="px-2 py-0.5 rounded text-[11px] flex items-center gap-1 transition-colors"
                      :class="sourceFilter[source]
                        ? 'bg-base-content/10 text-base-content/80'
                        : 'bg-base-300/40 text-base-content/30 line-through'"
                      :title="sourceFilter[source] ? `Hide ${sourceLabels[source]} entries` : `Show ${sourceLabels[source]} entries`"
                      @mouseenter="hoveredSource = source"
                      @mouseleave="hoveredSource = null"
                      @click="toggleSourceFilter(source)">
                <Icon :icon="annotationSourceIcon(source)" class="w-3 h-3" :class="sourceFilter[source] ? annotationSourceClass(source) : ''" />
                {{ sourceLabels[source] }}
              </button>
              <button v-if="isSourceFiltered()"
                      class="ml-auto px-1.5 py-0.5 rounded text-[10px] flex items-center gap-1 text-base-content/40 hover:text-base-content/70 transition-colors"
                      title="Re-enable all sources"
                      @click="resetSourceFilter()">
                <Icon icon="lucide:rotate-ccw" class="w-3 h-3" />
                Reset
              </button>
            </div>
            <!-- Terminal body with line numbers -->
            <div class="console-block font-mono code-block p-4 overflow-x-auto">
              <table class="w-full border-collapse">
                <template v-for="(seg, i) in outputSegments" :key="i">
                  <template v-if="seg.type === 'lines'">
                    <tr v-for="(entry, li) in seg.items" :key="`${i}-${li}`"
                        :class="[searchDimClass(entry.message),
                                 lineBreakpointState.get(outputSegmentOffsets[i] + li + 1) === 'hit' ? 'bg-primary/15' :
                                 lineBreakpointState.get(outputSegmentOffsets[i] + li + 1) === 'hovered' ? 'bg-primary/10' : '',
                                 hoveredSource && entry.source !== hoveredSource ? 'opacity-30' : '']"
                        :data-breakpoint-hit="lineBreakpointState.get(outputSegmentOffsets[i] + li + 1) === 'hit' || undefined">
                      <td v-if="hasVideos" class="w-4 pl-2 pr-0 align-top">
                        <span class="inline-block w-1.5 h-1.5 rounded-full mt-[7px] transition-colors duration-300 cursor-pointer dot-hover"
                              :class="[
                                lineReached(entry) ? 'bg-success' : 'bg-base-content/15',
                                lineBreakpointState.has(outputSegmentOffsets[i] + li + 1) ? 'ring-breakpoint' : '',
                                lineBreakpointState.get(outputSegmentOffsets[i] + li + 1) === 'hit' ? 'ring-breakpoint-hit' : ''
                              ]"
                              @click.stop="toggleBreakpoint(outputSegmentOffsets[i] + li + 1, entry)" />
                      </td>
                      <td class="text-right pr-3 pl-1 select-none text-base-content/20 align-top w-[1%] whitespace-nowrap tabular-nums">{{ outputSegmentOffsets[i] + li + 1 }}</td>
                      <td class="pr-2 align-top w-[1%]">
                        <Icon :icon="annotationSourceIcon(entry.source)" class="w-3 h-3 mt-[3px]" :class="annotationSourceClass(entry.source)" :title="sourceLabels[entry.source]" />
                      </td>
                      <td v-if="timestampMode !== 'off'" class="text-right pr-1 text-base-content/40 align-top whitespace-nowrap tabular-nums">{{ formatEntryTimestamp(entry.ts, timestampMode, outputAnchorMs) }}</td>
                      <td class="whitespace-pre-wrap break-words" :class="[annotationLevelClass(entry.level), isSuccessLine(entry.message) ? 'text-success' : '']">{{ entry.message }}</td>
                    </tr>
                  </template>
                  <tr v-else-if="seg.type === 'images' && showInlineScreenshots" class="screenshot-row">
                    <td v-if="hasVideos"></td>
                    <td></td>
                    <td class="py-2" :colspan="timestampMode !== 'off' ? 3 : 2">
                      <div class="flex gap-3 flex-wrap not-prose">
                        <MediaCard v-for="(img, j) in seg.items" :key="j"
                                   :label="`Screenshot · ${img.source}`"
                                   icon="lucide:image"
                                   :loading="!loadedImages.has(img.path) && !failedImages.has(img.path)"
                                   :unavailable="failedImages.has(img.path)"
                                   unavailable-text="Image unavailable"
                                   unavailable-icon="lucide:image-off"
                                   class="transition-[border-color,opacity] duration-150 ease-out"
                                   :class="failedImages.has(img.path) ? 'cursor-default' : 'group cursor-pointer hover:border-base-content/20'"
                                   @click="!failedImages.has(img.path) && loadedImages.has(img.path) && openLightbox(screenshotSrc(img.path))">
                          <img :src="screenshotSrc(img.path)"
                               alt=""
                               loading="lazy"
                               class="w-full h-full object-contain brightness-75 group-hover:brightness-100 transition-[filter] duration-200 ease-out"
                               style="border:none;outline:none"
                               @load="loadedImages.add(img.path)"
                               @error="failedImages.add(img.path)" />
                        </MediaCard>
                      </div>
                    </td>
                  </tr>
                  <!-- Videos are rendered in a standalone section below the output -->
                </template>
              </table>
            </div>
          </div>

          <!-- Inline error block -->
          <div v-if="hasError" class="mt-6 space-y-4">
            <div class="flex items-center justify-between">
              <div class="text-[11px] font-semibold uppercase tracking-widest text-error/60">Error</div>
              <button
                class="btn btn-ghost btn-xs gap-1 text-base-content/40 hover:text-base-content/70"
                @click="copyError"
              >
                <Icon v-if="!copiedError" icon="lucide:copy" class="w-3.5 h-3.5" />
                <Icon v-else icon="lucide:check" class="w-3.5 h-3.5 text-success" />
                <span :class="copiedError ? 'text-success font-medium' : ''">{{ copiedError ? 'Copied!' : 'Copy' }}</span>
              </button>
            </div>

            <!-- Exception type + message + stack trace -->
            <div class="bg-error/6 border border-error/10 rounded-lg p-4">
              <!-- Enrichment header: failure category, phase, repro -->
              <div v-if="test.failureCategory || test.phase || test.reproCommand"
                   class="flex flex-wrap items-center gap-2 mb-3 text-[11px]">
                <span v-if="test.failureCategory"
                      class="px-1.5 py-0.5 rounded bg-error/15 text-error font-medium uppercase tracking-wide"
                      :title="`Failure category: ${test.failureCategory}`">
                  {{ test.failureCategory }}
                </span>
                <span v-if="test.phase"
                      class="px-1.5 py-0.5 rounded bg-base-content/10 text-base-content/70 font-mono"
                      :title="`Failed during: ${test.phase}`">
                  {{ test.phase }}
                </span>
                <button v-if="test.reproCommand"
                        class="ml-auto px-1.5 py-0.5 rounded bg-base-content/10 hover:bg-base-content/20 text-base-content/70 font-mono inline-flex items-center gap-1"
                        :title="`Copy: ${test.reproCommand}`"
                        @click="copyRepro">
                  <Icon icon="lucide:copy" class="w-3 h-3" />
                  <span>{{ copiedRepro ? 'Copied!' : 'repro' }}</span>
                </button>
              </div>
              <div v-if="test.errorType" class="text-[11px] font-mono text-error/50 mb-1">
                {{ test.errorType }}
              </div>
              <div v-if="test.errorMessage" class="text-sm text-error font-mono whitespace-pre-wrap leading-relaxed">
                {{ test.errorMessage }}
              </div>
              <pre v-if="test.stackTrace"
                   class="font-mono code-block whitespace-pre-wrap text-error/40 mt-3 leading-relaxed">{{ test.stackTrace }}</pre>
            </div>

            <!-- Failure context: server state at failure point -->
            <details v-if="test.failureContext" class="bg-base-200/60 border border-base-content/10 rounded-lg">
              <summary class="px-4 py-2 cursor-pointer text-xs font-medium text-base-content/70 hover:text-base-content select-none">
                Failure context (server state)
              </summary>
              <pre class="font-mono text-[11px] whitespace-pre-wrap leading-relaxed text-base-content/70 px-4 pb-3 overflow-auto">{{ JSON.stringify(test.failureContext, null, 2) }}</pre>
            </details>
          </div>
        </div>

        <!-- Skip reason -->
        <div v-else-if="test.skipReason"
             class="flex items-start gap-2 bg-warning/6 border border-warning/10 rounded-lg p-4">
          <Icon icon="lucide:alert-triangle" class="w-4 h-4 text-warning flex-none mt-0.5" />
          <div>
            <div class="text-xs font-medium text-warning mb-0.5">Skipped</div>
            <div class="text-sm text-base-content/70">{{ test.skipReason }}</div>
          </div>
        </div>

        <!-- No output and no error -->
        <div v-else-if="!hasError" class="text-sm text-base-content/30 italic">No output captured</div>
      </div>
    </template>

    <!-- Lightbox overlay -->
    <ImageLightbox v-if="lightboxOpen" :images="allScreenshots" :initial-index="lightboxInitialIndex" @close="lightboxOpen = false" />
  </div>
</template>

<style scoped>
/* The console block uses white-space: pre-wrap from Tailwind.
   Screenshot rows are block-level divs that interrupt the text flow naturally. */
.console-block .screenshot-row {
  white-space: normal;
}

/* Inline VNC tiles: same responsive breakpoints as VncGrid's grid layout. */
.inline-vnc-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
}
@media (max-width: 1200px) { .inline-vnc-grid { grid-template-columns: repeat(3, 1fr); } }
@media (max-width: 900px)  { .inline-vnc-grid { grid-template-columns: repeat(2, 1fr); } }
@media (max-width: 600px)  { .inline-vnc-grid { grid-template-columns: 1fr; } }
</style>
