/**
 * Typed composable wrapping provide/inject for the test-ui's shared state.
 * Replaces raw `inject<T>('key')!` with proper typing and error messages.
 */

import type { Ref } from "vue";
import { inject } from "vue";
import type { useInspectNavigation } from "./useInspectNavigation";
import type { TestStore } from "./useTestStore";

export type InspectNavigation = ReturnType<typeof useInspectNavigation>;

/**
 * Access the test store and shared UI state.
 * Must be called inside a component that is a descendant of App.vue.
 */
export function useTestUI() {
    const store = inject<TestStore>("store");
    if (!store) {
        throw new Error("useTestUI: store not provided. Component must be inside App.vue");
    }

    const activeView = inject<Ref<"tests" | "vnc">>("activeView");
    if (!activeView) {
        throw new Error("useTestUI: activeView not provided");
    }

    const inspect = inject<InspectNavigation>("inspect");
    if (!inspect) {
        throw new Error("useTestUI: inspect not provided");
    }

    return { store, activeView, inspect };
}

/**
 * Access the filter trigger (optional, not all components need it).
 */
export function useFilterTrigger() {
    const filterToStatus = inject<Ref<string | null>>("filterToStatus");
    if (!filterToStatus) {
        throw new Error("useFilterTrigger: filterToStatus not provided");
    }
    return { filterToStatus };
}

/**
 * Access the showFailedTests callback (optional, only StatusBar needs it).
 */
export function useShowFailed() {
    const showFailedTests = inject<() => void>("showFailedTests");
    if (!showFailedTests) {
        throw new Error("useShowFailed: showFailedTests not provided");
    }
    return { showFailedTests };
}
