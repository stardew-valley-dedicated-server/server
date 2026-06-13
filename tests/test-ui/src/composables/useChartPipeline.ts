import { type ComputedRef, computed, onUnmounted, type Ref, ref, watch } from "vue";
import type { InstanceSnapshot, StatsSnapshotEntry } from "../types/state";
import { colorFor, formatTimeLabel } from "../utils/chart";

const THROTTLE_MS = 2000;
const GRID_STEP = 1000;

export function useChartPipeline(params: {
    filteredInstances: ComputedRef<InstanceSnapshot[]>;
    allInstances: ComputedRef<InstanceSnapshot[]>;
    instanceStatsHistory: Map<string, StatsSnapshotEntry[]>;
    statsVersion: Ref<number>;
}) {
    // Track which instances have stats (updated on flush, not reactively)
    const instancesWithStats = ref(new Set<string>());

    const chartInstances = computed(() =>
        params.filteredInstances.value.filter((i) => instancesWithStats.value.has(i.instanceId)),
    );

    // Deduplicate label for legend (append index if collides)
    function deduplicatedLabels(): Map<string, string> {
        const counts = new Map<string, number>();
        const result = new Map<string, string>();
        for (const inst of chartInstances.value) {
            const base = inst.label;
            const n = (counts.get(base) ?? 0) + 1;
            counts.set(base, n);
            result.set(inst.instanceId, n > 1 ? `${base} #${n}` : base);
        }
        for (const inst of chartInstances.value) {
            const base = inst.label;
            if ((counts.get(base) ?? 0) > 1 && result.get(inst.instanceId) === base) {
                result.set(inst.instanceId, `${base} #1`);
            }
        }
        return result;
    }

    // Pipeline state (mutable, not reactive)
    let peakScanned = new Map<string, number>();
    let epochCache = new Map<string, number[]>();
    let cachedGridMin = 0;
    let cachedGridMax = 0;
    let cachedGridEpochs: number[] = [];
    let cachedGridLabels: string[] = [];

    // Peaks (reactive, drive chart options for y-axis max)
    const peakCpu = ref(0);
    const peakMem = ref(0);
    const peakTps = ref(0);
    const peakFps = ref(0);
    const peakQueue = ref(0);
    const peakWait = ref(0);
    const peakGcRate = ref(0);
    const peakNetRx = ref(0);
    const peakNetTx = ref(0);

    // Chart data outputs, reassigned on each throttled flush
    const cpuChartData = ref<any>({ labels: [], datasets: [] });
    const memChartData = ref<any>({ labels: [], datasets: [] });
    const tpsChartData = ref<any>({ labels: [], datasets: [] });
    const fpsChartData = ref<any>({ labels: [], datasets: [] });
    const queueChartData = ref<any>({ labels: [], datasets: [] });
    const waitChartData = ref<any>({ labels: [], datasets: [] });
    const gcChartData = ref<any>({ labels: [], datasets: [] });
    const netChartData = ref<any>({ labels: [], datasets: [] });

    function rebuildPipeline() {
        peakScanned = new Map();
        epochCache = new Map();
        cachedGridMin = 0;
        cachedGridMax = 0;
        cachedGridEpochs = [];
        cachedGridLabels = [];
        peakCpu.value = 0;
        peakMem.value = 0;
        peakTps.value = 0;
        peakFps.value = 0;
        peakQueue.value = 0;
        peakWait.value = 0;
        peakGcRate.value = 0;
        peakNetRx.value = 0;
        peakNetTx.value = 0;
        flushChartData();
    }

    function getEpochs(instanceId: string, history: StatsSnapshotEntry[]): number[] {
        let cached = epochCache.get(instanceId);
        if (!cached || cached.length > history.length) {
            cached = history.map((h) => new Date(h.timestamp).getTime());
        } else {
            for (let i = cached.length; i < history.length; i++) {
                cached.push(new Date(history[i].timestamp).getTime());
            }
        }
        epochCache.set(instanceId, cached);
        return cached;
    }

    function flushChartData() {
        // Update which instances have stats (drives chartInstances computed)
        const newSet = new Set<string>();
        for (const inst of params.filteredInstances.value) {
            const h = params.instanceStatsHistory.get(inst.instanceId);
            if (h && h.length > 0) {
                newSet.add(inst.instanceId);
            }
        }
        if (
            newSet.size !== instancesWithStats.value.size ||
            [...newSet].some((id) => !instancesWithStats.value.has(id))
        ) {
            instancesWithStats.value = newSet;
        }

        const instances = chartInstances.value;

        // Update peaks incrementally. Only scan new entries since last flush.
        for (const inst of instances) {
            const h = params.instanceStatsHistory.get(inst.instanceId) ?? [];
            const scanned = peakScanned.get(inst.instanceId) ?? 0;
            for (let i = scanned; i < h.length; i++) {
                const s = h[i];
                if (s.cpuPercent > peakCpu.value) {
                    peakCpu.value = s.cpuPercent;
                }
                const mem = s.gameMemoryMb ?? s.memoryMb;
                if (mem > peakMem.value) {
                    peakMem.value = mem;
                }
                if (s.tps != null && s.tps > peakTps.value) {
                    peakTps.value = s.tps;
                }
                if (s.fps != null && s.fps > peakFps.value) {
                    peakFps.value = s.fps;
                }
                if (s.pendingActions != null && s.pendingActions > peakQueue.value) {
                    peakQueue.value = s.pendingActions;
                }
                if (s.gameThreadWaitMs != null && s.gameThreadWaitMs > peakWait.value) {
                    peakWait.value = s.gameThreadWaitMs;
                }
                if (s.gcRate != null && s.gcRate > peakGcRate.value) {
                    peakGcRate.value = s.gcRate;
                }
                if (s.netRxBytesPerSec != null && s.netRxBytesPerSec > peakNetRx.value) {
                    peakNetRx.value = s.netRxBytesPerSec;
                }
                if (s.netTxBytesPerSec != null && s.netTxBytesPerSec > peakNetTx.value) {
                    peakNetTx.value = s.netTxBytesPerSec;
                }
            }
            peakScanned.set(inst.instanceId, h.length);
        }

        // Find global time range using cached epoch arrays (avoids re-parsing)
        let globalMin = Number.POSITIVE_INFINITY,
            globalMax = Number.NEGATIVE_INFINITY;
        for (const inst of instances) {
            const h = params.instanceStatsHistory.get(inst.instanceId) ?? [];
            if (h.length === 0) {
                continue;
            }
            const epochs = getEpochs(inst.instanceId, h);
            if (epochs[0] < globalMin) {
                globalMin = epochs[0];
            }
            if (epochs[epochs.length - 1] > globalMax) {
                globalMax = epochs[epochs.length - 1];
            }
        }

        if (globalMin > globalMax) {
            cpuChartData.value = { labels: [], datasets: [] };
            memChartData.value = { labels: [], datasets: [] };
            tpsChartData.value = { labels: [], datasets: [] };
            fpsChartData.value = { labels: [], datasets: [] };
            queueChartData.value = { labels: [], datasets: [] };
            waitChartData.value = { labels: [], datasets: [] };
            gcChartData.value = { labels: [], datasets: [] };
            netChartData.value = { labels: [], datasets: [] };
            return;
        }

        // Rebuild grid and labels only when bounds change
        if (globalMin !== cachedGridMin || globalMax !== cachedGridMax) {
            cachedGridMin = globalMin;
            cachedGridMax = globalMax;
            const gridLen = Math.floor((globalMax - globalMin) / GRID_STEP) + 1;
            cachedGridEpochs = [];
            for (let i = 0; i < gridLen; i++) {
                cachedGridEpochs.push(globalMin + i * GRID_STEP);
            }
            cachedGridLabels = cachedGridEpochs.map((t) => formatTimeLabel(new Date(t).toISOString()));
        }
        const gridEpochs = cachedGridEpochs;
        const labels = cachedGridLabels;

        const labelMap = deduplicatedLabels();

        type MetricKey = "cpu" | "mem" | "tps" | "fps" | "queue" | "wait" | "gc" | "netRx" | "netTx";
        const extractors: Record<MetricKey, (s: StatsSnapshotEntry) => number | null> = {
            cpu: (s) => s.cpuPercent,
            mem: (s) => s.gameMemoryMb ?? s.memoryMb,
            tps: (s) => s.tps,
            fps: (s) => s.fps,
            queue: (s) => s.pendingActions,
            wait: (s) => s.gameThreadWaitMs,
            gc: (s) => s.gcRate,
            netRx: (s) => s.netRxBytesPerSec,
            netTx: (s) => s.netTxBytesPerSec,
        };

        const allDatasets: Record<MetricKey, any[]> = {
            cpu: [],
            mem: [],
            tps: [],
            fps: [],
            queue: [],
            wait: [],
            gc: [],
            netRx: [],
            netTx: [],
        };

        for (const inst of instances) {
            const history = params.instanceStatsHistory.get(inst.instanceId) ?? [];
            const color = colorFor(inst.instanceId, inst.instanceType, params.allInstances.value);
            const isServer = inst.instanceType === "server";
            const instLabel = labelMap.get(inst.instanceId) ?? inst.label;

            const histEpochs = getEpochs(inst.instanceId, history);
            const instStart = histEpochs.length > 0 ? histEpochs[0] : Number.POSITIVE_INFINITY;
            const instEnd = histEpochs.length > 0 ? histEpochs[histEpochs.length - 1] : Number.NEGATIVE_INFINITY;

            // Sample-and-hold: one scan per instance, shared across all metrics.
            // For each grid point, find the most recent history entry at or before it.
            const sampled: (StatsSnapshotEntry | null)[] = [];
            let hi = 0;
            for (const t of gridEpochs) {
                if (t < instStart || t > instEnd + GRID_STEP) {
                    sampled.push(null);
                    continue;
                }
                while (hi < histEpochs.length - 1 && histEpochs[hi + 1] <= t) {
                    hi++;
                }
                sampled.push(hi < histEpochs.length && histEpochs[hi] <= t ? history[hi] : null);
            }

            for (const metric of ["cpu", "mem", "tps", "fps", "queue", "wait", "gc", "netRx", "netTx"] as MetricKey[]) {
                const extract = extractors[metric];
                const isStacked = metric === "cpu" || metric === "mem";
                const fillMissing = isStacked ? 0 : null;

                const data = sampled.map((entry) => (entry ? extract(entry) : fillMissing));

                // netTx shares the net panel with netRx: use a dashed line to distinguish TX from RX within the same instance,
                // and tag the label so the legend reads "<instance> RX" / "<instance> TX".
                const isNetTx = metric === "netTx";
                const isNetRx = metric === "netRx";
                const metricLabel = isNetRx ? `${instLabel} RX` : isNetTx ? `${instLabel} TX` : instLabel;
                const baseDash = isStacked || isServer ? ([] as number[]) : [4, 2];
                const borderDash = isNetTx ? [2, 2] : baseDash;

                allDatasets[metric].push({
                    label: metricLabel,
                    data,
                    borderColor: color,
                    backgroundColor: isStacked ? color.replace("rgb(", "rgba(").replace(")", ", 0.25)") : "transparent",
                    fill: isStacked,
                    borderWidth: isServer ? (isStacked ? 2 : 2.5) : isStacked ? 1 : 1.5,
                    borderDash,
                    spanGaps: !isStacked,
                });
            }
        }

        cpuChartData.value = { labels, datasets: allDatasets.cpu };
        memChartData.value = { labels, datasets: allDatasets.mem };
        tpsChartData.value = { labels, datasets: allDatasets.tps };
        fpsChartData.value = { labels, datasets: allDatasets.fps };
        queueChartData.value = { labels, datasets: allDatasets.queue };
        waitChartData.value = { labels, datasets: allDatasets.wait };
        gcChartData.value = { labels, datasets: allDatasets.gc };
        // Net panel: RX (solid) + TX (dashed), per instance. Interleave RX/TX so each instance's pair sits together in the legend.
        const netDatasets: any[] = [];
        for (let i = 0; i < allDatasets.netRx.length; i++) {
            netDatasets.push(allDatasets.netRx[i], allDatasets.netTx[i]);
        }
        netChartData.value = { labels, datasets: netDatasets };
    }

    // Throttled flush: watch statsVersion, debounce via setTimeout
    let flushTimer: ReturnType<typeof setTimeout> | null = null;

    function scheduleFlush() {
        if (flushTimer != null) {
            return;
        }
        flushTimer = setTimeout(() => {
            flushTimer = null;
            flushChartData();
        }, THROTTLE_MS);
    }

    watch(
        () => params.statsVersion.value,
        () => {
            scheduleFlush();
        },
    );

    // Initial flush (synchronous, data is ready before charts mount)
    flushChartData();

    // Clean up on unmount
    onUnmounted(() => {
        if (flushTimer != null) {
            clearTimeout(flushTimer);
            flushTimer = null;
        }
    });

    return {
        instancesWithStats,
        chartInstances,
        peakCpu,
        peakMem,
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
    };
}
