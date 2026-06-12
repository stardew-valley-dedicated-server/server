<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { Line } from "vue-chartjs";
import { useLinkedCharts } from "../composables/useLinkedCharts";
import { useSyncedZoom } from "../composables/useSyncedZoom";
import { useTestUI } from "../composables/useTestUI";
import type { InstanceHistoryEntry, InstanceSnapshot, SetupStepSnapshot, StatsSnapshotEntry } from "../types/state";
import {
    buildZoomConfig,
    chartElementDefaults,
    chartLayoutDefaults,
    chartScaleDefaults,
    formatTimeLabel,
    memTickCallback,
    niceCeil,
    pctTickCallback,
    registerChartPlugins,
    smooth,
} from "../utils/chart";
import { shortTestName } from "../utils/format";
import { instanceStatusDotClass, instanceStatusLabel } from "../utils/instance-status";
import {
    DEFAULT_TARGET_TPS,
    formatBytesPerSec,
    METRIC_COLORS,
    TPS_PEAK_HEADROOM,
    TPS_TARGET_HEADROOM,
} from "../utils/metrics";
import {
    anchorTimestampMs,
    annotationLevelClass,
    annotationSourceClass,
    annotationSourceIcon,
    formatEntryTimestamp,
    isSuccessLine,
    nextTimestampMode,
    type TimestampMode,
} from "../utils/output";
import { medianOf } from "../utils/stats";
import ContainerLogViewer from "./ContainerLogViewer.vue";
import StatusIcon from "./StatusIcon.vue";

registerChartPlugins();

const { plugin: linkedPlugin, setChartRef: setLinkedChartRef } = useLinkedCharts();

const props = withDefaults(
    defineProps<{
        instance: InstanceSnapshot;
        stats?: {
            cpuPercent: number;
            memoryMb: number;
            cpuCount: number;
            totalMemoryMb: number;
            fps: number | null;
            tps: number | null;
            avgTickMs: number | null;
            gameMemoryMb: number | null;
            targetTps: number | null;
            targetFps: number | null;
            gcRate: number | null;
            pendingActions: number | null;
            gameThreadWaitMs: number | null;
            netRxBytesPerSec: number | null;
            netTxBytesPerSec: number | null;
            blkReadBytesPerSec: number | null;
            blkWriteBytesPerSec: number | null;
            memoryLimitMb: number;
        };
        statsHistory: StatsSnapshotEntry[];
        instanceCount: number;
        setupSteps: SetupStepSnapshot[];
        setupStatus: "pending" | "running" | "completed" | "failed" | null;
        stopped?: boolean;
        serverLabel?: string | null;
        connectedPeers?: { instanceId: string; label: string; instanceType: "server" | "client" }[];
        resolveLabel?: (id: string) => string;
        canGoBack?: boolean;
        hasPrev?: boolean;
        hasNext?: boolean;
        screenshotSrc?: (path: string) => string;
    }>(),
    {
        stopped: false,
        serverLabel: null,
        connectedPeers: undefined,
        resolveLabel: undefined,
        canGoBack: false,
        hasPrev: false,
        hasNext: false,
        screenshotSrc: (p: string) => `/artifacts/${p}`,
    },
);

const emit = defineEmits<{
    close: [];
    back: [];
    prev: [];
    next: [];
    "navigate-instance": [instanceId: string];
    "navigate-test": [testName: string];
}>();

const expandedSteps = ref<Set<string>>(new Set());
const timestampMode = ref<TimestampMode>("off");
const timestampTooltip = computed(() =>
    timestampMode.value === "off"
        ? "Show absolute timestamps"
        : timestampMode.value === "absolute"
          ? "Show relative timestamps"
          : "Hide timestamps",
);

const {
    charts: chartRefs,
    trackChartRef: trackZoomRef,
    syncAllAxes,
    resetAll: resetChartZoom,
    isZoomed,
    ctrlHeld,
} = useSyncedZoom();

function setChartRef(el: any) {
    if (!el) return;
    setLinkedChartRef(el);
    trackZoomRef(el);
}

function toggleStep(step: string) {
    if (expandedSteps.value.has(step)) expandedSteps.value.delete(step);
    else expandedSteps.value.add(step);
}

function onKeyDown(e: KeyboardEvent) {
    if (e.key === "ArrowLeft" && props.hasPrev) {
        e.preventDefault();
        emit("prev");
    } else if (e.key === "ArrowRight" && props.hasNext) {
        e.preventDefault();
        emit("next");
    } else if (e.key === "Escape") {
        e.preventDefault();
        emit("close");
    }
}

