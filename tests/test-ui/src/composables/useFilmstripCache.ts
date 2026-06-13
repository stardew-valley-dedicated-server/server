import { shallowRef, triggerRef } from "vue";
import { createLogger } from "../utils/logger";

const log = createLogger("Filmstrip");

// ── Types ──

export interface Thumbnail {
    url: string;
}

export interface ThumbJob {
    /** Video artifact path (cache key). */
    path: string;
    /** Fetchable video URL. */
    src: string;
    /** Wall-clock recording duration in seconds. */
    wallClockDuration: number;
    /** Number of thumbnails to generate. */
    count: number;
}

// ── Constants ──

export const THUMB_W = 120;
export const THUMB_H = 68;
const MAX_THUMBS = 300;

/** Duration-based thumbnail count: 1 per second, clamped to [4, 300]. */
export function defaultFrameCount(durationSec: number): number {
    return Math.max(4, Math.min(MAX_THUMBS, Math.ceil(durationSec)));
}

// ── Singleton state ──

/** path -> generated thumbnails. Never evicted. */
const cache = new Map<string, Thumbnail[]>();

/** Paths currently queued or being generated. */
const activeJobs = new Set<string>();

/** path -> expected total thumbnail count for the current/last job. */
const expectedCounts = new Map<string, number>();

/**
 * Bumped on every progressive thumbnail write. Components watch this to
 * reactively pick up new thumbnails from the cache without the engine
 * needing a reference to any component state.
 */
const version = shallowRef(0);

// ── Generation engine ──

const queue: ThumbJob[] = [];
let processing = false;

function cleanupVideo(vid: HTMLVideoElement) {
    vid.pause();
    vid.onloadeddata = null;
    vid.onerror = null;
    vid.onseeked = null;
    vid.remove();
}

function waitForSeek(vid: HTMLVideoElement, time: number): Promise<void> {
    return new Promise((resolve) => {
        if (Math.abs(vid.currentTime - time) < 0.001 && !vid.seeking && vid.readyState >= 2) {
            resolve();
            return;
        }
        vid.addEventListener("seeked", () => resolve(), { once: true });
        vid.currentTime = time;
    });
}

async function generate(job: ThumbJob) {
    const existing = cache.get(job.path) ?? [];
    const thumbs: Thumbnail[] = [...existing];

    // Build full seek-time sequence (deterministic for a given count + wallClockDuration)
    const vid = document.createElement("video");
    vid.preload = "auto";
    vid.muted = true;
    vid.src = job.src;

    const loaded = await Promise.race([
        new Promise<boolean>((resolve) => {
            vid.onloadeddata = () => resolve(true);
            vid.onerror = () => resolve(false);
        }),
        new Promise<boolean>((resolve) => setTimeout(() => resolve(false), 10_000)),
    ]);

    if (!loaded) {
        log.warn(`failed to load video ${job.path.slice(-40)}`);
        cleanupVideo(vid);
        return;
    }

    const duration = vid.duration;
    if (!duration || !Number.isFinite(duration) || duration <= 0) {
        cleanupVideo(vid);
        return;
    }

    // Distribute seek times across wall-clock duration so thumbnails align
    // with the timeline layout. Clamp to vid.duration for safety.
    const wallClock = job.wallClockDuration > 0 ? job.wallClockDuration : duration;

    const seekTimes: number[] = [0];
    if (job.count > 2) {
        const inner = job.count - 2;
        for (let i = 1; i <= inner; i++) {
            seekTimes.push(Math.min((i / (inner + 1)) * wallClock, duration - 0.01));
        }
    }
    seekTimes.push(Math.max(0, duration - 0.1));

    // Skip already-generated frames (seek times are deterministic, so slicing is safe)
    const resumeFrom = existing.length;
    const remaining = seekTimes.slice(resumeFrom);
    if (remaining.length === 0) {
        cleanupVideo(vid);
        return;
    }

    log.log(
        `${resumeFrom > 0 ? "resuming" : "generating"} ${remaining.length}/${seekTimes.length} thumbs for ${job.path.slice(-40)}`,
        { src: job.src.slice(0, 80) },
    );

    const canvas = document.createElement("canvas");
    canvas.width = THUMB_W;
    canvas.height = THUMB_H;
    const ctx = canvas.getContext("2d")!;

    for (const seekTime of remaining) {
        await waitForSeek(vid, seekTime);
        ctx.drawImage(vid, 0, 0, THUMB_W, THUMB_H);
        const blob = await new Promise<Blob>((resolve) => canvas.toBlob((b) => resolve(b!), "image/jpeg", 0.5));
        thumbs.push({ url: URL.createObjectURL(blob) });
        // Persist progressively so partial results survive page navigation
        cache.set(job.path, [...thumbs]);
        version.value++;
    }

    cleanupVideo(vid);
    log.log(
        `done ${job.path.slice(-40)} (${thumbs.length} thumbs${resumeFrom > 0 ? `, resumed from ${resumeFrom}` : ""})`,
    );
}

async function processQueue() {
    processing = true;
    while (queue.length > 0) {
        const job = queue.shift()!;
        await generate(job);
        activeJobs.delete(job.path);
        triggerRef(version);
    }
    processing = false;
}

// ── Public API ──

export function useFilmstripCache() {
    return {
        /** Reactive version counter -- watch this to react to new thumbnails. */
        version,

        /** Get cached thumbnails for a video path, or undefined if not yet generated. */
        get(path: string): Thumbnail[] | undefined {
            return cache.get(path);
        },

        /** Whether this path has a complete cache entry with at least `expected` thumbnails. */
        isComplete(path: string, expected: number): boolean {
            const entry = cache.get(path);
            return !!entry && entry.length >= expected;
        },

        /** Whether this path is currently queued or being generated. */
        isGenerating(path: string): boolean {
            return activeJobs.has(path);
        },

        /** Whether this path has all expected thumbnails and no active job. */
        isFullyComplete(path: string): boolean {
            if (activeJobs.has(path)) {
                return false;
            }
            const expected = expectedCounts.get(path);
            if (!expected) {
                return false;
            }
            const entry = cache.get(path);
            return !!entry && entry.length >= expected;
        },

        /** Enqueue a thumbnail generation job. No-op if already queued or cached with enough frames. */
        enqueue(job: ThumbJob) {
            if (activeJobs.has(job.path)) {
                return;
            }
            const existing = cache.get(job.path);
            if (existing && existing.length >= job.count) {
                return;
            }
            expectedCounts.set(job.path, job.count);
            log.log(`enqueue ${job.path.slice(-40)} (count=${job.count}, queue=${queue.length})`);
            activeJobs.add(job.path);
            queue.push(job);
            if (!processing) {
                processQueue();
            }
        },

        /** Move jobs for these paths to the front of the queue so they generate next. */
        prioritize(paths: string[]) {
            const urgent: ThumbJob[] = [];
            const rest: ThumbJob[] = [];
            const pathSet = new Set(paths);
            for (const job of queue) {
                if (pathSet.has(job.path)) {
                    urgent.push(job);
                } else {
                    rest.push(job);
                }
            }
            if (urgent.length > 0) {
                log.log(`prioritize ${urgent.length} jobs to front (${rest.length} deferred)`);
                queue.length = 0;
                queue.push(...urgent, ...rest);
            }
        },
    };
}
