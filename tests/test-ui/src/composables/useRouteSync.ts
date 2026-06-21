import { type Ref, watch } from "vue";
import { type RouteLocationRaw, useRoute, useRouter } from "vue-router";
import type { useInspectNavigation } from "./useInspectNavigation";
import type { TestStore } from "./useTestStore";
import type { ActiveView } from "./useTestUI";

type Inspect = ReturnType<typeof useInspectNavigation>;
type ActiveViewRef = Ref<ActiveView>;

/**
 * Two-way sync between the URL and the app's in-memory state, with App.vue kept
 * as the single shell (no <router-view>). Two independent dimensions:
 *
 *   - Base path  → view + selected test:  /  ·  /tests/:displayName  ·  /vnc
 *   - ?inspect=  → container-inspect modal overlay (composes with any base view)
 *
 * Direct state mutations (TestTree clicks, OutputPanel "Containers" bar, VNC
 * inspect buttons, modal peer-nav) are left untouched: they mutate
 * selectedTest / activeView / inspectId, which the state→route watchers reflect
 * into the URL. A `syncing` guard stops the route→state and state→route
 * watchers from ping-ponging.
 */
export function useRouteSync(store: TestStore, inspect: Inspect, activeView: ActiveViewRef): void {
    const route = useRoute();
    const router = useRouter();

    // True while we're applying one direction, so the opposite watcher no-ops.
    let syncing = false;

    // Set when a deep-linked test/instance hasn't streamed in yet (live mode); the
    // source-data watcher re-applies the route once the tree/instances populate.
    let pending = false;

    // Cold-visit latch: was the page loaded at the bare root (no test path, no
    // ?inspect)? Read from the loaded URL, NOT selectedTest — the store's
    // hydrate-time autoSelect has already mutated selectedTest by now (synchronously
    // in report mode), so keying off the URL dodges that race: deep-links leave this
    // false (untouched), a bare root defaults to the Overview.
    const initialHash = window.location.hash; // "", "#", "#/", or "#/<route>[?query]"
    const initialPath = initialHash.replace(/^#/, "").split("?")[0] ?? "";
    const initialQuery = initialHash.includes("?") ? initialHash.slice(initialHash.indexOf("?")) : "";
    const coldRootVisit = (initialPath === "" || initialPath === "/") && !initialQuery.includes("inspect=");

    function withSync(fn: () => void) {
        syncing = true;
        try {
            fn();
        } finally {
            syncing = false;
        }
    }

    // ── Route → state ────────────────────────────────────────────────────────

    function resolveTestFromName(name: string): boolean {
        // Vue Router decodes path params, but bracketed names have been encoded
        // inconsistently across versions — try raw, then one explicit decode.
        let t = store.findTest(name);
        if (!t) {
            try {
                t = store.findTest(decodeURIComponent(name));
            } catch {
                /* malformed escape — fall through to not-found */
            }
        }
        if (t) {
            store.selectTest(t);
            return true;
        }
        return false;
    }

    function applyBaseFromRoute() {
        const name = route.name;
        if (name === "test") {
            activeView.value = "tests";
            const displayName = String(route.params.displayName ?? "");
            if (resolveTestFromName(displayName)) {
                return;
            }
            store.selectTest(null);
            if (store.runDone) {
                // Tree is fully hydrated and the test isn't here — drop the stale path
                // (keep any ?inspect query) instead of stranding the URL.
                router.replace({ name: "tests", query: route.query });
            } else {
                pending = true;
            }
        } else {
            store.selectTest(null);
            activeView.value = name === "vnc" ? "vnc" : name === "overview" ? "overview" : "tests";
        }
    }

    function applyInspectFromRoute() {
        const id = route.query.inspect;
        const inspectId = typeof id === "string" && id.length > 0 ? id : null;
        if (!inspectId) {
            inspect.closeInspect();
            return;
        }
        inspect.openInspect(inspectId);
        if (inspect.inspectInstance.value) {
            return;
        }
        // openInspect set the stack, but the instance isn't loaded.
        if (store.runDone) {
            inspect.closeInspect();
            const query = { ...route.query };
            delete query.inspect;
            router.replace({ name: route.name ?? "tests", params: route.params, query });
        } else {
            pending = true;
        }
    }

    function applyFromRoute() {
        withSync(() => {
            pending = false;
            applyBaseFromRoute();
            applyInspectFromRoute();
        });
    }

    watch(
        () => route.fullPath,
        () => {
            if (!syncing) {
                applyFromRoute();
            }
        },
    );

    // One re-resolve path for both dimensions: when the tree or instance list
    // populates and a deep-link is still unresolved, re-apply the whole route.
    // The instance arrays are mutated in place (splice/push), so watch .length —
    // watching the array ref wouldn't fire on an in-place mutation.
    watch([store.collections, () => store.state.instances?.length, () => store.stoppedInstances.length], () => {
        if (pending) {
            applyFromRoute();
        }
    });

    // ── State → route ─────────────────────────────────────────────────────────

    // Base: view + selected test. A WS auto-select uses replace (no history spam);
    // user clicks push. Preserve the current ?inspect query so a view switch
    // doesn't clobber an open modal.
    watch([activeView, () => store.selectedTest], () => {
        if (syncing) {
            return;
        }
        const query = route.query;
        let target: RouteLocationRaw;
        if (activeView.value === "overview") {
            target = { name: "overview", query };
        } else if (activeView.value === "vnc") {
            target = { name: "vnc", query };
        } else if (store.selectedTest) {
            target = { name: "test", params: { displayName: store.selectedTest.displayName }, query };
        } else {
            target = { name: "tests", query };
        }
        const auto = store.lastSelectionWasAuto.value;
        store.lastSelectionWasAuto.value = false;
        if (auto) {
            router.replace(target);
        } else {
            router.push(target);
        }
    });

    // Modal: reflect inspectId into ?inspect, preserving the base path. Always a
    // user action (no auto-inspect), so always push.
    watch(
        () => inspect.inspectId.value,
        (id) => {
            if (syncing) {
                return;
            }
            const query = { ...route.query };
            if (id) {
                query.inspect = id;
            } else {
                delete query.inspect;
            }
            router.push({ name: route.name ?? "tests", params: route.params, query });
        },
    );

    // ── Initial resolve ───────────────────────────────────────────────────────
    if (coldRootVisit) {
        // Show the Overview and clear the hydrate-time autoSelect (else it strands
        // the URL at /tests/<name> or pre-empts the view). Inside withSync so the
        // state→route watcher stays quiet; replace (not push) parks the URL at
        // /overview without a back-button trap to /.
        withSync(() => {
            activeView.value = "overview";
            store.selectTest(null);
            applyInspectFromRoute();
        });
        router.replace({ name: "overview", query: route.query });
    } else {
        applyFromRoute();
    }
}