onMounted(() => window.addEventListener("keydown", onKeyDown));
onUnmounted(() => {
    window.removeEventListener("keydown", onKeyDown);
    // Remove tooltip DOM elements created by leaseLinePlugin
    const tooltips = document.querySelectorAll(".lease-marker-tip");
    tooltips.forEach((el) => {
        el.remove();
    });
});

// ── Scrub-to-history (driven by InfrastructureTimeline click) ──
const { store } = useTestUI();
const historyRows = ref<HTMLElement[]>([]);
function setHistoryRow(el: Element | null, idx: number) {
    if (el instanceof HTMLElement) historyRows.value[idx] = el;
}

watch(
    () => store.state.currentRunMs,
    async (ms) => {
        if (ms == null) return;
        const startIso = store.state.runStartTime;
        const history = props.instance.history;
        if (!startIso || !history || history.length === 0) return;
        const runStart = new Date(startIso).getTime();
        if (Number.isNaN(runStart)) return;
        let bestIdx = -1;
        let bestDelta = Number.POSITIVE_INFINITY;
        for (let i = 0; i < history.length; i++) {
            const t = new Date(history[i].timestamp).getTime();
            if (Number.isNaN(t)) continue;
            const delta = Math.abs(t - runStart - ms);
            if (delta < bestDelta) {
                bestDelta = delta;
                bestIdx = i;
            }
        }
        if (bestIdx < 0) return;
        // Wait one tick so freshly-mounted history rows are populated before scroll.
        await nextTick();
        const el = historyRows.value[bestIdx];
        if (el) el.scrollIntoView({ behavior: "smooth", block: "center" });
    },
    { immediate: true },
);

const isServer = computed(() => props.instance.instanceType === "server");

const medianCpu = computed(() => medianOf(props.statsHistory, (s) => s.cpuPercent));
const medianMem = computed(() => medianOf(props.statsHistory, (s) => s.gameMemoryMb ?? s.memoryMb));
const medianTps = computed(() => medianOf(props.statsHistory, (s) => s.tps));
const medianFps = computed(() => medianOf(props.statsHistory, (s) => s.fps));
const medianQueue = computed(() => medianOf(props.statsHistory, (s) => s.pendingActions));
const medianWait = computed(() => medianOf(props.statsHistory, (s) => s.gameThreadWaitMs));
const medianGcRate = computed(() => medianOf(props.statsHistory, (s) => s.gcRate));
const medianNetRx = computed(() => medianOf(props.statsHistory, (s) => s.netRxBytesPerSec));
const medianNetTx = computed(() => medianOf(props.statsHistory, (s) => s.netTxBytesPerSec));

const medianSampleCount = computed(() => props.statsHistory.length);
const medianTooltip = computed(
    () => `Median of ${medianSampleCount.value} sample${medianSampleCount.value === 1 ? "" : "s"}`,
);

// Chart options shared across all charts (built from shared defaults)
const chartOptions = {
    ...chartLayoutDefaults,
    interaction: {
        mode: "index" as const,
        intersect: false,
    },
    plugins: {
        legend: { display: false },
        tooltip: {
            backgroundColor: "rgba(0,0,0,0.8)",
            titleFont: { size: 11 },
            bodyFont: { size: 11 },
            callbacks: {
                label: (ctx: any) => {
                    const v = ctx.raw as number | null;
                    if (v == null) return "";
                    return `${Math.round(v * 10) / 10}`;
                },
            },
        },
        zoom: buildZoomConfig({ ctrlHeld, onSync: syncAllAxes }),
    },
    scales: { ...chartScaleDefaults },
    elements: { ...chartElementDefaults },
};

const labels = computed(() => props.statsHistory.map((s) => formatTimeLabel(s.timestamp)));

function makeDataset(data: number[], color: string) {
    return {
        data,
        borderColor: color,
        backgroundColor: color.replace(")", ", 0.1)").replace("rgb", "rgba"),
        fill: true,
        borderWidth: 1.5,
    };
}

// ── Scale helpers ──
// Track the highest value seen per metric so scales only grow (no jitter).
// Snap to nice round ceilings with generous headroom.
const peakCpu = ref(0);
const peakMem = ref(0);
const peakTps = ref(0);
const peakFps = ref(0);
const peakQueue = ref(0);
const peakWait = ref(0);
const peakGcRate = ref(0);
const peakNetRx = ref(0);
const peakNetTx = ref(0);

