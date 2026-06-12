/**
 * Shared metric threshold helpers for instance performance display.
 * Determines color classes based on fair-share resource allocation.
 */

/** Default TPS/FPS target when not reported by the game. */
export const DEFAULT_TARGET_TPS = 60;

/** Headroom multiplier for TPS/FPS chart Y-axis max (15% above peak). */
export const TPS_PEAK_HEADROOM = 1.15;

/** Headroom multiplier above target TPS/FPS for chart Y-axis minimum ceiling.
 *  Ensures the target reference line sits at ~83% height, not jammed at the top. */
export const TPS_TARGET_HEADROOM = 1.2;

/** Canonical RGB colors for each metric, used by all chart surfaces. */
export const METRIC_COLORS = {
    cpu: "rgb(59, 130, 246)", // blue
    mem: "rgb(168, 85, 247)", // purple
    tps: "rgb(45, 212, 191)", // teal
    fps: "rgb(234, 179, 8)", // yellow
    queue: "rgb(251, 146, 60)", // orange
    wait: "rgb(244, 63, 94)", // rose
    gc: "rgb(132, 204, 22)", // lime
    netRx: "rgb(52, 211, 153)", // emerald
    netTx: "rgb(236, 72, 153)", // pink
} as const;

/** TPS threshold ratios (fraction of target). */
export const TPS_GOOD_RATIO = 0.8; // >= 80% of target = normal
export const TPS_WARN_RATIO = 0.33; // >= 33% of target = warning, below = error

/** Classify a TPS/FPS value against its target. */
export type TpsLevel = "good" | "warn" | "error";
export function tpsLevel(value: number, targetTps: number | null): TpsLevel {
    const target = targetTps ?? DEFAULT_TARGET_TPS;
    if (value >= target * TPS_GOOD_RATIO) return "good";
    if (value >= target * TPS_WARN_RATIO) return "warn";
    return "error";
}

interface MetricStats {
    cpuPercent: number;
    memoryMb: number;
    cpuCount: number;
    totalMemoryMb: number;
    fps: number | null;
    tps: number | null;
    gameMemoryMb: number | null;
    targetTps: number | null;
    targetFps: number | null;
    pendingActions: number | null;
    gameThreadWaitMs: number | null;
    memoryLimitMb: number;
}

/** TPS color class based on % of target. */
export function tpsTextClass(stats: MetricStats | undefined): string {
    if (!stats || stats.tps == null) return "text-base-content/40";
    const level = tpsLevel(stats.tps, stats.targetTps);
    if (level === "good") return "text-base-content/50";
    if (level === "warn") return "text-warning";
    return "text-error";
}

/** FPS color class based on % of target. */
export function fpsTextClass(stats: MetricStats | undefined): string {
    if (!stats || stats.fps == null) return "text-base-content/40";
    const level = tpsLevel(stats.fps, stats.targetFps ?? stats.targetTps);
    if (level === "good") return "text-base-content/50";
    if (level === "warn") return "text-warning";
    return "text-error";
}

/** CPU color class based on fair-share allocation. */
export function cpuTextClass(stats: MetricStats | undefined, instanceCount: number): string {
    if (!stats) return "text-base-content/40";
    const totalCpuPct = (stats.cpuCount > 0 ? stats.cpuCount : 4) * 100;
    const fairShare = totalCpuPct / Math.max(instanceCount, 1);
    const pct = stats.cpuPercent / fairShare;
    if (pct >= 1.5) return "text-error";
    if (pct >= 1.0) return "text-warning";
    return "text-base-content/50";
}

/** Memory color class based on fair-share allocation or per-container Docker limit. */
export function memTextClass(stats: MetricStats | undefined, instanceCount: number): string {
    if (!stats) return "text-base-content/40";
    const mem = stats.gameMemoryMb ?? stats.memoryMb;
    // Use per-container Docker limit only if it's a real constraint (less than host total).
    // When no --memory flag is set, Docker reports the host/VM total as Limit, same as totalMemoryMb.
    const totalMem = stats.totalMemoryMb > 0 ? stats.totalMemoryMb : 16384;
    const hasRealLimit = stats.memoryLimitMb > 0 && stats.memoryLimitMb < totalMem * 0.95;
    const effectiveLimit = hasRealLimit ? stats.memoryLimitMb : totalMem / Math.max(instanceCount, 1);
    const pct = mem / effectiveLimit;
    if (pct >= 1.5) return "text-error";
    if (pct >= 1.0) return "text-warning";
    return "text-base-content/50";
}

/** Get display memory (game memory preferred over container memory). */
export function displayMem(stats: MetricStats | undefined): number | null {
    if (!stats) return null;
    return stats.gameMemoryMb ?? stats.memoryMb;
}

/** Format MB value as human-readable string. */
export function formatMem(mb: number): string {
    if (mb >= 1024) return (mb / 1024).toFixed(1) + "gb";
    return Math.round(mb) + "mb";
}

/** Game thread queue color: green = 0, yellow = 1-5, red > 5 */
export function queueTextClass(stats: MetricStats | undefined): string {
    if (!stats || stats.pendingActions == null) return "text-base-content/40";
    if (stats.pendingActions === 0) return "text-base-content/50";
    if (stats.pendingActions <= 5) return "text-warning";
    return "text-error";
}

/** Game thread wait time color: green < 50ms, yellow 50-200ms, red > 200ms */
export function waitTextClass(stats: MetricStats | undefined): string {
    if (!stats || stats.gameThreadWaitMs == null) return "text-base-content/40";
    if (stats.gameThreadWaitMs < 50) return "text-base-content/50";
    if (stats.gameThreadWaitMs < 200) return "text-warning";
    return "text-error";
}

/** Format bytes/sec as human-readable string. */
export function formatBytesPerSec(bps: number): string {
    if (bps >= 1024 * 1024) return (bps / (1024 * 1024)).toFixed(1) + " MB/s";
    if (bps >= 1024) return (bps / 1024).toFixed(1) + " KB/s";
    return Math.round(bps) + " B/s";
}
