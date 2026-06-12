import type { StatsSnapshotEntry } from "../types/state";

export function median(values: number[]): number {
    if (values.length === 0) return 0;
    const sorted = [...values].sort((a, b) => a - b);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

export function medianOf(
    history: StatsSnapshotEntry[],
    extract: (s: StatsSnapshotEntry) => number | null,
): number | null {
    const values = history.map(extract).filter((v): v is number => v != null);
    return values.length > 0 ? median(values) : null;
}
