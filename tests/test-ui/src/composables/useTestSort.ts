import { ref } from "vue";
import type { ClassSnapshot, TestSnapshot } from "../types/state";

export type SortMode =
    | "none"
    | "name-asc"
    | "name-desc"
    | "duration-asc"
    | "duration-desc"
    | "started-asc"
    | "started-desc"
    | "finished-asc"
    | "finished-desc";

export function useTestSort() {
    const sortMode = ref<SortMode>("none");

    function cycleSortMode(field: "name" | "duration" | "started" | "finished") {
        const current = sortMode.value;
        const asc = `${field}-asc` as SortMode;
        const desc = `${field}-desc` as SortMode;
        if (field === "name") {
            // asc (A-Z) -> desc (Z-A) -> none
            if (current === asc) sortMode.value = desc;
            else if (current === desc) sortMode.value = "none";
            else sortMode.value = asc;
        } else if (field === "duration") {
            // desc (longest first) -> asc (shortest first) -> none
            if (current === desc) sortMode.value = asc;
            else if (current === asc) sortMode.value = "none";
            else sortMode.value = desc;
        } else {
            // Time-based sorts: asc (earliest first) -> desc (latest first) -> none
            if (current === asc) sortMode.value = desc;
            else if (current === desc) sortMode.value = "none";
            else sortMode.value = asc;
        }
    }

    function testFinishTime(t: TestSnapshot): number | null {
        if (t.runningStartTime == null || t.durationMs == null) return null;
        return new Date(t.runningStartTime).getTime() + t.durationMs;
    }

    function sortTests(tests: TestSnapshot[]): TestSnapshot[] {
        const sorted = [...tests];
        const mode = sortMode.value;
        if (mode === "name-asc") return sorted.sort((a, b) => a.displayName.localeCompare(b.displayName));
        if (mode === "name-desc") return sorted.sort((a, b) => b.displayName.localeCompare(a.displayName));
        if (mode === "duration-asc")
            return sorted.sort(
                (a, b) => (a.durationMs ?? Number.POSITIVE_INFINITY) - (b.durationMs ?? Number.POSITIVE_INFINITY),
            );
        if (mode === "duration-desc")
            return sorted.sort(
                (a, b) => (b.durationMs ?? Number.NEGATIVE_INFINITY) - (a.durationMs ?? Number.NEGATIVE_INFINITY),
            );
        if (mode === "started-asc")
            return sorted.sort((a, b) =>
                (a.runningStartTime ?? "\uffff").localeCompare(b.runningStartTime ?? "\uffff"),
            );
        if (mode === "started-desc")
            return sorted.sort((a, b) => (b.runningStartTime ?? "").localeCompare(a.runningStartTime ?? ""));
        if (mode === "finished-asc")
            return sorted.sort(
                (a, b) =>
                    (testFinishTime(a) ?? Number.POSITIVE_INFINITY) - (testFinishTime(b) ?? Number.POSITIVE_INFINITY),
            );
        if (mode === "finished-desc")
            return sorted.sort(
                (a, b) =>
                    (testFinishTime(b) ?? Number.NEGATIVE_INFINITY) - (testFinishTime(a) ?? Number.NEGATIVE_INFINITY),
            );
        return sorted;
    }

    function sortClasses(classes: ClassSnapshot[]): ClassSnapshot[] {
        const mode = sortMode.value;
        if (mode === "none") return classes;
        const sorted = classes.map((cls) => ({ ...cls, tests: sortTests(cls.tests) }));
        if (mode === "name-asc" || mode === "name-desc") {
            const dir = mode === "name-asc" ? 1 : -1;
            sorted.sort((a, b) => dir * a.name.localeCompare(b.name));
        } else if (mode === "duration-asc" || mode === "duration-desc") {
            const aggDuration = (cls: ClassSnapshot) => {
                let total = 0;
                for (const t of cls.tests) {
                    if (t.durationMs == null)
                        return mode === "duration-asc" ? Number.POSITIVE_INFINITY : Number.NEGATIVE_INFINITY;
                    total += t.durationMs;
                }
                return cls.tests.length > 0
                    ? total
                    : mode === "duration-asc"
                      ? Number.POSITIVE_INFINITY
                      : Number.NEGATIVE_INFINITY;
            };
            const dir = mode === "duration-asc" ? 1 : -1;
            sorted.sort((a, b) => dir * (aggDuration(a) - aggDuration(b)));
        } else if (mode === "started-asc" || mode === "started-desc") {
            const earliest = (cls: ClassSnapshot) => {
                let min = "\uffff";
                for (const t of cls.tests)
                    if (t.runningStartTime != null && t.runningStartTime < min) min = t.runningStartTime;
                return min;
            };
            const dir = mode === "started-asc" ? 1 : -1;
            sorted.sort((a, b) => dir * earliest(a).localeCompare(earliest(b)));
        } else if (mode === "finished-asc" || mode === "finished-desc") {
            const latestFinish = (cls: ClassSnapshot) => {
                let max = mode === "finished-asc" ? Number.POSITIVE_INFINITY : Number.NEGATIVE_INFINITY;
                for (const t of cls.tests) {
                    const ft = testFinishTime(t);
                    if (ft == null) return max;
                    if (mode === "finished-asc" ? ft < max : ft > max) max = ft;
                }
                return max;
            };
            const dir = mode === "finished-asc" ? 1 : -1;
            sorted.sort((a, b) => dir * (latestFinish(a) - latestFinish(b)));
        }
        return sorted;
    }

    return { sortMode, cycleSortMode, testFinishTime, sortTests, sortClasses };
}
