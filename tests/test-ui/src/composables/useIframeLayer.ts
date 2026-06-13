/**
 * Global iframe overlay layer.
 *
 * Iframes cannot survive DOM reparenting (appendChild reloads them). This
 * module provides a persistent container on document.body where iframes live
 * forever, positioned via `position: fixed` to overlay their placeholder divs.
 *
 * Sync strategy (event-driven):
 *
 *   A naive rAF-per-frame-per-iframe loop dominates CPU when many tiles are
 *   mounted (forced reflows + style writes every vsync). Instead, we sync
 *   only when the placeholder's box actually moves or resizes:
 *
 *   - ResizeObserver on each placeholder (and on the clip host)
 *   - A single capturing `scroll` listener on window (covers any ancestor
 *     scroll container) and a `resize` listener for the viewport
 *   - A CSS `transitionrun` listener on document (captures sidebar slides,
 *     DaisyUI transitions, and our own animateTransition() calls); kicks
 *     off a short-lived rAF window so we follow animated ancestors smoothly
 *   - Explicit scheduleSync() on track() and on every animateTransition()
 *
 *   Multiple events in the same frame coalesce: each handle has a `pending`
 *   flag, and a single shared rAF drains all pending wrappers on the next
 *   frame. Style writes are diffed against the last-written value so a
 *   no-op sync costs almost nothing.
 */

let container: HTMLDivElement | null = null;
let clipHost: HTMLElement | null = null;
const TRANSITION_DURATION_MS = 350;

function getContainer(): HTMLDivElement {
    if (!container) {
        container = document.createElement("div");
        container.id = "vnc-iframe-layer";
        container.style.cssText = "position:fixed;inset:0;pointer-events:none;z-index:10;";
        document.body.appendChild(container);
    }
    return container;
}

interface InternalHandle {
    wrapper: HTMLDivElement;
    placeholder: HTMLElement | null;
    pending: boolean;
    /** Cached target height during transitions, set once when placeholder changes. */
    targetHeight: number;
    /** Last-written style values. `undefined` means "not yet written". */
    last: {
        display?: string;
        top?: string;
        left?: string;
        width?: string;
        height?: string;
        clipPath?: string;
    };
    placeholderRO: ResizeObserver | null;
}

const handles = new Set<InternalHandle>();

// ── rAF continuation window ────────────────────────────────────────────────
// While > 0 we keep a shared rAF loop running that drains every handle each
// frame. Used during CSS transitions (ours via animateTransition(), or any
// ancestor transition caught via the transitionrun listener).
let transitionCount = 0;
let continuationUntilMs = 0;
let sharedRafId = 0;

function scheduleDrainAll() {
    if (sharedRafId) {
        return;
    }
    sharedRafId = requestAnimationFrame(drainAll);
}

function drainAll() {
    sharedRafId = 0;
    for (const h of handles) {
        if (h.pending || transitionCount > 0 || performance.now() < continuationUntilMs) {
            h.pending = false;
            syncHandle(h);
        }
    }
    // Keep looping while a transition is in flight or within the continuation window.
    if (transitionCount > 0 || performance.now() < continuationUntilMs) {
        sharedRafId = requestAnimationFrame(drainAll);
    }
}

function scheduleSync(h: InternalHandle) {
    h.pending = true;
    scheduleDrainAll();
}

function scheduleSyncAll() {
    for (const h of handles) {
        h.pending = true;
    }
    scheduleDrainAll();
}

/** Extend the continuation window so we keep syncing for `ms` more. */
function bumpContinuation(ms: number) {
    const until = performance.now() + ms;
    if (until > continuationUntilMs) {
        continuationUntilMs = until;
    }
    scheduleDrainAll();
}

// ── Core sync: measure placeholder, reposition wrapper, diff-writes ────────

function writeStyle<K extends keyof CSSStyleDeclaration>(
    style: CSSStyleDeclaration,
    last: InternalHandle["last"],
    key: K & keyof InternalHandle["last"],
    value: string,
) {
    if (last[key] === value) {
        return;
    }
    last[key] = value;
    (style as any)[key] = value;
}

