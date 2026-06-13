<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { Chart } from "chart.js";
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { Line } from "vue-chartjs";
import { useChartPipeline } from "../composables/useChartPipeline";
import { useLinkedCharts } from "../composables/useLinkedCharts";
import { useSyncedZoom } from "../composables/useSyncedZoom";
import { useTestUI } from "../composables/useTestUI";
import {
    applyDatasetHighlight,
    buildZoomConfig,
    chartElementDefaults,
    chartLayoutDefaults,
    chartScaleDefaults,
    memTickCallback,
    niceCeil,
    pctTickCallback,
    registerChartPlugins,
} from "../utils/chart";
import { DEFAULT_TARGET_TPS, formatBytesPerSec, TPS_PEAK_HEADROOM, TPS_TARGET_HEADROOM } from "../utils/metrics";

registerChartPlugins();

const { plugin: linkedPlugin, setChartRef: setLinkedChartRef } = useLinkedCharts();

const { store } = useTestUI();

type FilterMode = "all" | "server" | "client";
const filter = ref<FilterMode>("all");
const chartsLinked = ref(true);

const {
    charts: chartEls,
    trackChartRef: trackZoomRef,
    syncAllAxes,
    resetAll: doResetAllZoom,
    isZoomed,
    ctrlHeld,
} = useSyncedZoom({ enabled: chartsLinked });

function trackChartRef(el: any) {
    setLinkedChartRef(el);
    trackZoomRef(el);
}

// Chart.js renders with size 0 when its canvas mounts inside a v-else branch
// that just became visible. The browser hasn't completed layout yet when
// vue-chartjs calls new Chart() in its onMounted hook. A setTimeout(0) defers
// the resize+update to after the browser's layout/paint cycle, letting Chart.js
// pick up the correct container dimensions.
onMounted(() => {
    setTimeout(() => {
        for (const c of chartEls) {
            const chart = c?.chart;
            if (chart) {
                chart.resize();
                chart.update("none");
            }
        }
    }, 0);
});

// ── Persist zoom across filter changes ──
interface ZoomState {
    xMin?: number;
    xMax?: number;
    yMin?: number;
    yMax?: number;
}
let savedZoom: ZoomState[] = [];

watch(filter, () => {
    savedZoom = chartEls.map((c) => {
        const chart = c?.chart;
        if (!chart) {
            return {};
        }
        return {
            xMin: chart.scales?.x?.min,
            xMax: chart.scales?.x?.max,
            yMin: chart.scales?.y?.min,
            yMax: chart.scales?.y?.max,
        };
    });
    rebuildPipeline();
    nextTick(() => {
        for (let i = 0; i < chartEls.length; i++) {
            const chart = chartEls[i]?.chart;
            const z = savedZoom[i];
            if (!chart || !z || z.xMin == null) {
                continue;
            }
            if (z.xMin != null && z.xMax != null) {
                chart.scales.x.options.min = z.xMin;
                chart.scales.x.options.max = z.xMax;
            }
            if (z.yMin != null && z.yMax != null) {
                chart.scales.y.options.min = z.yMin;
                chart.scales.y.options.max = z.yMax;
            }
            chart.update("none");
        }
    });
});

const allInstances = computed(() => {
    const live = store.state.instances ?? [];
    const stopped = store.stoppedInstances;
    const liveIds = new Set(live.map((i) => i.instanceId));
    return [...live, ...stopped.filter((i) => !liveIds.has(i.instanceId))];
});

const filteredInstances = computed(() => {
    if (filter.value === "all") {
        return allInstances.value;
    }
    return allInstances.value.filter((i) => i.instanceType === filter.value);
});

const pipeline = useChartPipeline({
    filteredInstances,
    allInstances,
    instanceStatsHistory: store.instanceStatsHistory,
    statsVersion: store.statsVersion,
});
const {
    chartInstances,
    peakTps,
    peakFps,
    peakQueue,
    peakWait,
    peakGcRate,
    peakNetRx,
    peakNetTx,
    cpuChartData,
    memChartData,
    tpsChartData,
    fpsChartData,
    queueChartData,
    waitChartData,
    gcChartData,
    netChartData,
    rebuildPipeline,
} = pipeline;

