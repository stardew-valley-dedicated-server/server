import type { AnnotationLevel, AnnotationSource, OutputEntry } from "../types/state";

export type { AnnotationLevel, AnnotationSource };

// ── Output segment grouping ──

export type AnnotationEntry = Extract<OutputEntry, { type: "annotation" }>;
export type ScreenshotEntry = Extract<OutputEntry, { type: "screenshot" }>;

export type OutputSegment = { type: "lines"; items: AnnotationEntry[] } | { type: "images"; items: ScreenshotEntry[] };

/** Groups consecutive output entries of the same type for rendering. */
export function groupOutputEntries(entries: OutputEntry[] | null | undefined): OutputSegment[] {
    if (!entries?.length) return [];
    const segments: OutputSegment[] = [];
    for (const entry of entries) {
        if (entry.type === "annotation") {
            const last = segments[segments.length - 1];
            if (last?.type === "lines") last.items.push(entry);
            else segments.push({ type: "lines", items: [entry] });
        } else if (entry.type === "screenshot") {
            const last = segments[segments.length - 1];
            if (last?.type === "images") last.items.push(entry);
            else segments.push({ type: "images", items: [entry] });
        }
    }
    return segments;
}

/** Cumulative line offset for each segment so line numbers are continuous. */
export function segmentLineOffset(segments: OutputSegment[], segIndex: number): number {
    let offset = 0;
    for (let i = 0; i < segIndex; i++) {
        const seg = segments[i];
        if (seg.type === "lines") offset += seg.items.length;
    }
    return offset;
}

// ── Line formatting ──

export type TimestampMode = "off" | "absolute" | "relative";

export function nextTimestampMode(m: TimestampMode): TimestampMode {
    return m === "off" ? "absolute" : m === "absolute" ? "relative" : "off";
}

/** Format an ISO timestamp to HH:mm:ss.mmm for display in output panels. */
export function formatLineTimestamp(iso: string): string {
    const d = new Date(iso);
    return (
        d.toLocaleTimeString("en-US", { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" }) +
        "." +
        String(d.getMilliseconds()).padStart(3, "0")
    );
}

/** Format a signed duration in ms as +M:SS.mmm (M omitted when 0). */
export function formatRelativeTimestamp(elapsedMs: number): string {
    const sign = elapsedMs < 0 ? "-" : "+";
    const abs = Math.abs(elapsedMs);
    const totalSec = Math.floor(abs / 1000);
    const ms = Math.floor(abs % 1000);
    const m = Math.floor(totalSec / 60);
    const s = totalSec % 60;
    const base = m > 0 ? `${m}:${String(s).padStart(2, "0")}` : `${s}`;
    return `${sign}${base}.${String(ms).padStart(3, "0")}`;
}

/** Format an output entry's timestamp according to the active mode. Returns '' when off or missing. */
export function formatEntryTimestamp(ts: string | undefined, mode: TimestampMode, anchorMs: number): string {
    if (!ts || mode === "off") return "";
    if (mode === "absolute") return `[${formatLineTimestamp(ts)}]`;
    return `[${formatRelativeTimestamp(new Date(ts).getTime() - anchorMs)}]`;
}

/** Anchor timestamp (ms) of the first annotation entry, or 0 if none. */
export function anchorTimestampMs(entries: OutputEntry[] | null | undefined): number {
    const first = entries?.find((e) => e.type === "annotation" && e.ts);
    return first?.ts ? new Date(first.ts).getTime() : 0;
}

/** Test whether a message should be highlighted as a success line (green).
 *  Textual heuristic for green-highlighting; the `level` field is the
 *  authoritative success signal. */
export function isSuccessLine(text: string): boolean {
    return /✓|Done \(/.test(text);
}

// ── Annotation styling ──

/** Iconify icon name for an annotation source. */
export function annotationSourceIcon(source: AnnotationSource): string {
    switch (source) {
        case "broker":
            return "lucide:settings";
        case "recording":
            return "lucide:video";
        case "mod":
            return "lucide:puzzle";
        case "setup":
            return "lucide:hammer";
        default:
            return "lucide:chevron-right";
    }
}

/** Tailwind classes for the message body of an annotation, keyed by level.
 *  `isSuccessLine` still applies on top for ✓-prefixed messages — the `level`
 *  field is the authoritative success signal. */
export function annotationLevelClass(level: AnnotationLevel): string {
    switch (level) {
        case "success":
            return "text-success";
        case "warning":
            return "text-warning";
        case "error":
            return "text-error";
        case "detail":
            return "text-base-content/55";
        case "trace":
            return "text-base-content/35";
        case "section":
            return "text-info font-semibold";
        default:
            return "text-base-content/75";
    }
}

/** Tailwind class for the source icon's color. Tints the icon to match the
 *  origin of the annotation without overpowering the message text. */
export function annotationSourceClass(source: AnnotationSource): string {
    switch (source) {
        case "broker":
            return "text-info/70";
        case "recording":
            return "text-warning/70";
        case "mod":
            return "text-success/70";
        case "setup":
            return "text-base-content/40";
        default:
            return "text-base-content/30";
    }
}