function syncHandle(h: InternalHandle) {
    const { wrapper, placeholder, last } = h;
    if (!placeholder) {
        writeStyle(wrapper.style, last, "display", "none");
        return;
    }

    const rect = placeholder.getBoundingClientRect();

    // Hide when placeholder has no width (parent hidden via v-show, etc.)
    if (rect.width === 0) {
        writeStyle(wrapper.style, last, "display", "none");
        return;
    }

    writeStyle(wrapper.style, last, "display", "");
    let wrapperHeight: number;

    if (transitionCount > 0) {
        // Mid-transition: set explicit target rect. CSS transitions (installed by
        // animateTransition) animate from the snapshot to these values. Measure
        // target height once per placeholder change.
        if (h.targetHeight === 0) {
            const measure = wrapper.cloneNode(false) as HTMLDivElement;
            measure.style.cssText = "position:fixed;top:-9999px;left:-9999px;pointer-events:none;overflow:hidden;";
            measure.style.width = rect.width + "px";
            measure.style.height = "";
            measure.style.transition = "none";
            while (wrapper.firstChild) {
                measure.appendChild(wrapper.firstChild);
            }
            wrapper.parentNode!.appendChild(measure);
            h.targetHeight = measure.scrollHeight;
            while (measure.firstChild) {
                wrapper.appendChild(measure.firstChild);
            }
            measure.remove();
        }
        wrapperHeight = h.targetHeight;
        writeStyle(wrapper.style, last, "top", rect.top + "px");
        writeStyle(wrapper.style, last, "left", rect.left + "px");
        writeStyle(wrapper.style, last, "width", rect.width + "px");
        writeStyle(wrapper.style, last, "height", wrapperHeight + "px");
        if (wrapperHeight > 0) {
            placeholder.style.height = wrapperHeight + "px";
        }
    } else {
        // Normal mode: width from placeholder; height driven by natural card content.
        writeStyle(wrapper.style, last, "width", rect.width + "px");
        writeStyle(wrapper.style, last, "height", "");

        // Push the card's natural height back to the placeholder so layout reserves
        // space for the header + test bar + iframe.
        wrapperHeight = wrapper.scrollHeight;
        if (wrapperHeight > 0 && placeholder.style.height !== wrapperHeight + "px") {
            placeholder.style.height = wrapperHeight + "px";
        }

        // Re-read position once the placeholder height has settled.
        const updatedRect = placeholder.getBoundingClientRect();
        writeStyle(wrapper.style, last, "top", updatedRect.top + "px");
        writeStyle(wrapper.style, last, "left", updatedRect.left + "px");
    }

    // Clip to host bounds if registered.
    if (clipHost) {
        const finalRect = placeholder.getBoundingClientRect();
        const clip = clipHost.getBoundingClientRect();
        const top = finalRect.top;
        const bottom = top + wrapperHeight;
        const clipTop = Math.max(0, clip.top - top);
        const clipBottom = Math.max(0, bottom - clip.bottom);
        const clipPath = clipTop > 0 || clipBottom > 0 ? `inset(${clipTop}px 0 ${clipBottom}px 0)` : "";
        writeStyle(wrapper.style, last, "clipPath", clipPath);
    } else {
        writeStyle(wrapper.style, last, "clipPath", "");
    }
}

// ── Module-scoped listeners ────────────────────────────────────────────────
// Installed lazily on first handle; torn down on last handle unregister.

let listenersInstalled = false;
let clipHostRO: ResizeObserver | null = null;

function onScroll() {
    scheduleSyncAll();
}
function onResize() {
    scheduleSyncAll();
}
function onTransitionRun() {
    // Any CSS transition starting on the page may affect placeholder geometry
    // (e.g. sidebar collapse). Follow it for slightly longer than the longest
    // transition we care about; subsequent transitionrun events extend the window.
    bumpContinuation(600);
}

function installListeners() {
    if (listenersInstalled) {
        return;
    }
    listenersInstalled = true;
    window.addEventListener("scroll", onScroll, { capture: true, passive: true });
    window.addEventListener("resize", onResize, { passive: true });
    document.addEventListener("transitionrun", onTransitionRun, { capture: true, passive: true });
}

function uninstallListenersIfIdle() {
    if (!listenersInstalled) {
        return;
    }
    if (handles.size > 0) {
        return;
    }
    listenersInstalled = false;
    window.removeEventListener("scroll", onScroll, { capture: true });
    window.removeEventListener("resize", onResize);
    document.removeEventListener("transitionrun", onTransitionRun, { capture: true });
}

// ── Public API ─────────────────────────────────────────────────────────────

/**
 * Register an element whose bounds clip the iframe overlays. Wrappers outside
 * this rect are clipped via clip-path. Call from onMounted.
 */
export function setClipHost(host: HTMLElement | null) {
    clipHost = host;
    if (clipHostRO) {
        clipHostRO.disconnect();
        clipHostRO = null;
    }
    if (host && typeof ResizeObserver !== "undefined") {
        clipHostRO = new ResizeObserver(scheduleSyncAll);
        clipHostRO.observe(host);
    }
    scheduleSyncAll();
}

/**
 * Enable CSS transitions on all iframe wrappers for a smooth layout change.
 *
 * Strategy:
 * 1. Snapshot each wrapper's current rect as explicit inline styles (the "from" state).
 * 2. On the next frame, enable CSS transitions.
 * 3. sync() observes transitionCount > 0 and writes explicit target rect values
 *    each frame so width+height animate in lockstep.
 * 4. After the duration, clear transitions and release height back to auto.
 */