// Update peaks incrementally. Only check latest entry when history grows.
// Reset and recompute when length shrinks (instance switched).
let lastPeakLength = 0;
function updatePeaksFromEntry(s: StatsSnapshotEntry) {
    if (s.cpuPercent > peakCpu.value) peakCpu.value = s.cpuPercent;
    const mem = s.gameMemoryMb ?? s.memoryMb;
    if (mem > peakMem.value) peakMem.value = mem;
    if (s.tps != null && s.tps > peakTps.value) peakTps.value = s.tps;
    if (s.fps != null && s.fps > peakFps.value) peakFps.value = s.fps;
    if (s.pendingActions != null && s.pendingActions > peakQueue.value) peakQueue.value = s.pendingActions;
    if (s.gameThreadWaitMs != null && s.gameThreadWaitMs > peakWait.value) peakWait.value = s.gameThreadWaitMs;
    if (s.gcRate != null && s.gcRate > peakGcRate.value) peakGcRate.value = s.gcRate;
    if (s.netRxBytesPerSec != null && s.netRxBytesPerSec > peakNetRx.value) peakNetRx.value = s.netRxBytesPerSec;
    if (s.netTxBytesPerSec != null && s.netTxBytesPerSec > peakNetTx.value) peakNetTx.value = s.netTxBytesPerSec;
}

watch(
    () => props.statsHistory.length,
    (len) => {
        if (len < lastPeakLength) {
            // History shrank (different instance); full recompute
            peakCpu.value = 0;
            peakMem.value = 0;
            peakTps.value = 0;
            peakFps.value = 0;
            peakQueue.value = 0;
            peakWait.value = 0;
            peakGcRate.value = 0;
            peakNetRx.value = 0;
            peakNetTx.value = 0;
            for (const s of props.statsHistory) updatePeaksFromEntry(s);
        } else if (len > lastPeakLength) {
            updatePeaksFromEntry(props.statsHistory[len - 1]);
        }
        lastPeakLength = len;
    },
    { immediate: true },
);

// ── CPU chart ──
const cpuChartData = computed(() => ({
    labels: labels.value,
    datasets: [makeDataset(smooth(props.statsHistory.map((s) => s.cpuPercent)) as number[], METRIC_COLORS.cpu)],
}));

const cpuOptions = computed(() => {
    // Match badge logic: fair share = (cpuCount * 100) / instanceCount
    // Badge fallback: cpuCount defaults to 4 when 0/unknown
    const cpuCount = (props.stats?.cpuCount ?? 0) > 0 ? props.stats!.cpuCount! : 4;
    const fairShare = (cpuCount * 100) / Math.max(props.instanceCount, 1);
    // Badge turns red at 1.5x fair share, so show that range comfortably
    const max = niceCeil(Math.max(peakCpu.value * 1.15, fairShare * 1.5), 50);
    return {
        ...chartOptions,
        scales: {
            ...chartOptions.scales,
            y: {
                ...chartOptions.scales.y,
                min: 0,
                max,
                ticks: { ...chartOptions.scales.y.ticks, callback: pctTickCallback },
            },
        },
    };
});

// ── Memory chart ──
const memChartData = computed(() => ({
    labels: labels.value,
    datasets: [
        makeDataset(smooth(props.statsHistory.map((s) => s.gameMemoryMb ?? s.memoryMb)) as number[], METRIC_COLORS.mem),
    ],
}));

const memOptions = computed(() => {
    // Match badge logic: fair share = totalMem / instanceCount
    // Badge fallback: totalMem defaults to 16384 when 0/unknown
    const totalMem = (props.stats?.totalMemoryMb ?? 0) > 0 ? props.stats!.totalMemoryMb! : 16384;
    const fairShare = totalMem / Math.max(props.instanceCount, 1);
    // Badge turns red at 1.5x fair share, so show that range comfortably
    const ceiling = niceCeil(Math.max(peakMem.value * 1.15, fairShare * 1.5), 512);
    return {
        ...chartOptions,
        scales: {
            ...chartOptions.scales,
            y: {
                ...chartOptions.scales.y,
                min: 0,
                max: ceiling,
                ticks: { ...chartOptions.scales.y.ticks, callback: memTickCallback },
            },
        },
    };
});

// ── TPS chart ──
const tpsChartData = computed(() => ({
    labels: labels.value,
    datasets: [makeDataset(smooth(props.statsHistory.map((s) => s.tps ?? 0)) as number[], METRIC_COLORS.tps)],
}));

const tpsOptions = computed(() => {
    const target = props.stats?.targetTps ?? DEFAULT_TARGET_TPS;
    const max = niceCeil(Math.max(peakTps.value * TPS_PEAK_HEADROOM, target * TPS_TARGET_HEADROOM), 10);
    return {
        ...chartOptions,
        scales: {
            ...chartOptions.scales,
            y: { ...chartOptions.scales.y, min: 0, max },
        },
    };
});

// ── FPS chart ──
const fpsChartData = computed(() => ({
    labels: labels.value,
    datasets: [makeDataset(smooth(props.statsHistory.map((s) => s.fps ?? 0)) as number[], METRIC_COLORS.fps)],
}));

