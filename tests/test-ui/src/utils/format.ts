/** Format duration in milliseconds to human-readable string. */
export function formatDuration(ms: number | null | undefined): string {
    if (ms == null) {
        return "";
    }
    if (ms >= 60000) {
        return `${(ms / 60000).toFixed(1)}m`;
    }
    if (ms >= 1000) {
        return `${(ms / 1000).toFixed(2)}s`;
    }
    return `${Math.round(ms)}ms`;
}

/** Format an ISO timestamp as an absolute local date + time (e.g. "Jun 21, 2026, 14:03").
 *  Returns "" for null/unparseable input so callers can `v-if` on it. */
export function formatDateTime(iso: string | null | undefined): string {
    if (!iso) {
        return "";
    }
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) {
        return "";
    }
    return d.toLocaleString([], {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
        hour12: false,
    });
}

/** Extract short test name from display name. */
export function shortTestName(displayName: string): string {
    const parenIdx = displayName.indexOf("(");
    const searchUpTo = parenIdx >= 0 ? parenIdx : displayName.length;
    const lastDot = displayName.lastIndexOf(".", searchUpTo - 1);
    return lastDot >= 0 ? displayName.substring(lastDot + 1) : displayName;
}