export function animateTransition(durationMs: number = TRANSITION_DURATION_MS) {
    // Snapshot each wrapper's current rect so the transition starts from here.
    for (const h of handles) {
        const w = h.wrapper;
        const cur = w.getBoundingClientRect();
        w.style.top = cur.top + "px";
        w.style.left = cur.left + "px";
        w.style.width = cur.width + "px";
        w.style.height = cur.height + "px";
        // Clear diff cache so the next sync() writes will definitely take effect.
        h.last.top = cur.top + "px";
        h.last.left = cur.left + "px";
        h.last.width = cur.width + "px";
        h.last.height = cur.height + "px";
    }

    transitionCount++;

    // Enable transitions on the next frame so the browser registers the snapshot.
    requestAnimationFrame(() => {
        const transition = `top ${durationMs}ms ease, left ${durationMs}ms ease, width ${durationMs}ms ease, height ${durationMs}ms ease`;
        for (const h of handles) {
            h.wrapper.style.transition = transition;
        }
        scheduleDrainAll();
    });

    setTimeout(() => {
        transitionCount--;
        if (transitionCount <= 0) {
            transitionCount = 0;
            for (const h of handles) {
                h.wrapper.style.transition = "";
                h.wrapper.style.height = "";
                // Height was released; force a re-sync to pick up the real scrollHeight.
                h.last.height = undefined;
                h.last.top = undefined;
                h.last.left = undefined;
            }
            scheduleSyncAll();
        }
    }, durationMs + 50);
}

export interface IframeHandle {
    wrapper: HTMLDivElement;
    track: (placeholder: HTMLElement) => void;
    untrack: () => void;
    destroy: () => void;
}

export function createIframeSlot(): IframeHandle {
    const layer = getContainer();
    const wrapper = document.createElement("div");
    wrapper.style.cssText = "position:fixed;pointer-events:auto;display:none;overflow:hidden;";
    layer.appendChild(wrapper);

    const h: InternalHandle = {
        wrapper,
        placeholder: null,
        pending: false,
        targetHeight: 0,
        last: {},
        placeholderRO: null,
    };
    handles.add(h);
    installListeners();

    function track(el: HTMLElement) {
        // Stop watching the previous placeholder.
        if (h.placeholderRO) {
            h.placeholderRO.disconnect();
            h.placeholderRO = null;
        }

        h.placeholder = el;
        h.targetHeight = 0; // force re-measure on next sync

        if (typeof ResizeObserver !== "undefined") {
            h.placeholderRO = new ResizeObserver(() => scheduleSync(h));
            h.placeholderRO.observe(el);
        }

        scheduleSync(h);
    }

    function untrack() {
        if (h.placeholderRO) {
            h.placeholderRO.disconnect();
            h.placeholderRO = null;
        }
        h.placeholder = null;
        writeStyle(wrapper.style, h.last, "display", "none");
    }

    function destroy() {
        untrack();
        handles.delete(h);
        wrapper.remove();
        uninstallListenersIfIdle();
    }

    return { wrapper, track, untrack, destroy };
}

// ── Instance-keyed slot pool ───────────────────────────────────────────────
//
// Iframes survive only if their Teleport target (the wrapper div) outlives
// the component owning the placeholder. The pool keeps one wrapper per
// instanceId for the whole session; <VncIframePool> Teleports a single
// <VncFrame> into each wrapper. Consumer components (<VncTile>) call
// acquireInstanceSlot() to obtain the wrapper and publish their placeholder
// via track(); when they unmount they untrack but the wrapper (and iframe)
// stays alive, so the next consumer (e.g. inline panel after a view switch
// or test switch) reuses the same iframe with no reload.

export interface PooledSlot {
    wrapper: HTMLDivElement;
    track: (placeholder: HTMLElement) => void;
    untrack: () => void;
}

interface PoolEntry {
    handle: IframeHandle;
    refCount: number;
}

const pool = new Map<string, PoolEntry>();

/**
 * Acquire (or reuse) a per-instance overlay slot. The returned wrapper is
 * stable for the lifetime of the session, regardless of how many consumers
 * mount and unmount. Always pair with releaseInstanceSlot(id).
 */
export function acquireInstanceSlot(instanceId: string): PooledSlot {
    let entry = pool.get(instanceId);
    if (!entry) {
        entry = { handle: createIframeSlot(), refCount: 0 };
        pool.set(instanceId, entry);
    }
    entry.refCount++;
    return {
        wrapper: entry.handle.wrapper,
        track: entry.handle.track,
        untrack: entry.handle.untrack,
    };
}

/**
 * Release a previously acquired slot. The wrapper is intentionally NOT
 * destroyed when refCount reaches zero — keeping it alive lets the iframe
 * survive consumer unmount/remount cycles (test switches, view switches).
 * Slots are only destroyed via destroyInstanceSlot() when an instance is
 * known to be permanently gone.
 */
export function releaseInstanceSlot(instanceId: string) {
    const entry = pool.get(instanceId);
    if (!entry) {
        return;
    }
    entry.refCount = Math.max(0, entry.refCount - 1);
}

/** Permanently destroy a pooled slot. Call when an instance is removed. */
export function destroyInstanceSlot(instanceId: string) {
    const entry = pool.get(instanceId);
    if (!entry) {
        return;
    }
    entry.handle.destroy();
    pool.delete(instanceId);
}
