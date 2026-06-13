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

/** Extract short test name from display name. */
export function shortTestName(displayName: string): string {
    const parenIdx = displayName.indexOf("(");
    const searchUpTo = parenIdx >= 0 ? parenIdx : displayName.length;
    const lastDot = displayName.lastIndexOf(".", searchUpTo - 1);
    return lastDot >= 0 ? displayName.substring(lastDot + 1) : displayName;
}
