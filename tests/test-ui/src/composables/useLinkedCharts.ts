import type { Chart as ChartJS } from "chart.js";
import { ref } from "vue";
import { applyDatasetHighlight } from "../utils/chart";

/**
 * Links multiple Chart.js instances so hovering one chart shows
 * the tooltip/crosshair at the same x-index on all others.
 *
 * Usage:
 *   const { plugin, setChartRef, resetAllZoom } = useLinkedCharts()
 *   // Pass `plugin` in :plugins="[plugin]" for each <Line>
 *   // Pass `setChartRef` as :ref="setChartRef" for each <Line>
 */
export function useLinkedCharts() {
    const charts = ref<any[]>([]);
    let syncing = false;
    // Track last synced point to skip redundant updates. mousemove fires
    // for every pixel but the active data point only changes at thresholds
    let lastIndex = -1;
    let lastDataset = -1;

    function setChartRef(el: any) {
        if (!el) return;
        if (!charts.value.includes(el)) {
            charts.value.push(el);
        }
    }

    const plugin = {
        id: "linkedCharts",
        afterEvent(chart: any, args: any) {
            if (syncing) return;
            const evt = args.event;

            if (evt.type === "mouseout") {
                if (lastIndex === -1) return; // Already cleared
                lastIndex = -1;
                lastDataset = -1;

                syncing = true;
                for (const c of charts.value) {
                    const target = c?.chart as ChartJS | undefined;
                    if (!target || target === chart) continue;
                    target.setActiveElements([]);
                    target.tooltip?.setActiveElements([], { x: 0, y: 0 });
                    target.update("none");
                }
                applyDatasetHighlight(chart, null);
                syncing = false;
                return;
            }

            if (evt.type !== "mousemove") return;

            const active = chart.getActiveElements();
            if (active.length === 0) return;

            const dataIndex = active[0].index;
            const datasetIndex = active[0].datasetIndex;

            // Skip if still hovering the same data point
            if (dataIndex === lastIndex && datasetIndex === lastDataset) return;
            lastIndex = dataIndex;
            lastDataset = datasetIndex;

            syncing = true;
            for (const c of charts.value) {
                const target = c?.chart as ChartJS | undefined;
                if (!target || target === chart) continue;

                const elements: any[] = [];
                for (let di = 0; di < target.data.datasets.length; di++) {
                    const val = target.data.datasets[di].data[dataIndex];
                    if (val != null) {
                        elements.push({ datasetIndex: di, index: dataIndex });
                    }
                }

                target.setActiveElements(elements);

                if (target.tooltip && elements.length > 0) {
                    const meta = target.getDatasetMeta(elements[0].datasetIndex);
                    const point = meta?.data?.[dataIndex];
                    if (point) {
                        target.tooltip.setActiveElements(elements, { x: point.x, y: point.y });
                    }
                }

                // Dataset counts can differ across linked charts (e.g. a 2-line-per-instance net panel
                // vs a 1-line-per-instance CPU panel). Only apply the highlight when the hovered index
                // exists on the target — otherwise every dataset would be dimmed to "not the hovered one".
                const targetHighlight = datasetIndex < target.data.datasets.length ? datasetIndex : null;
                applyDatasetHighlight(target, targetHighlight);
                target.update("none");
            }
            syncing = false;
        },
    };

    return { plugin, setChartRef };
}
