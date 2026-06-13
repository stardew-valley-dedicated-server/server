<script setup lang="ts">
import { Icon } from "@iconify/vue";
import type { ChartData, ChartOptions } from "chart.js";
import { computed, reactive, ref, watch } from "vue";
import { Scatter } from "vue-chartjs";
import { useTestUI } from "../composables/useTestUI";
import type { InstanceSnapshot } from "../types/state";
import { GRID_COLOR, registerChartPlugins, TICK_COLOR, X_TICK_COLOR } from "../utils/chart";

registerChartPlugins();

const { store, inspect } = useTestUI();

const expanded = ref(true);

// Categories. Wire-event names match `state.instances[].history[].event`,
// which is populated by the store's `case 'instance_*'` handlers — the same
// canonical vocabulary as Schema/EventNames.cs's Instance* constants.
type CategoryKey = "created" | "leased" | "returned" | "poisoned" | "disposed";
const CATEGORIES: { key: CategoryKey; label: string; color: string }[] = [
    { key: "created", label: "created", color: "#22c55e" },
    { key: "leased", label: "leased", color: "#3b82f6" },
    { key: "returned", label: "returned", color: "#93c5fd" },
    { key: "poisoned", label: "poisoned", color: "#ef4444" },
    { key: "disposed", label: "disposed", color: "#9ca3af" },
];

const enabled = reactive<Record<CategoryKey, boolean>>({
    created: true,
    leased: true,
    returned: true,
    poisoned: true,
    disposed: true,
});

// Live + archived instances (post-run runs only have stoppedInstances).
// Sorted by creation time so row order reflects when each container started,
// regardless of which list it currently lives in. Mid-run disposals migrate
// from `state.instances` to `stoppedInstances`, so a naive concat puts a
// later-created live instance ahead of an earlier-created disposed one.
const allInstances = computed<InstanceSnapshot[]>(() => {
    const merged = [...(store.state.instances ?? []), ...store.stoppedInstances];
    return merged.slice().sort((a, b) => {
        const ta = a.history?.[0] ? new Date(a.history[0].timestamp).getTime() : Number.POSITIVE_INFINITY;
        const tb = b.history?.[0] ? new Date(b.history[0].timestamp).getTime() : Number.POSITIVE_INFINITY;
        return ta - tb;
    });
});

const runStartMs = computed<number | null>(() => {
    const iso = store.state.runStartTime;
    if (!iso) {
        return null;
    }
    const t = new Date(iso).getTime();
    return Number.isFinite(t) ? t : null;
});

interface TimelinePoint {
    x: number; // seconds since run start
    y: number; // instance row index
    instanceId: string;
    event: string;
    runMs: number;
    ts: string;
    testName: string | null;
    reason: string | null;
}

const chartData = computed<ChartData<"scatter", TimelinePoint[]>>(() => {
    const start = runStartMs.value;
    const instances = allInstances.value;

    // Build per-category point arrays in a single pass. Skip categories not in
    // the legend (the four extra history events — connected/disconnected/
    // client_attached/etc. — are intentionally not plotted).
    const buckets: Record<CategoryKey, TimelinePoint[]> = {
        created: [],
        leased: [],
        returned: [],
        poisoned: [],
        disposed: [],
    };

    instances.forEach((inst, y) => {
        if (!inst.history) {
            return;
        }
        for (const entry of inst.history) {
            const cat = entry.event as CategoryKey;
            if (!(cat in buckets)) {
                continue;
            }
            if (!enabled[cat]) {
                continue;
            }
            const t = new Date(entry.timestamp).getTime();
            if (!Number.isFinite(t)) {
                continue;
            }
            const runMs = start != null ? t - start : 0;
            buckets[cat].push({
                x: runMs / 1000,
                y,
                instanceId: inst.instanceId,
                event: entry.event,
                runMs,
                ts: entry.timestamp,
                testName: entry.testName,
                reason: entry.reason,
            });
        }
    });

    return {
        datasets: CATEGORIES.map((cat) => ({
            label: cat.label,
            data: buckets[cat.key],
            backgroundColor: cat.color,
            borderColor: cat.color,
            pointRadius: 4,
            pointHoverRadius: 6,
        })),
    };
});

const rowCount = computed(() => Math.max(1, allInstances.value.length));

const chartHeight = computed(() => Math.min(28 + rowCount.value * 22, 260));