const fpsOptions = computed(() => {
    const target = props.stats?.targetFps ?? props.stats?.targetTps ?? DEFAULT_TARGET_TPS;
    const max = niceCeil(Math.max(peakFps.value * TPS_PEAK_HEADROOM, target * TPS_TARGET_HEADROOM), 10);
    return {
        ...chartOptions,
        scales: {
            ...chartOptions.scales,
            y: { ...chartOptions.scales.y, min: 0, max },
        },
    };
});

// ── Game Thread Queue chart ──
const queueChartData = computed(() => ({
    labels: labels.value,
    datasets: [
        makeDataset(smooth(props.statsHistory.map((s) => s.pendingActions ?? 0)) as number[], METRIC_COLORS.queue),
    ],
}));

const queueOptions = computed(() => {
    const max = niceCeil(Math.max(peakQueue.value * 1.15, 10), 5);
    return { ...chartOptions, scales: { ...chartOptions.scales, y: { ...chartOptions.scales.y, min: 0, max } } };
});

// ── Thread Wait chart ──
const waitChartData = computed(() => ({
    labels: labels.value,
    datasets: [
        makeDataset(smooth(props.statsHistory.map((s) => s.gameThreadWaitMs ?? 0)) as number[], METRIC_COLORS.wait),
    ],
}));

const waitOptions = computed(() => {
    const max = niceCeil(Math.max(peakWait.value * 1.15, 100), 50);
    return {
        ...chartOptions,
        scales: {
            ...chartOptions.scales,
            y: {
                ...chartOptions.scales.y,
                min: 0,
                max,
                ticks: { ...chartOptions.scales.y.ticks, callback: (v: number) => `${v}ms` },
            },
        },
    };
});

// ── GC Rate chart ──
const gcRateChartData = computed(() => ({
    labels: labels.value,
    datasets: [makeDataset(smooth(props.statsHistory.map((s) => s.gcRate ?? 0)) as number[], METRIC_COLORS.gc)],
}));

const gcRateOptions = computed(() => {
    const max = niceCeil(Math.max(peakGcRate.value * 1.15, 5), 2);
    return { ...chartOptions, scales: { ...chartOptions.scales, y: { ...chartOptions.scales.y, min: 0, max } } };
});

// ── Network I/O chart (2-line: RX + TX) ──
const netChartData = computed(() => ({
    labels: labels.value,
    datasets: [
        {
            ...makeDataset(
                smooth(props.statsHistory.map((s) => s.netRxBytesPerSec ?? 0)) as number[],
                METRIC_COLORS.netRx,
            ),
            label: "RX",
        },
        {
            ...makeDataset(
                smooth(props.statsHistory.map((s) => s.netTxBytesPerSec ?? 0)) as number[],
                METRIC_COLORS.netTx,
            ),
            label: "TX",
        },
    ],
}));

const netOptions = computed(() => {
    const peakNet = Math.max(peakNetRx.value, peakNetTx.value);
    const max = niceCeil(Math.max(peakNet * 1.15, 1024), 1024);
    return {
        ...chartOptions,
        plugins: {
            ...chartOptions.plugins,
            legend: {
                display: true,
                position: "top" as const,
                labels: { color: "rgba(255,255,255,0.5)", font: { size: 9 }, boxWidth: 12, boxHeight: 2, padding: 4 },
            },
            tooltip: {
                ...chartOptions.plugins.tooltip,
                callbacks: {
                    label: (ctx: any) => {
                        const v = ctx.raw as number | null;
                        if (v == null) return "";
                        return `${ctx.dataset.label}: ${formatBytesPerSec(v)}`;
                    },
                },
            },
        },
        scales: {
            ...chartOptions.scales,
            y: {
                ...chartOptions.scales.y,
                min: 0,
                max,
                ticks: { ...chartOptions.scales.y.ticks, callback: (v: number) => formatBytesPerSec(v) },
            },
        },
    };
});

// ── Lease event markers for charts ──
const showLeaseMarkers = ref(true);

watch(showLeaseMarkers, () => {
    for (const c of chartRefs) {
        c?.chart?.update("none");
    }
});

const leaseMarkers = computed(() => {
    if (props.statsHistory.length === 0) return [];
    const markers: { index: number; label: string }[] = [];
    for (const entry of props.instance.history) {
        if (entry.event !== "leased") continue;
        const entryTime = new Date(entry.timestamp).getTime();
        let closest = -1,
            minDiff = Number.POSITIVE_INFINITY;
        for (let i = 0; i < props.statsHistory.length; i++) {
            const diff = Math.abs(new Date(props.statsHistory[i].timestamp).getTime() - entryTime);
            if (diff < minDiff) {
                minDiff = diff;
                closest = i;
            }
        }
        if (closest >= 0 && minDiff < 30000) {
            markers.push({ index: closest, label: entry.testName ?? "Leased" });
        }
    }
    return markers;
});