// ── Plugins ──
// Only clamp y-axis to 0 for stacked charts (CPU/Memory).
// Non-stacked charts (TPS/FPS) can zoom freely into any range.
const pinYZeroPlugin = {
    id: "pinYZero",
    beforeLayout(chart: any) {
        const yOpts = chart.options?.scales?.y;
        if (yOpts?.stacked) {
            yOpts.min = 0;
        }
    },
    afterDataLimits(_chart: any, args: any) {
        if (args.scale?.id === "y" && args.scale.options?.stacked && args.scale.min < 0) {
            args.scale.min = 0;
        }
    },
};

const highlightPlugin = {
    id: "hoverHighlight",
    beforeDraw(chart: any) {
        const active = chart.getActiveElements();
        applyDatasetHighlight(chart, active.length > 0 ? active[0].datasetIndex : null);
    },
};

// ── Chart options ──
const baseOptions = {
    ...chartLayoutDefaults,
    interaction: {
        mode: "nearest" as const,
        intersect: false,
    },
    plugins: {
        legend: {
            display: true,
            position: "top" as const,
            labels: {
                color: "rgba(255,255,255,0.5)",
                font: { size: 9 },
                boxWidth: 16,
                boxHeight: 2,
                padding: 6,
                usePointStyle: false,
                generateLabels(chart: any) {
                    const items = Chart.defaults.plugins.legend.labels.generateLabels(chart);
                    for (const it of items) {
                        if (it.hidden) {
                            it.fontColor = "rgba(255,255,255,0.9)";
                        }
                    }
                    return items;
                },
            },
            onHover(_: any, legendItem: any, legend: any) {
                legend.chart.canvas.style.cursor = "pointer";
                applyDatasetHighlight(legend.chart, legendItem.datasetIndex);
                legend.chart.draw();
            },
            onLeave(_: any, __: any, legend: any) {
                legend.chart.canvas.style.cursor = "default";
                applyDatasetHighlight(legend.chart, null);
                legend.chart.draw();
            },
        },
        tooltip: {
            backgroundColor: "rgba(0,0,0,0.85)",
            titleFont: { size: 10 },
            bodyFont: { size: 10 },
            bodySpacing: 2,
            padding: 8,
            itemSort: (a: any, b: any) => (b.raw ?? 0) - (a.raw ?? 0),
            callbacks: {
                label: (ctx: any) => {
                    const v = ctx.raw as number | null;
                    if (v == null) {
                        return "";
                    }
                    return `${ctx.dataset.label}: ${Math.round(v * 10) / 10}`;
                },
            },
        },
        zoom: buildZoomConfig({ ctrlHeld, onSync: syncAllAxes }),
    },
    scales: { ...chartScaleDefaults },
    elements: {
        ...chartElementDefaults,
        point: { ...chartElementDefaults.point, hitRadius: 15 },
        line: { ...chartElementDefaults.line, tension: 0 },
    },
};

const cpuOptions = computed(() => ({
    ...baseOptions,
    plugins: {
        ...baseOptions.plugins,
        legend: { ...baseOptions.plugins.legend, display: showLegend.value },
        zoom: {
            ...baseOptions.plugins.zoom,
            limits: { ...baseOptions.plugins.zoom.limits, y: { min: 0, minRange: 1 } },
        },
    },
    scales: {
        ...baseOptions.scales,
        x: { ...baseOptions.scales.x, stacked: true },
        y: {
            ...baseOptions.scales.y,
            stacked: true,
            min: 0,
            ticks: { ...baseOptions.scales.y.ticks, precision: 0, callback: pctTickCallback },
        },
    },
}));