const chartOptions = computed<ChartOptions<"scatter">>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    animation: { duration: 0 },
    layout: { padding: { left: 4, right: 8, top: 4, bottom: 4 } },
    scales: {
        x: {
            type: "linear",
            position: "bottom",
            min: 0,
            ticks: {
                color: X_TICK_COLOR,
                font: { size: 9 },
                maxTicksLimit: 8,
                callback: (v) => `${Number(v).toFixed(0)}s`,
            },
            grid: { color: GRID_COLOR },
        },
        y: {
            type: "linear",
            min: -0.5,
            max: rowCount.value - 0.5,
            reverse: true,
            ticks: {
                color: TICK_COLOR,
                font: { size: 10 },
                stepSize: 1,
                autoSkip: false,
                callback: (v) => {
                    const i = Number(v);
                    if (!Number.isInteger(i)) {
                        return "";
                    }
                    const inst = allInstances.value[i];
                    return inst?.label || inst?.instanceId || "";
                },
            },
            grid: { color: GRID_COLOR },
        },
    },
    plugins: {
        legend: { display: false },
        tooltip: {
            backgroundColor: "rgba(0,0,0,0.85)",
            callbacks: {
                title: () => "",
                label: (ctx) => {
                    const p = ctx.raw as TimelinePoint;
                    const inst = allInstances.value[p.y];
                    const name = inst?.label || p.instanceId;
                    const detail = p.testName ?? p.reason;
                    const tail = detail ? ` · ${detail}` : "";
                    return `${name} · ${p.event} @ ${(p.runMs / 1000).toFixed(2)}s${tail}`;
                },
            },
        },
        zoom: {
            pan: { enabled: true, mode: "x" as const },
            zoom: {
                wheel: { enabled: true, speed: 0.05 },
                pinch: { enabled: true },
                mode: "x" as const,
            },
            limits: { x: { min: 0, minRange: 1 } },
        },
    },
    onClick: (_evt, elements) => {
        if (elements.length === 0) {
            return;
        }
        const first = elements[0];
        const ds = chartData.value.datasets[first.datasetIndex];
        const point = ds?.data[first.index] as TimelinePoint | undefined;
        if (!point) {
            return;
        }
        store.state.currentRunMs = point.runMs;
        inspect.pushInspect(point.instanceId);
    },
}));

// Chart.js doesn't reactively redraw when the y-axis label callback closure
// changes; force a remount when the instance set grows or reorders.
const renderKey = ref(0);
watch(
    () => allInstances.value.map((i) => i.instanceId).join("|"),
    () => {
        renderKey.value++;
    },
);
</script>

<template>
  <div class="border-b border-base-content/5 bg-base-200/40 flex-none">
    <button class="w-full flex items-center gap-2 px-4 py-1.5 text-left hover:bg-base-content/3 transition-colors"
            @click="expanded = !expanded">
      <Icon icon="lucide:chevron-right" class="w-3 h-3 text-base-content/30 transition-transform duration-150"
            :class="{ 'rotate-90': expanded }" />
      <Icon icon="lucide:activity" class="w-3.5 h-3.5 text-base-content/40" />
      <span class="text-[11px] font-semibold uppercase tracking-widest text-base-content/40">Infrastructure Timeline</span>
      <span v-if="expanded && allInstances.length" class="text-[10px] text-base-content/30 tabular-nums">{{ allInstances.length }} instances</span>

      <div v-if="expanded" class="ml-auto flex items-center gap-1.5" @click.stop>
        <button v-for="cat in CATEGORIES" :key="cat.key"
                class="text-[10px] px-1.5 py-0.5 rounded border transition-colors flex items-center gap-1"
                :class="enabled[cat.key]
                  ? 'border-base-content/20 text-base-content/70'
                  : 'border-base-content/5 text-base-content/25'"
                :title="enabled[cat.key] ? `Hide ${cat.label}` : `Show ${cat.label}`"
                @click="enabled[cat.key] = !enabled[cat.key]">
          <span class="w-1.5 h-1.5 rounded-full" :style="{ backgroundColor: enabled[cat.key] ? cat.color : 'transparent', borderColor: cat.color, borderWidth: '1px', borderStyle: 'solid' }" />
          {{ cat.label }}
        </button>
      </div>
    </button>

    <div v-if="expanded && allInstances.length > 0"
         :style="{ height: `${chartHeight}px` }"
         class="px-2">
      <Scatter :key="renderKey" :data="chartData" :options="chartOptions" />
    </div>
    <div v-else-if="expanded" class="px-4 py-2 text-[11px] text-base-content/30 italic">
      No instance events yet
    </div>
  </div>
</template>
