import {
    CategoryScale,
    Chart as ChartJS,
    Filler,
    Legend,
    LinearScale,
    LineElement,
    PointElement,
    Tooltip,
} from "chart.js";
import zoomPlugin from "chartjs-plugin-zoom";
import type { Ref } from "vue";

export function registerChartPlugins() {
    ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Filler, Tooltip, Legend, zoomPlugin);
}

export function niceCeil(value: number, step: number): number {
    return Math.ceil(value / step) * step;
}

export const SMOOTH_WINDOW = 10;

export function smooth(values: (number | null)[], window = SMOOTH_WINDOW): (number | null)[] {
    const result: (number | null)[] = [];
    for (let i = 0; i < values.length; i++) {
        if (values[i] == null) {
            result.push(null);
            continue;
        }
        let sum = 0,
            count = 0;
        for (let j = Math.max(0, i - window + 1); j <= i; j++) {
            if (values[j] != null) {
                sum += values[j]!;
                count++;
            }
        }
        result.push(count > 0 ? sum / count : null);
    }
    return result;
}

export function formatTimeLabel(timestamp: string): string {
    const d = new Date(timestamp);
    return d.toLocaleTimeString("en-US", { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

export const GRID_COLOR = "rgba(255,255,255,0.05)";
export const TICK_COLOR = "rgba(255,255,255,0.3)";
export const X_TICK_COLOR = "rgba(255,255,255,0.2)";

// ── Line chart defaults (shared base that InstanceInspect and CombinedCharts extend) ──

/**
 * Build a zoom plugin config wired into a Ctrl-held ref and a sync callback.
 * Wheel zooms Y by default; Ctrl+wheel zooms X. Drag pans X. Pan/zoom
 * completion invokes `onSync(chart)` so consumers can mirror the change
 * across linked charts.
 */
export function buildZoomConfig(opts: { ctrlHeld: Ref<boolean>; onSync: (chart: any) => void }) {
    return {
        pan: {
            enabled: true,
            mode: "x" as const,
            onPanComplete: ({ chart }: any) => opts.onSync(chart),
        },
        zoom: {
            wheel: { enabled: true, speed: 0.05 },
            pinch: { enabled: true },
            drag: { enabled: false },
            mode: (() => (opts.ctrlHeld.value ? "x" : "y")) as any,
            onZoomComplete: ({ chart }: any) => opts.onSync(chart),
        },
        limits: {
            x: { minRange: 2 },
            y: { minRange: 1 },
        },
    };
}

/** Common element styling for all charts. */
export const chartElementDefaults = {
    point: { radius: 0, hitRadius: 8, hoverRadius: 4, hoverBorderWidth: 2 },
    line: { tension: 0.3 },
};

/** Common scale defaults. */
export const chartScaleDefaults = {
    x: {
        display: true,
        ticks: {
            color: X_TICK_COLOR,
            font: { size: 9 },
            maxTicksLimit: 6,
            maxRotation: 0,
        },
        grid: { display: false },
    },
    y: {
        min: 0,
        grid: { color: GRID_COLOR },
        ticks: { color: TICK_COLOR, font: { size: 10 }, precision: 0 },
    },
};

/** Chart layout defaults. */
export const chartLayoutDefaults = {
    responsive: true,
    maintainAspectRatio: false,
    animation: { duration: 0 } as const,
    layout: { padding: { top: 2 } },
};

/** Y-axis tick formatter for percentages. */
export function pctTickCallback(v: number | string): string {
    return Math.round(Number(v)) + "%";
}

/** Y-axis tick formatter for memory (MB/GB). */
export function memTickCallback(v: number | string): string {
    const n = Number(v);
    return n >= 1024 ? (n / 1024).toFixed(1) + "G" : Math.round(n) + "M";
}

// ── Instance color palettes ──

export const SERVER_COLORS = [
    "rgb(34, 197, 94)", // green
    "rgb(239, 68, 68)", // red
    "rgb(45, 212, 191)", // teal
    "rgb(251, 191, 36)", // amber
];
export const CLIENT_COLORS = [
    "rgb(59, 130, 246)", // blue
    "rgb(168, 85, 247)", // purple
    "rgb(251, 146, 60)", // orange
    "rgb(236, 72, 153)", // pink
    "rgb(14, 165, 233)", // sky
    "rgb(132, 204, 22)", // lime
    "rgb(99, 102, 241)", // indigo
    "rgb(20, 184, 166)", // cyan
];

/** Pick a deterministic color for an instance based on its position among same-type peers. */
export function colorFor(
    instanceId: string,
    instanceType: "server" | "client",
    allInstances: { instanceId: string; instanceType: string }[],
): string {
    const pool = instanceType === "server" ? SERVER_COLORS : CLIENT_COLORS;
    const sameType = allInstances.filter((i) => i.instanceType === instanceType);
    const idx = sameType.findIndex((i) => i.instanceId === instanceId);
    return pool[(idx >= 0 ? idx : 0) % pool.length];
}

// ── Highlight utility ──

export function applyDatasetHighlight(chart: any, hoveredIndex: number | null): void {
    for (let i = 0; i < chart.data.datasets.length; i++) {
        const meta = chart.getDatasetMeta(i);
        if (!meta.dataset) continue;
        const ds = chart.data.datasets[i];
        if (hoveredIndex == null || i === hoveredIndex) {
            meta.dataset.options.borderColor = ds.borderColor;
            meta.dataset.options.borderWidth =
                hoveredIndex != null && i === hoveredIndex ? ((ds.borderWidth as number) ?? 1.5) + 1 : ds.borderWidth;
        } else {
            const color = ds.borderColor as string;
            meta.dataset.options.borderColor = color.replace("rgb(", "rgba(").replace(")", ", 0.12)");
            meta.dataset.options.borderWidth = 1;
        }
    }
}
