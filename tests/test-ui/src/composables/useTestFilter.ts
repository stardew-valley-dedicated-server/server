import { computed, type Ref, ref } from "vue";
import type { TestSnapshot } from "../types/state";

export const allStatuses = [
    "running",
    "queued",
    "passed",
    "failed",
    "canceled",
    "skipped",
    "notDispatched",
    "aborted",
    "pending",
] as const;
export type TestStatus = (typeof allStatuses)[number];

export function useTestFilter(statusCounts: Record<string, number>, searchQuery: Ref<string>) {
    const activeFilters = ref<Set<TestStatus>>(new Set(allStatuses));

    function toggleFilter(status: TestStatus) {
        if (activeFilters.value.has(status)) {
            if (activeFilters.value.size <= 1) {
                return;
            }
            activeFilters.value.delete(status);
        } else {
            activeFilters.value.add(status);
        }
    }

    const visibleStatuses = computed(() => allStatuses.filter((s) => statusCounts[s] > 0));

    const isFiltering = computed(() => activeFilters.value.size < allStatuses.length);

    function testPassesFilter(test: TestSnapshot): boolean {
        if (!activeFilters.value.has(test.status as TestStatus)) {
            return false;
        }
        if (searchQuery.value) {
            return test.displayName.toLowerCase().includes(searchQuery.value.toLowerCase());
        }
        return true;
    }

    function setExclusiveFilter(status: TestStatus) {
        activeFilters.value = new Set([status]);
    }

    return {
        activeFilters,
        toggleFilter,
        visibleStatuses,
        isFiltering,
        testPassesFilter,
        setExclusiveFilter,
    };
}
