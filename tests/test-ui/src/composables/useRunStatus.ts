import { type ComputedRef, computed } from "vue";
import type { TestStore } from "./useTestStore";

/**
 * Overall run status reduced to a one-word label and a Tailwind text color,
 * derived from `state.status` plus the failed/canceled/passed counts. Shared by
 * StatusBar (the header strip) and OverviewPanel (the landing summary) so the two
 * never drift — the priority order (aborted/running, then failed > canceled >
 * passed) lives in exactly one place.
 */
export function useRunStatus(store: TestStore): {
    statusLabel: ComputedRef<string>;
    statusTextClass: ComputedRef<string>;
} {
    const sc = store.statusCounts;

    const statusLabel = computed(() => {
        if (store.state.status === "aborted") {
            return "aborted";
        }
        if (store.state.status === "running") {
            return "running";
        }
        if (store.state.status === "finished") {
            if (sc.failed > 0) {
                return "failed";
            }
            if (sc.canceled > 0) {
                return "canceled";
            }
            if (sc.passed > 0) {
                return "passed";
            }
            return "no tests ran";
        }
        return "pending";
    });

    const statusTextClass = computed(() => {
        if (store.state.status === "running") {
            return "text-success";
        }
        if (store.state.status === "finished") {
            if (sc.failed > 0) {
                return "text-error";
            }
            if (sc.canceled > 0) {
                return "text-warning";
            }
            if (sc.passed > 0) {
                return "text-success";
            }
            return "text-warning";
        }
        if (store.state.status === "aborted") {
            return "text-warning";
        }
        return "text-base-content/50";
    });

    return { statusLabel, statusTextClass };
}
