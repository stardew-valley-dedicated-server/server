/** Decompose seconds into hours, minutes, seconds, and milliseconds. */
export function decomposeTime(seconds: number) {
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = Math.floor(seconds % 60);
    const ms = Math.floor((seconds % 1) * 1000);
    return { h, m, s, ms };
}

/** Format seconds as "mm:ss.mmm" or "hh:mm:ss.mmm" (all zero-padded). */
export function formatTimePrecise(seconds: number): string {
    const { h, m, s, ms } = decomposeTime(seconds);
    const mmss = `${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}.${ms.toString().padStart(3, "0")}`;
    return h > 0 ? `${h.toString().padStart(2, "0")}:${mmss}` : mmss;
}

/** Format seconds as "m:ss" or "h:mm:ss" (compact, no milliseconds). */
export function formatTimeShort(seconds: number): string {
    const { h, m, s } = decomposeTime(seconds);
    if (h > 0) {
        return `${h}:${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
    }
    return `${m}:${s.toString().padStart(2, "0")}`;
}

/** Format seconds for the timeline ruler: "mm:ss.mmm" or "hh:mm:ss.mmm" (all zero-padded). */
export function formatTimeRuler(seconds: number): string {
    const { h, m, s, ms } = decomposeTime(seconds);
    if (h > 0) {
        return `${h.toString().padStart(2, "0")}:${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}.${ms.toString().padStart(3, "0")}`;
    }
    return `${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}.${ms.toString().padStart(3, "0")}`;
}