const leaseLinePlugin = {
    id: "leaseLines",
    afterDraw(chart: any) {
        if (!showLeaseMarkers.value) return;
        const meta = chart.getDatasetMeta(0);
        if (!meta?.data?.length) return;
        const ctx = chart.ctx;
        const yScale = chart.scales.y;
        const mouseX = (chart as any)._leaseMouseX as number | undefined;

        // First pass: find nearest marker within hover tolerance (6px).
        let nearestIndex = -1;
        let nearestDist = Number.POSITIVE_INFINITY;
        if (mouseX != null) {
            for (let i = 0; i < leaseMarkers.value.length; i++) {
                const m = leaseMarkers.value[i];
                if (m.index >= meta.data.length) continue;
                const dist = Math.abs(mouseX - meta.data[m.index].x);
                if (dist < 6 && dist < nearestDist) {
                    nearestDist = dist;
                    nearestIndex = i;
                }
            }
        }

        let hoveredMarker: { label: string; x: number } | null = null;
        for (let i = 0; i < leaseMarkers.value.length; i++) {
            const m = leaseMarkers.value[i];
            if (m.index >= meta.data.length) continue;
            const x = meta.data[m.index].x;
            const isHovered = i === nearestIndex;
            if (isHovered) hoveredMarker = { label: m.label, x };
            ctx.save();
            ctx.strokeStyle = isHovered ? "rgba(255,255,255,0.75)" : "rgba(255,255,255,0.28)";
            ctx.lineWidth = isHovered ? 2.5 : 1.5;
            ctx.setLineDash([4, 3]);
            ctx.beginPath();
            ctx.moveTo(x, yScale.top);
            ctx.lineTo(x, yScale.bottom);
            ctx.stroke();
            ctx.restore();
        }
        // Position HTML tooltip (managed as a child of the canvas wrapper)
        const wrapper = chart.canvas?.parentElement;
        if (!wrapper) return;
        let tip = wrapper.querySelector(".lease-marker-tip") as HTMLElement | null;
        if (hoveredMarker) {
            if (!tip) {
                tip = document.createElement("div");
                tip.className = "lease-marker-tip";
                Object.assign(tip.style, {
                    position: "absolute",
                    pointerEvents: "none",
                    zIndex: "10",
                    transform: "translateX(-50%)",
                    background: "rgba(0,0,0,0.8)",
                    color: "rgba(255,255,255,0.9)",
                    fontSize: "10px",
                    padding: "2px 6px",
                    borderRadius: "4px",
                    whiteSpace: "nowrap",
                    boxShadow: "0 2px 6px rgba(0,0,0,0.3)",
                });
                wrapper.appendChild(tip);
            }
            tip.textContent = hoveredMarker.label;
            tip.style.left = hoveredMarker.x + "px";
            tip.style.top = yScale.top - 4 + "px";
            tip.style.display = "";
        } else if (tip) {
            tip.style.display = "none";
        }
    },
    afterEvent(chart: any, args: any) {
        if (args.event.type === "mousemove") {
            (chart as any)._leaseMouseX = args.event.x;
            chart.draw();
        } else if (args.event.type === "mouseout") {
            (chart as any)._leaseMouseX = undefined;
            const tip = chart.canvas?.parentElement?.querySelector(".lease-marker-tip") as HTMLElement | null;
            if (tip) tip.style.display = "none";
            chart.draw();
        }
    },
};

function statusDotClass(): string {
    return instanceStatusDotClass(props.instance.status, props.instance.connected, props.stopped, "md");
}

function statusLabel(): string {
    return instanceStatusLabel(props.instance.status, props.instance.connected, props.stopped, true);
}

function historyLabel(entry: InstanceHistoryEntry): string {
    switch (entry.event) {
        case "created":
            return "Created";
        case "leased":
            return entry.testName ? "Leased for" : "Leased";
        case "returned":
            return "Returned to pool";
        case "connected":
            return "Connected";
        case "disconnected":
            return "Disconnected";
        case "client_attached":
            return "Client attached";
        case "poisoned":
            return entry.reason ? `Poisoned: ${entry.reason}` : "Poisoned";
        case "disposed":
            return "Disposed";
        default:
            return entry.event;
    }
}

