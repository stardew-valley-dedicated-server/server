/**
 * Build an iframe src URL for a noVNC endpoint.
 * Centralises query-param logic so VncGrid and OutputPanel stay consistent.
 */
export function vncIframeSrc(baseUrl: string, interactive: boolean): string {
    const params = new URLSearchParams({ resize: "scale", clipboard: "false" });
    if (!interactive) {
        params.set("view_only", "true");
    }
    return `${baseUrl}?${params}`;
}