const memOptions = computed(() => ({
    ...baseOptions,
    plugins: {
        ...baseOptions.plugins,
        legend: { ...baseOptions.plugins.legend, display: showLegend.value },
        zoom: {
            ...baseOptions.plugins.zoom,
            limits: { ...baseOptions.plugins.zoom.limits, y: { min: 0, minRange: 1 } },
        },
    },
    scales: {
        ...baseOptions.scales,
        x: { ...baseOptions.scales.x, stacked: true },
        y: {
            ...baseOptions.scales.y,
            stacked: true,
            min: 0,
            ticks: {
                ...baseOptions.scales.y.ticks,
                precision: 0,
                callback: memTickCallback,
            },
        },
    },
}));

const tpsOptions = computed(() => {
    const max = niceCeil(Math.max(peakTps.value * TPS_PEAK_HEADROOM, DEFAULT_TARGET_TPS * TPS_TARGET_HEADROOM), 10);
    return {
        ...baseOptions,
        plugins: { ...baseOptions.plugins, legend: { ...baseOptions.plugins.legend, display: showLegend.value } },
        scales: {
            ...baseOptions.scales,
            y: { ...baseOptions.scales.y, min: 0, max },
        },
    };
});

const fpsOptions = computed(() => {
    const max = niceCeil(Math.max(peakFps.value * TPS_PEAK_HEADROOM, DEFAULT_TARGET_TPS * TPS_TARGET_HEADROOM), 10);
    return {
        ...baseOptions,
        plugins: { ...baseOptions.plugins, legend: { ...baseOptions.plugins.legend, display: showLegend.value } },
        scales: {
            ...baseOptions.scales,
            y: { ...baseOptions.scales.y, min: 0, max },
        },
    };
});

const queueOptions = computed(() => {
    const max = niceCeil(Math.max(peakQueue.value * 1.15, 10), 5);
    return {
        ...baseOptions,
        plugins: { ...baseOptions.plugins, legend: { ...baseOptions.plugins.legend, display: showLegend.value } },
        scales: { ...baseOptions.scales, y: { ...baseOptions.scales.y, min: 0, max } },
    };
});

const waitOptions = computed(() => {
    const max = niceCeil(Math.max(peakWait.value * 1.15, 100), 50);
    return {
        ...baseOptions,
        plugins: { ...baseOptions.plugins, legend: { ...baseOptions.plugins.legend, display: showLegend.value } },
        scales: {
            ...baseOptions.scales,
            y: {
                ...baseOptions.scales.y,
                min: 0,
                max,
                ticks: { ...baseOptions.scales.y.ticks, callback: (v: number) => `${v}ms` },
            },
        },
    };
});

const gcOptions = computed(() => {
    const max = niceCeil(Math.max(peakGcRate.value * 1.15, 5), 2);
    return {
        ...baseOptions,
        plugins: { ...baseOptions.plugins, legend: { ...baseOptions.plugins.legend, display: showLegend.value } },
        scales: { ...baseOptions.scales, y: { ...baseOptions.scales.y, min: 0, max } },
    };
});

const netOptions = computed(() => {
    const peakNet = Math.max(peakNetRx.value, peakNetTx.value);
    const max = niceCeil(Math.max(peakNet * 1.15, 1024), 1024);
    return {
        ...baseOptions,
        plugins: {
            ...baseOptions.plugins,
            legend: { ...baseOptions.plugins.legend, display: showLegend.value },
            tooltip: {
                ...baseOptions.plugins.tooltip,
                callbacks: {
                    label: (ctx: any) => {
                        const v = ctx.raw as number | null;
                        if (v == null) {
                            return "";
                        }
                        return `${ctx.dataset.label}: ${formatBytesPerSec(v)}`;
                    },
                },
            },
        },
        scales: {
            ...baseOptions.scales,
            y: {
                ...baseOptions.scales.y,
                min: 0,
                max,
                ticks: { ...baseOptions.scales.y.ticks, callback: (v: number) => formatBytesPerSec(v) },
            },
        },
    };
});

const showLegend = computed(() => chartInstances.value.length > 1);