function formatHistoryTime(timestamp: string): string {
    const d = new Date(timestamp);
    return (
        d.toLocaleTimeString("en-US", { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" }) +
        "." +
        String(d.getMilliseconds()).padStart(3, "0")
    );
}
</script>

<template>
  <!-- Modal backdrop -->
  <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm"
       role="dialog" aria-modal="true"
       data-modal
       @click.self="emit('close')">
    <div class="bg-base-100 rounded-xl shadow-2xl w-[90vw] max-w-4xl max-h-[85vh] overflow-hidden flex flex-col">
      <!-- Header -->
      <div class="flex items-center gap-3 px-6 py-4 bg-base-200/50 border-b border-base-content/5 flex-none">
        <span :class="statusDotClass()" />
        <span class="text-sm font-semibold text-base-content/80">{{ instance.label }}</span>
        <span class="badge badge-sm" :class="instance.status === 'poisoned' ? 'badge-error' : instance.status === 'starting' ? 'badge-info' : instance.status === 'in_use' ? 'badge-success' : 'badge-ghost'">
          {{ statusLabel() }}
        </span>
        <span class="badge badge-sm badge-ghost">{{ instance.instanceType }}</span>
        <div class="flex-1" />
        <a v-if="instance.vncUrl && !stopped" :href="instance.vncUrl" target="_blank" rel="noopener"
           class="btn btn-ghost btn-sm gap-1 text-xs text-base-content/50 hover:text-base-content/70">
          <Icon icon="lucide:monitor" class="w-3.5 h-3.5" />
          VNC
        </a>
        <button v-if="canGoBack" class="btn btn-ghost btn-sm px-2" title="Back" @click="emit('back')">
          <Icon icon="lucide:arrow-left" class="w-4 h-4" />
        </button>
        <button class="btn btn-ghost btn-sm px-2" title="Close (Esc)" @click="emit('close')">
          <Icon icon="lucide:x" class="w-4 h-4" />
        </button>
      </div>

      <!-- Content -->
      <div class="flex-1 overflow-auto p-6 space-y-6">
        <!-- Full container recording -->
        <div v-if="instance.recordingPath">
          <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40 mb-3">Container Video</h3>
          <video :src="screenshotSrc(instance.recordingPath!)" controls preload="metadata"
                 class="w-full rounded-lg border border-base-content/10 bg-black" />
        </div>
        <div v-else-if="instance.setupSteps?.some(s => s.step === 'Starting video recording' && s.status === 'completed')">
          <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40 mb-3">Container Video</h3>
          <div class="w-full aspect-video bg-base-300 rounded-lg border border-base-content/10 flex items-center justify-center bg-black/30">
            <div class="text-center">
              <Icon icon="lucide:film" class="w-8 h-8 text-base-content/15 mx-auto mb-1.5" />
              <p class="text-xs text-base-content/25">Available after run completes</p>
            </div>
          </div>
        </div>

        <!-- Stats charts -->
        <div v-if="statsHistory.length > 1">
          <div class="flex items-center gap-2">
            <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40">Performance</h3>
            <button v-if="leaseMarkers.length > 0"
                    class="btn btn-ghost btn-xs px-1.5 text-base-content/30 hover:text-base-content/60"
                    :class="showLeaseMarkers ? 'text-info' : ''"
                    title="Toggle lease markers"
                    @click="showLeaseMarkers = !showLeaseMarkers">
              <Icon icon="lucide:git-branch" class="w-3 h-3" />
            </button>
            <button class="btn btn-ghost btn-xs px-1.5"
                    :class="isZoomed ? 'text-info hover:text-info/80' : 'text-base-content/30 hover:text-base-content/60'"
                    title="Reset zoom"
                    @click="resetChartZoom">
              <Icon icon="lucide:maximize-2" class="w-3 h-3" />
            </button>
          </div>
          <div class="text-[10px] text-base-content/20 mb-3">
            Drag to pan. Scroll to zoom Y-axis (Ctrl for X-axis).
          </div>
          <div class="grid grid-cols-2 gap-4">
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">CPU Usage</span>
              <span v-if="medianCpu != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.cpu }" :title="medianTooltip">{{ medianCpu.toFixed(1) }}%</span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="cpuChartData" :options="cpuOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">Memory</span>
              <span v-if="medianMem != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.mem }" :title="medianTooltip">
                {{ medianMem >= 1024 ? (medianMem / 1024).toFixed(1) + ' GB' : Math.round(medianMem) + ' MB' }}
              </span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="memChartData" :options="memOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">Ticks Per Second</span>
              <span v-if="medianTps != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.tps }" :title="medianTooltip">{{ medianTps.toFixed(1) }} TPS</span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="tpsChartData" :options="tpsOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">Frames Per Second</span>
              <span v-if="medianFps != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.fps }" :title="medianTooltip">{{ medianFps.toFixed(1) }} FPS</span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="fpsChartData" :options="fpsOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">Game Thread Queue</span>
              <span v-if="medianQueue != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.queue }" :title="medianTooltip">{{ medianQueue.toFixed(1) }}</span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="queueChartData" :options="queueOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">Thread Wait (ms)</span>
              <span v-if="medianWait != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.wait }" :title="medianTooltip">{{ medianWait.toFixed(1) }}ms</span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="waitChartData" :options="waitOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">GC Rate</span>
              <span v-if="medianGcRate != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.gc }" :title="medianTooltip">{{ medianGcRate.toFixed(1) }}/s</span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="gcRateChartData" :options="gcRateOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
            <div class="bg-base-200/30 rounded-lg p-3">
              <span class="text-[11px] text-base-content/40 font-medium">Network I/O</span>
              <span v-if="medianNetRx != null || medianNetTx != null" class="text-[11px] float-right font-mono median-hint" :style="{ color: METRIC_COLORS.netRx }" :title="medianTooltip">
                {{ medianNetRx != null ? formatBytesPerSec(medianNetRx) : '-' }}
              </span>
              <div class="h-32 mt-1 relative overflow-visible">
                <Line :ref="setChartRef" :data="netChartData" :options="netOptions as any" :plugins="[leaseLinePlugin, linkedPlugin]" />
              </div>
            </div>
          </div>
        </div>

        <!-- No stats yet -->
        <div v-else-if="instance.status !== 'starting'" class="text-sm text-base-content/30 italic">
          No performance data available yet
        </div>

        <!-- Container Log -->
        <ContainerLogViewer :instance-id="instance.instanceId" :history="instance.history" />

        <!-- Setup Steps -->
        <div v-if="setupSteps.length > 0">
          <div class="flex items-center gap-2 mb-3">
            <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40">Setup Steps</h3>
            <button class="btn btn-ghost btn-xs px-1.5 text-base-content/30 hover:text-base-content/60"
                    :class="timestampMode !== 'off' ? 'text-info' : ''"
                    :title="timestampTooltip"
                    @click="timestampMode = nextTimestampMode(timestampMode)">
              <Icon icon="lucide:clock" class="w-3 h-3" />
            </button>
          </div>
          <div class="space-y-1.5">
            <div v-for="s in setupSteps" :key="s.step"
                 class="rounded-lg border border-base-content/5 bg-base-200/50 overflow-hidden">
              <div class="flex items-center gap-2 px-3 py-1.5 cursor-pointer hover:bg-base-content/3"
                   @click="toggleStep(s.step)">
                <StatusIcon :status="s.status" :size="14" />
                <span class="text-xs font-medium flex-1 truncate">{{ s.step }}</span>
                <span v-if="s.details" class="text-[10px] text-base-content/35 truncate max-w-[40%]">{{ s.details }}</span>
                <Icon v-if="s.output" icon="lucide:chevron-right" class="w-3 h-3 text-base-content/30 transition-transform duration-150" :class="{ 'rotate-90': expandedSteps.has(s.step) }" />
              </div>
              <div v-if="expandedSteps.has(s.step) && s.output?.length"
                   class="font-mono text-[11px] leading-relaxed p-3 overflow-x-auto border-t border-base-content/5 bg-base-300/30">
                <table class="w-full border-collapse">
                  <template v-for="(entry, li) in s.output" :key="li">
                    <tr v-if="entry.type === 'annotation'">
                      <td class="text-right pr-3 select-none text-base-content/20 align-top w-[1%] whitespace-nowrap">{{ li + 1 }}</td>
                      <td class="pr-2 align-top w-[1%]">
                        <Icon :icon="annotationSourceIcon(entry.source)" class="w-3 h-3 mt-[3px]" :class="annotationSourceClass(entry.source)" />
                      </td>
                      <td v-if="timestampMode !== 'off'" class="text-right pr-1 select-none text-base-content/40 align-top whitespace-nowrap text-[10px] tabular-nums">{{ formatEntryTimestamp(entry.ts, timestampMode, anchorTimestampMs(s.output)) }}</td>
                      <td class="whitespace-pre-wrap break-words" :class="[annotationLevelClass(entry.level), isSuccessLine(entry.message) ? 'text-success' : '']">{{ entry.message }}</td>
                    </tr>
                  </template>
                </table>
              </div>
            </div>
          </div>
        </div>

        <!-- Connection info for clients -->
        <div v-if="!isServer && instance.connectedServerId" class="text-xs text-base-content/40">
          Connected to server: <span class="font-medium text-base-content/60" :title="instance.connectedServerId">{{ serverLabel ?? instance.connectedServerId }}</span>
        </div>

        <!-- Connected peers -->
        <div v-if="connectedPeers && connectedPeers.length > 0">
          <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40 mb-3">
            {{ isServer ? 'Connected Clients' : 'Connected Servers' }}
          </h3>
          <div class="flex flex-wrap gap-1.5">
            <button v-for="peer in connectedPeers" :key="peer.instanceId"
                    class="badge badge-sm gap-1.5 cursor-pointer hover:badge-primary transition-colors"
                    @click="emit('navigate-instance', peer.instanceId)">
              <Icon :icon="peer.instanceType === 'server' ? 'lucide:server' : 'lucide:monitor'" class="w-3 h-3" />
              {{ peer.label }}
            </button>
          </div>
        </div>

        <!-- Connection History -->
        <div v-if="instance.history && instance.history.length > 0">
          <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40 mb-3">Connection History</h3>
          <div class="space-y-1">
            <div v-for="(entry, i) in instance.history" :key="i"
                 :ref="(el) => setHistoryRow(el as Element | null, i)"
                 class="flex items-center gap-2.5 px-2 py-1 rounded hover:bg-base-content/3">
              <!-- Event dot -->
              <span class="w-2 h-2 rounded-full flex-none"
                    :class="entry.event === 'created' ? 'bg-info'
                          : entry.event === 'leased' || entry.event === 'connected' || entry.event === 'client_attached' ? 'bg-success'
                          : entry.event === 'poisoned' ? 'bg-error'
                          : 'bg-base-content/20'" />
              <!-- Event details -->
              <div class="flex-1 min-w-0 flex items-center gap-1 text-[11px]">
                <span class="font-medium text-base-content/60 flex-none">
                  {{ historyLabel(entry) }}
                </span>
                <button v-if="entry.event === 'leased' && entry.testName"
                        class="text-base-content/40 hover:text-info truncate min-w-0 cursor-pointer transition-colors"
                        :title="entry.testName"
                        @click.stop="emit('navigate-test', entry.testName!)">
                  {{ shortTestName(entry.testName) }}
                </button>
                <span v-if="entry.serverInstanceId" class="text-base-content/20 flex-none">·</span>
                <button v-if="entry.serverInstanceId"
                        class="text-base-content/35 hover:text-info flex-none whitespace-nowrap cursor-pointer transition-colors"
                        :title="`Open ${resolveLabel?.(entry.serverInstanceId) ?? entry.serverInstanceId}`"
                        @click.stop="emit('navigate-instance', entry.serverInstanceId!)">
                  {{ resolveLabel?.(entry.serverInstanceId) ?? entry.serverInstanceId }}
                </button>
                <span v-if="entry.clientInstanceId" class="text-base-content/20 flex-none">·</span>
                <button v-if="entry.clientInstanceId"
                        class="text-base-content/35 hover:text-info flex-none whitespace-nowrap cursor-pointer transition-colors"
                        :title="`Open ${resolveLabel?.(entry.clientInstanceId) ?? entry.clientInstanceId}`"
                        @click.stop="emit('navigate-instance', entry.clientInstanceId!)">
                  {{ resolveLabel?.(entry.clientInstanceId) ?? entry.clientInstanceId }}
                </button>
              </div>
              <!-- Timestamp -->
              <span class="text-[10px] text-base-content/25 tabular-nums flex-none">
                {{ formatHistoryTime(entry.timestamp) }}
              </span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Prev/next container nav arrows (outside modal card) -->
    <button class="absolute left-3 top-1/2 -translate-y-1/2 p-2 rounded-full bg-black/30 transition-colors"
            :class="hasPrev ? 'text-white/50 hover:text-white hover:bg-black/50 cursor-pointer' : 'text-white/15 cursor-default'"
            :disabled="!hasPrev"
            title="Previous container"
            @click.stop="hasPrev && emit('prev')">
      <Icon icon="lucide:chevron-left" class="w-6 h-6" />
    </button>
    <button class="absolute right-3 top-1/2 -translate-y-1/2 p-2 rounded-full bg-black/30 transition-colors"
            :class="hasNext ? 'text-white/50 hover:text-white hover:bg-black/50 cursor-pointer' : 'text-white/15 cursor-default'"
            :disabled="!hasNext"
            title="Next container"
            @click.stop="hasNext && emit('next')">
      <Icon icon="lucide:chevron-right" class="w-6 h-6" />
    </button>
  </div>
</template>

<style scoped>
.median-hint {
  cursor: help;
  text-decoration: underline dotted currentColor;
  text-underline-offset: 3px;
  text-decoration-thickness: 1px;
  opacity: 0.85;
}
.median-hint:hover {
  opacity: 1;
}
</style>
