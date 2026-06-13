import { onMounted, onUnmounted, type Ref, readonly, ref } from "vue";

/**
 * Synchronizes zoom/pan across a group of Chart.js instances.
 *
 * - Wheel zooms Y; Ctrl+wheel zooms X (via `ctrlHeld` consumed by buildZoomConfig).
 * - Pan/zoom on any chart mirrors x-range to all others, with y-range scaled
 *   proportionally to each chart's recorded base y-max so peaks stay comparable.
 * - Stacked-y charts keep y.min pinned to 0.
 *
 * Usage:
 *   const { trackChartRef, syncAllAxes, resetAll, isZoomed, ctrlHeld, charts } = useSyncedZoom()
 *   // Pass `syncAllAxes` as the onSync callback to buildZoomConfig.
 *   // Call `trackChartRef(el)` from each <Line :ref="...">.
 */
export function useSyncedZoom(opts?: { enabled?: Ref<boolean> }) {
    const enabled = opts?.enabled ?? ref(true);

    const chartEls: any[] = [];

    function pruneDead() {
        for (let i = chartEls.length - 1; i >= 0; i--) {
            if (!chartEls[i]?.chart) {
                chartEls.splice(i, 1);
            }
        }
    }

    function trackChartRef(el: any) {
        if (el == null) {
            pruneDead();
            return;
        }
        if (!chartEls.includes(el)) {
            chartEls.push(el);
        }
    }

    const ctrlHeld = ref(false);
    function onKeyDown(e: KeyboardEvent) {
        if (e.key === "Control") {
            ctrlHeld.value = true;
        }
    }
    function onKeyUp(e: KeyboardEvent) {
        if (e.key === "Control") {
            ctrlHeld.value = false;
        }
    }

    onMounted(() => {
        window.addEventListener("keydown", onKeyDown);
        window.addEventListener("keyup", onKeyUp);
    });
    onUnmounted(() => {
        window.removeEventListener("keydown", onKeyDown);
        window.removeEventListener("keyup", onKeyUp);
        chartEls.length = 0;
    });

    const isZoomed = ref(false);

    function checkZoomed() {
        for (const c of chartEls) {
            const chart = c?.chart;
            if (!chart) {
                continue;
            }
            if (chart.isZoomedOrPanned?.()) {
                isZoomed.value = true;
                return;
            }
        }
        isZoomed.value = false;
    }

    let syncing = false;
    // Caches each chart's natural (unzoomed) y-max so we can preserve the
    // relative peak-ratio across charts while the user is zoomed.
    const baseYMax = new WeakMap<any, number>();

    function updateBaseYMax(chart: any) {
        if (chart.isZoomedOrPanned?.()) {
            return;
        }
        const yMax = chart.scales?.y?.max;
        if (yMax != null && yMax > 0) {
            baseYMax.set(chart, yMax);
        }
    }

    function syncAllAxes(sourceChart: any) {
        if (syncing || !enabled.value) {
            checkZoomed();
            return;
        }
        pruneDead();
        const srcX = sourceChart.scales?.x;
        const srcY = sourceChart.scales?.y;
        if (!srcX || !srcY) {
            return;
        }

        updateBaseYMax(sourceChart);
        const srcBase = baseYMax.get(sourceChart) ?? srcY.max;
        const yRatio = srcBase > 0 ? srcY.max / srcBase : 1;

        syncing = true;
        for (const c of chartEls) {
            const target = c?.chart;
            if (!target || target === sourceChart) {
                continue;
            }

            target.scales.x.options.min = srcX.min;
            target.scales.x.options.max = srcX.max;

            updateBaseYMax(target);
            const tgtBase = baseYMax.get(target) ?? target.scales.y.max;
            if (tgtBase > 0) {
                target.scales.y.options.max = Math.max(1, Math.ceil(tgtBase * yRatio));
            }
            if (target.options?.scales?.y?.stacked) {
                target.scales.y.options.min = 0;
            }

            target.update("none");
        }
        syncing = false;
        checkZoomed();
    }

    function resetAll() {
        pruneDead();
        for (const c of chartEls) {
            const chart = c?.chart;
            if (!chart) {
                continue;
            }
            baseYMax.delete(chart);
            chart.resetZoom();
        }
        isZoomed.value = false;
    }

    return {
        charts: readonly(chartEls),
        trackChartRef,
        syncAllAxes,
        resetAll,
        isZoomed,
        ctrlHeld,
    };
}
