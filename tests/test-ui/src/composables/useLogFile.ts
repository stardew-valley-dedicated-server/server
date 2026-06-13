import { onUnmounted, type Ref, ref, watch } from "vue";

export interface LogFileHandle {
    /** Lines as plain strings, in file order. Empty until first fetch resolves. */
    lines: Ref<string[]>;
    loading: Ref<boolean>;
    error: Ref<string | null>;
    /** Manual refetch (ignores cache). */
    refresh: () => Promise<void>;
}

/**
 * Polls a text artifact under `/artifacts/`. After the first fetch, subsequent
 * polls send `If-Modified-Since` and treat 304 as a no-op. ASP.NET Core's
 * `Results.File` (WebRenderer.cs:110) sets `Last-Modified` and honors the
 * conditional automatically, making the loop cheap.
 *
 * Polling stops when `live.value` is false; the composable also stops on
 * unmount. Errors leave previous content visible and surface via `error`.
 */
export function useLogFile(
    path: Ref<string | null>,
    opts: { pollMs?: number; live?: Ref<boolean> } = {},
): LogFileHandle {
    const pollMs = opts.pollMs ?? 2000;
    const live = opts.live;

    const lines = ref<string[]>([]);
    const loading = ref(false);
    const error = ref<string | null>(null);
    let lastModified: string | null = null;
    let timer: ReturnType<typeof setTimeout> | null = null;
    let cancelled = false;
    // A response only gets applied while its version is still current — a slow
    // fetch for a previous path must not overwrite the newer path's state.
    let requestVersion = 0;
    let inFlight: AbortController | null = null;

    function clearTimer() {
        if (timer != null) {
            clearTimeout(timer);
            timer = null;
        }
    }

    function invalidateInFlight() {
        requestVersion++;
        inFlight?.abort();
        inFlight = null;
    }

    async function fetchOnce(force: boolean): Promise<void> {
        const url = path.value;
        if (!url) {
            return;
        }
        inFlight?.abort();
        inFlight = new AbortController();
        const signal = inFlight.signal;
        const myVersion = ++requestVersion;
        loading.value = true;
        try {
            const headers: Record<string, string> = {};
            if (!force && lastModified) {
                headers["If-Modified-Since"] = lastModified;
            }
            const res = await fetch(url, { headers, signal });
            if (myVersion !== requestVersion) {
                return;
            }
            if (res.status === 304) {
                return;
            }
            if (!res.ok) {
                if (res.status === 404) {
                    // Artifact not yet created (early in run, or empty container) — keep prior state.
                    return;
                }
                error.value = `${res.status} ${res.statusText}`;
                return;
            }
            error.value = null;
            const lm = res.headers.get("Last-Modified");
            if (lm) {
                lastModified = lm;
            }
            const text = await res.text();
            if (myVersion !== requestVersion) {
                return;
            }
            // Strip a single trailing newline so the row count matches visual lines.
            const trimmed = text.endsWith("\n") ? text.slice(0, -1) : text;
            lines.value = trimmed.length === 0 ? [] : trimmed.split("\n");
        } catch (e) {
            if (myVersion !== requestVersion || signal.aborted) {
                return;
            }
            error.value = e instanceof Error ? e.message : String(e);
        } finally {
            if (myVersion === requestVersion) {
                loading.value = false;
            }
        }
    }

    function scheduleNext() {
        if (cancelled) {
            return;
        }
        if (live && !live.value) {
            return;
        }
        timer = setTimeout(async () => {
            await fetchOnce(false);
            scheduleNext();
        }, pollMs);
    }

    async function refresh() {
        clearTimer();
        await fetchOnce(true);
        scheduleNext();
    }

    // Restart whenever the path changes (e.g. switching the inspected instance).
    watch(
        path,
        async (next, prev) => {
            if (next === prev) {
                return;
            }
            clearTimer();
            invalidateInFlight();
            lastModified = null;
            lines.value = [];
            error.value = null;
            loading.value = false;
            if (next) {
                await fetchOnce(true);
                scheduleNext();
            }
        },
        { immediate: true },
    );

    // React to live flips: when a run finishes, drop the timer; when a fresh run
    // starts (live flips back true), resume polling.
    if (live) {
        watch(live, async (isLive) => {
            clearTimer();
            if (isLive && path.value) {
                await fetchOnce(true);
                scheduleNext();
            }
        });
    }

    onUnmounted(() => {
        cancelled = true;
        clearTimer();
        invalidateInFlight();
    });

    return { lines, loading, error, refresh };
}