const chartPlugins = computed(() => {
    const plugins: any[] = [pinYZeroPlugin, highlightPlugin];
    if (chartsLinked.value) {
        plugins.push(linkedPlugin);
    }
    return plugins;
});
</script>

<template>
  <div class="flex-1 flex flex-col overflow-hidden p-4 gap-2">
    <!-- Filter bar -->
    <div class="flex items-center gap-3 flex-wrap flex-none">
      <h3 class="text-xs font-semibold uppercase tracking-widest text-base-content/40">
        Combined Performance
      </h3>
      <div class="join join-horizontal">
        <button class="join-item btn btn-ghost btn-xs px-2"
                :class="filter === 'all' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                @click="filter = 'all'">All</button>
        <button class="join-item btn btn-ghost btn-xs px-2"
                :class="filter === 'server' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                @click="filter = 'server'">Servers</button>
        <button class="join-item btn btn-ghost btn-xs px-2"
                :class="filter === 'client' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                @click="filter = 'client'">Clients</button>
      </div>
      <span class="text-[10px] text-base-content/25">
        {{ chartInstances.length }} instance{{ chartInstances.length === 1 ? '' : 's' }}
      </span>
      <div class="flex-1" />
      <button class="btn btn-ghost btn-xs gap-1 text-[10px]"
              :class="chartsLinked ? 'text-success/70 hover:text-success' : 'text-base-content/30 hover:text-base-content/60'"
              :title="chartsLinked ? 'Charts linked: zoom/hover synced across all charts' : 'Charts unlinked: zoom/hover independent'"
              @click="chartsLinked = !chartsLinked">
        <Icon :icon="chartsLinked ? 'lucide:link' : 'lucide:unlink'" class="w-3 h-3" />
        {{ chartsLinked ? 'Linked' : 'Unlinked' }}
      </button>
      <button class="btn btn-ghost btn-xs gap-1 text-[10px]"
              :class="isZoomed ? 'text-info hover:text-info/80' : 'text-base-content/30 hover:text-base-content/60'"
              title="Reset zoom on all charts"
              @click="doResetAllZoom">
        <Icon icon="lucide:maximize-2" class="w-3 h-3" />
        Reset zoom
      </button>
    </div>

    <!-- Hint -->
    <div v-if="chartInstances.length > 0" class="text-[10px] text-base-content/20 flex-none">
      Drag to pan. Scroll to zoom Y-axis (Ctrl for X-axis). Click legend to hide/show. Servers = solid, Clients = dashed.
    </div>

    <!-- No data state -->
    <div v-if="chartInstances.length === 0"
         class="flex-1 flex flex-col items-center justify-center text-base-content/30 gap-2">
      <Icon icon="lucide:bar-chart-3" class="w-8 h-8 opacity-20" />
      <span class="text-sm">No performance data yet</span>
    </div>

    <!-- 4x2 chart grid. Fills remaining space, each chart gets equal share. -->
    <div v-else class="flex-1 grid grid-cols-2 auto-rows-fr gap-3 min-h-0 overflow-auto">
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">CPU Usage</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="cpuChartData" :options="cpuOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">Memory</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="memChartData" :options="memOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">Ticks Per Second</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="tpsChartData" :options="tpsOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">Frames Per Second</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="fpsChartData" :options="fpsOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">Game Thread Queue</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="queueChartData" :options="queueOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">Thread Wait (ms)</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="waitChartData" :options="waitOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">GC Rate</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="gcChartData" :options="gcOptions as any" :plugins="chartPlugins" /></div>
      </div>
      <div class="bg-base-200/30 rounded-lg p-3 flex flex-col min-h-[160px]">
        <span class="text-[11px] text-base-content/40 font-medium flex-none">Network I/O (RX solid, TX dashed)</span>
        <div class="flex-1 min-h-0 mt-1"><Line :ref="trackChartRef" :data="netChartData" :options="netOptions as any" :plugins="chartPlugins" /></div>
      </div>
    </div>
  </div>
</template>
