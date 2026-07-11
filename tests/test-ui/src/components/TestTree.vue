<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { allStatuses, type TestStatus, useTestFilter } from "../composables/useTestFilter";
import { useTestSort } from "../composables/useTestSort";
import { useFilterTrigger, useTestUI } from "../composables/useTestUI";
import type { ClassSnapshot, TestSnapshot } from "../types/state";
import { shortTestName } from "../utils/format";
import { TERM_HELP } from "../utils/glossary";
import { statusFilterClass } from "../utils/status";
import EmptyState from "./EmptyState.vue";
import SortIcon from "./SortIcon.vue";
import StatusIcon from "./StatusIcon.vue";
import TestTreeItem from "./TestTreeItem.vue";

const { store } = useTestUI();
const { filterToStatus } = useFilterTrigger();
const { sortMode, cycleSortMode, testFinishTime, sortTests, sortClasses } = useTestSort();
const collapsed = ref<Set<string>>(new Set());
const userCollapsed = ref<Set<string>>(new Set());
const searchQuery = ref("");
const searchInputRef = ref<HTMLInputElement | null>(null);
const viewMode = ref<"grouped" | "timeline">("grouped");

const statusCounts = store.statusCounts;
const { activeFilters, toggleFilter, visibleStatuses, isFiltering, testPassesFilter, setExclusiveFilter } =
    useTestFilter(statusCounts, searchQuery);

// Status-pill tooltip: the term definition plus the toggle action it performs.
function statusTitle(status: TestStatus): string {
    const action = activeFilters.value.has(status) ? "Hide" : "Show";
    return `${TERM_HELP[status] ?? ""}\n\n${action} ${status} tests.`.trim();
}

// Flaky lookup. Each entry's `test` matches the test name (className.method form),
// so we match against the test's displayName (xUnit's full display name) endsWith
// the flaky entry's test name. Empty map until the run-end flaky_tests event arrives.
const flakyByName = computed(() => {
    const map = new Map<string, { failRate: number; recentRuns: number }>();
    const entries = store.state.flakyTests;
    if (!entries) {
        return map;
    }
    for (const e of entries) {
        map.set(e.test, { failRate: e.failRate, recentRuns: e.recentRuns });
    }
    return map;
});

function flakyFor(test: TestSnapshot): { failRate: number; recentRuns: number } | null {
    // FlakinessTracker keys by full test name; the snapshot exposes the same on displayName.
    return flakyByName.value.get(test.displayName) ?? null;
}

// Auto-expand all nodes when search is active
watch(searchQuery, (q) => {
    if (q) {
        collapsed.value.clear();
    }
});

// React to external filter trigger (e.g., clicking "failed" in status bar).
// immediate: the Overview sets filterToStatus *before* this tree mounts (the tree
// is v-if'd on the tests view), so a lazy watcher would miss that pre-set value —
// the immediate run applies it on mount.
watch(
    filterToStatus,
    (status) => {
        if (!status) {
            return;
        }
        const s = status as TestStatus;
        if (allStatuses.includes(s)) {
            setExclusiveFilter(s);
            // Expand all so filtered results are visible
            collapsed.value.clear();
            userCollapsed.value.clear();
            // Reset keyboard focus so next arrow key starts from the top of filtered results
            focusedKey.value = null;
        }
        // Reset trigger so it can fire again
        filterToStatus.value = null;
    },
    { immediate: true },
);

// Filtered collections: remove tests that don't match, then remove empty classes/collections
const filteredCollections = computed(() => {
    let cols = store.collections.value;
    if (isFiltering.value || searchQuery.value) {
        cols = cols
            .map((col) => ({
                ...col,
                classes: col.classes
                    .map((cls) => ({
                        ...cls,
                        tests: cls.tests.filter(testPassesFilter),
                    }))
                    .filter((cls) => cls.tests.length > 0),
            }))
            .filter((col) => col.classes.length > 0);
    }
    if (sortMode.value !== "none") {
        cols = cols.map((col) => ({ ...col, classes: sortClasses(col.classes) }));
        // Also sort collections themselves
        const mode = sortMode.value;
        if (mode === "name-asc" || mode === "name-desc") {
            const dir = mode === "name-asc" ? 1 : -1;
            cols = [...cols].sort((a, b) => dir * a.name.localeCompare(b.name));
        } else if (mode === "duration-asc" || mode === "duration-desc") {
            const aggColDuration = (col: { classes: { tests: { durationMs?: number | null }[] }[] }) => {
                let total = 0;
                for (const cls of col.classes) {
                    for (const t of cls.tests) {
                        if (t.durationMs == null) {
                            return mode === "duration-asc" ? Number.POSITIVE_INFINITY : Number.NEGATIVE_INFINITY;
                        }
                        total += t.durationMs;
                    }
                }
                return total;
            };
            const dir = mode === "duration-asc" ? 1 : -1;
            cols = [...cols].sort((a, b) => dir * (aggColDuration(a) - aggColDuration(b)));
        } else if (mode === "started-asc" || mode === "started-desc") {
            const earliestStart = (col: { classes: { tests: TestSnapshot[] }[] }) => {
                let min = "\uffff";
                for (const cls of col.classes) {
                    for (const t of cls.tests) {
                        if (t.runningStartTime != null && t.runningStartTime < min) {
                            min = t.runningStartTime;
                        }
                    }
                }
                return min;
            };
            const dir = mode === "started-asc" ? 1 : -1;
            cols = [...cols].sort((a, b) => dir * earliestStart(a).localeCompare(earliestStart(b)));
        } else if (mode === "finished-asc" || mode === "finished-desc") {
            const latestFinish = (col: { classes: { tests: TestSnapshot[] }[] }) => {
                let max = mode === "finished-asc" ? Number.POSITIVE_INFINITY : Number.NEGATIVE_INFINITY;
                for (const cls of col.classes) {
                    for (const t of cls.tests) {
                        const ft = testFinishTime(t);
                        if (ft == null) {
                            return max;
                        }
                        if (mode === "finished-asc" ? ft < max : ft > max) {
                            max = ft;
                        }
                    }
                }
                return max;
            };
            const dir = mode === "finished-asc" ? 1 : -1;
            cols = [...cols].sort((a, b) => dir * (latestFinish(a) - latestFinish(b)));
        }
    }
    return cols;
});

// Flatten collections → classes (removes the redundant "Test Collection for XXX" level)
const flatClasses = computed(() => {
    const classes: ClassSnapshot[] = [];
    for (const col of filteredCollections.value) {
        for (const cls of col.classes) {
            classes.push(cls);
        }
    }
    return classes;
});

// Timeline view: flat list of all tests sorted by execution order
const timelineTests = computed(() => {
    const all: TestSnapshot[] = [];
    for (const col of filteredCollections.value) {
        for (const cls of col.classes) {
            for (const test of cls.tests) {
                all.push(test);
            }
        }
    }

    if (sortMode.value !== "none") {
        return sortTests(all);
    }

    const executed = all.filter((t) => t.executionOrder != null).sort((a, b) => a.executionOrder! - b.executionOrder!);
    const notExecuted = all
        .filter((t) => t.executionOrder == null)
        .sort((a, b) => {
            const da = a.discoveryOrder ?? Number.POSITIVE_INFINITY;
            const db = b.discoveryOrder ?? Number.POSITIVE_INFINITY;
            if (da !== db) {
                return da - db;
            }
            return a.displayName.localeCompare(b.displayName);
        });
    return [...executed, ...notExecuted];
});

// Focused node key for keyboard navigation (independent of test selection)
const focusedKey = ref<string | null>(null);

function toggleCollapse(key: string) {
    if (collapsed.value.has(key)) {
        collapsed.value.delete(key);
        userCollapsed.value.delete(key);
    } else {
        collapsed.value.add(key);
        userCollapsed.value.add(key);
    }
}

function isExpanded(key: string) {
    return !collapsed.value.has(key);
}

// Auto-expand tree nodes containing the selected test and scroll into view.
// `pendingScroll` defers scrolling until first paint when selection changes
// happen pre-load (e.g. auto-select on hydrate fires before stylesheets settle).
let pendingScroll = false;
function scrollFocusedAfterLoad() {
    pendingScroll = false;
    nextTick(() => {
        document.querySelector('[data-focused="true"]')?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    });
}
watch(
    () => store.selectedTest,
    (test) => {
        if (!test) {
            return;
        }
        for (const col of store.collections.value) {
            for (const cls of col.classes) {
                if (cls.tests.some((t) => t.displayName === test.displayName)) {
                    const clsKey = `cls:${cls.name}`;
                    if (!userCollapsed.value.has(clsKey)) {
                        collapsed.value.delete(clsKey);
                    }
                }
            }
        }
        if (document.readyState === "complete") {
            scrollFocusedAfterLoad();
        } else if (!pendingScroll) {
            pendingScroll = true;
            window.addEventListener("load", scrollFocusedAfterLoad, { once: true });
        }
    },
);

function aggregateStatus(tests: TestSnapshot[]): string {
    if (tests.some((t) => t.status === "failed")) {
        return "failed";
    }
    if (tests.some((t) => t.status === "canceled")) {
        return "canceled";
    }
    if (tests.some((t) => t.status === "aborted")) {
        return "aborted";
    }
    if (tests.some((t) => t.status === "running")) {
        return "running";
    }
    if (tests.some((t) => t.status === "queued")) {
        return "queued";
    }
    if (tests.every((t) => t.status === "passed") && tests.length > 0) {
        return "passed";
    }
    if (tests.every((t) => t.status === "skipped") && tests.length > 0) {
        return "skipped";
    }
    if (tests.every((t) => t.status === "notDispatched") && tests.length > 0) {
        return "notDispatched";
    }
    // Some passed but not all. Only show as running if the run is actually in progress.
    if (tests.some((t) => t.status === "passed") && store.state.status === "running") {
        return "running";
    }
    if (tests.some((t) => t.status === "passed")) {
        return "passed";
    }
    return "pending";
}

// ── Setup phase grouping ──
const sharedSetupPhases = computed(() => store.state.setupPhases.filter((p) => p.collectionName == null));

function classStatus(cls: { tests: TestSnapshot[] }): string {
    return aggregateStatus(cls.tests);
}

function classDuration(cls: { tests: TestSnapshot[] }): number | null {
    let total = 0;
    for (const test of cls.tests) {
        if (test.durationMs == null) {
            return null;
        }
        total += test.durationMs;
    }
    return cls.tests.length > 0 ? total : null;
}

// ── Expand/Collapse all ──
const allCollapsed = computed(() => {
    const classes = flatClasses.value;
    if (classes.length === 0) {
        return true;
    }
    for (const cls of classes) {
        if (!collapsed.value.has(`cls:${cls.name}`)) {
            return false;
        }
    }
    return true;
});

function expandAll() {
    // Only expand test classes, preserve setup section collapse state
    for (const cls of flatClasses.value) {
        const clsKey = `cls:${cls.name}`;
        collapsed.value.delete(clsKey);
        userCollapsed.value.delete(clsKey);
    }
}

function collapseAll() {
    for (const cls of flatClasses.value) {
        const clsKey = `cls:${cls.name}`;
        collapsed.value.add(clsKey);
        userCollapsed.value.add(clsKey);
    }
}

function selectTest(test: TestSnapshot) {
    store.selectTest(test);
}

// ── Flat visible node list for keyboard navigation ──

type TreeNode = {
    key: string;
    type: "class" | "test";
    parentKey?: string;
    test?: TestSnapshot;
    isGroup: boolean;
};

const visibleNodes = computed((): TreeNode[] => {
    const nodes: TreeNode[] = [];
    if (viewMode.value === "timeline") {
        for (const test of timelineTests.value) {
            nodes.push({ key: `test:${test.displayName}`, type: "test", test, isGroup: false });
        }
        return nodes;
    }
    for (const cls of flatClasses.value) {
        const clsKey = `cls:${cls.name}`;
        nodes.push({ key: clsKey, type: "class", isGroup: true });
        if (!isExpanded(clsKey)) {
            continue;
        }
        for (const test of cls.tests) {
            nodes.push({ key: `test:${test.displayName}`, type: "test", parentKey: clsKey, test, isGroup: false });
        }
    }
    return nodes;
});

function focusedIndex(): number {
    if (!focusedKey.value) {
        return -1;
    }
    return visibleNodes.value.findIndex((n) => n.key === focusedKey.value);
}

function scrollFocusedIntoView() {
    nextTick(() => {
        const el = document.querySelector('[data-focused="true"]');
        el?.scrollIntoView({ block: "nearest", behavior: "smooth" });
    });
}

function onKeyDown(e: KeyboardEvent) {
    const target = e.target as HTMLElement;
    const tag = target?.tagName;
    // Allow Escape in search input to clear and blur
    if ((tag === "INPUT" || tag === "TEXTAREA") && e.key === "Escape") {
        searchQuery.value = "";
        target.blur();
        e.preventDefault();
        return;
    }
    if (tag === "INPUT" || tag === "TEXTAREA") {
        return;
    }
    // Don't handle keys when lightbox or modal is open (they take priority)
    if (document.querySelector("[data-lightbox]") || document.querySelector("[data-modal]")) {
        return;
    }

    const nodes = visibleNodes.value;
    if (nodes.length === 0) {
        return;
    }

    switch (e.key) {
        case "ArrowDown":
        case "j": {
            e.preventDefault();
            const idx = focusedIndex();
            const next = idx < nodes.length - 1 ? idx + 1 : 0;
            focusedKey.value = nodes[next].key;
            // Auto-select if it's a test
            if (nodes[next].test) {
                store.selectTest(nodes[next].test!);
            }
            scrollFocusedIntoView();
            break;
        }
        case "ArrowUp":
        case "k": {
            e.preventDefault();
            const idx = focusedIndex();
            const prev = idx > 0 ? idx - 1 : nodes.length - 1;
            focusedKey.value = nodes[prev].key;
            if (nodes[prev].test) {
                store.selectTest(nodes[prev].test!);
            }
            scrollFocusedIntoView();
            break;
        }
        case "ArrowRight": {
            e.preventDefault();
            const idx = focusedIndex();
            if (idx < 0) {
                break;
            }
            const node = nodes[idx];
            if (node.isGroup && collapsed.value.has(node.key)) {
                // Expand
                collapsed.value.delete(node.key);
                userCollapsed.value.delete(node.key);
            } else if (node.isGroup && idx < nodes.length - 1) {
                // Already expanded, move to first child
                focusedKey.value = nodes[idx + 1].key;
                if (nodes[idx + 1].test) {
                    store.selectTest(nodes[idx + 1].test!);
                }
                scrollFocusedIntoView();
            }
            break;
        }
        case "ArrowLeft": {
            e.preventDefault();
            const idx = focusedIndex();
            if (idx < 0) {
                break;
            }
            const node = nodes[idx];
            if (node.isGroup && !collapsed.value.has(node.key)) {
                // Collapse
                collapsed.value.add(node.key);
                userCollapsed.value.add(node.key);
            } else if (node.parentKey) {
                // Go to parent
                focusedKey.value = node.parentKey;
                scrollFocusedIntoView();
            }
            break;
        }
        case "Escape": {
            e.preventDefault();
            const idx = focusedIndex();
            if (idx < 0) {
                break;
            }
            const node = nodes[idx];
            if (node.parentKey) {
                focusedKey.value = node.parentKey;
                scrollFocusedIntoView();
            }
            break;
        }
        case "Enter":
        case " ": {
            e.preventDefault();
            const idx = focusedIndex();
            if (idx < 0) {
                break;
            }
            const node = nodes[idx];
            if (node.isGroup) {
                toggleCollapse(node.key);
            } else if (node.test) {
                store.selectTest(node.test);
            }
            break;
        }
        case "f": {
            e.preventDefault();
            searchInputRef.value?.focus();
            break;
        }
        default:
            return; // Don't stop propagation for unhandled keys
    }
}

onMounted(() => window.addEventListener("keydown", onKeyDown));
onUnmounted(() => window.removeEventListener("keydown", onKeyDown));
</script>

<template>
  <div class="flex flex-col h-full">
    <!-- Tree header -->
    <div class="flex items-center px-4 h-12 border-b border-base-content/5 flex-none">
      <span class="text-xs font-semibold uppercase tracking-widest text-base-content/40">Explorer</span>
    </div>

    <!-- Tree content -->
    <div class="flex-1 min-h-0 panel-scroll py-1">
      <!-- Setup Phases section -->
      <template v-if="store.state.setupPhases.length > 0">
        <div class="px-4 pt-3 pb-1 flex items-center gap-1 cursor-pointer select-none"
             @click="toggleCollapse('section:setup')">
          <Icon icon="lucide:chevron-right" class="w-3 h-3 text-base-content/40 transition-transform duration-150" :class="{ 'rotate-90': isExpanded('section:setup') }" />
          <span class="text-[11px] font-semibold uppercase tracking-widest text-base-content/40">Setup</span>
        </div>
        <template v-if="isExpanded('section:setup')">
        <!-- Shared phases (e.g. Docker Images), expandable with inline steps -->
        <template v-for="phase in sharedSetupPhases" :key="`phase-${phase.category}-${phase.phase}-shared`">
          <TestTreeItem
            :label="phase.phase"
            :status="phase.status"
            :icon="phase.phase.toLowerCase().includes('docker') || phase.phase.toLowerCase().includes('image') ? 'docker' : undefined"
            :indent="0"
            :clickable="true"
            :is-group="true"
            :is-expanded="isExpanded(`phase-${phase.category}-${phase.phase}-shared`)"
            @click="toggleCollapse(`phase-${phase.category}-${phase.phase}-shared`)"
          />
          <template v-if="isExpanded(`phase-${phase.category}-${phase.phase}-shared`)">
            <TestTreeItem
              v-for="step in phase.steps"
              :key="step.step"
              :label="step.step"
              :status="step.status"
              :subtitle="step.details || undefined"
              :indent="1"
              :clickable="true"
              :is-selected="store.selectedStep === step"
              :show-progress="step.status === 'completed' || step.status === 'in_progress'"
              :progress-complete="step.status === 'completed'"
              @click="store.selectStep(step)"
            />
          </template>
        </template>

        </template>
      </template>

      <!-- Errors section -->
      <template v-if="store.state.errors.length > 0">
        <div class="px-4 pt-2 pb-1 flex items-center gap-2">
          <span class="text-[11px] font-semibold uppercase tracking-widest text-error/60">Errors</span>
          <span class="text-[10px] tabular-nums px-1.5 py-0.5 rounded-full bg-error/10 text-error/70 font-medium">
            {{ store.state.errors.length }}
          </span>
        </div>
        <TestTreeItem
          v-for="(error, idx) in store.state.errors"
          :key="`error-${idx}`"
          :label="error.message.split('\n')[0].slice(0, 80)"
          status="failed"
          :indent="0"
          :clickable="true"
          :is-selected="store.selectedError === error"
          @click="store.selectError(error)"
        />
      </template>

      <!-- Spacing between sections -->
      <div v-if="(store.state.setupPhases.length > 0 || store.state.errors.length > 0) && store.collections.value.length > 0"
           class="h-1" />

      <!-- Test Collections section -->
      <template v-if="store.collections.value.length > 0">
        <div class="px-4 pt-3 pb-1 flex items-center justify-between">
          <div class="flex items-center gap-2">
            <span class="text-[11px] font-semibold uppercase tracking-widest text-base-content/40">Tests</span>
          </div>
          <div class="flex items-center gap-1">
            <!-- Sort by name -->
            <button
              class="w-5 h-5 flex items-center justify-center rounded transition-colors"
              :class="sortMode.startsWith('name') ? 'text-primary bg-primary/10' : 'text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5'"
              :title="sortMode === 'name-asc' ? 'Sort Z-A' : sortMode === 'name-desc' ? 'Clear sort' : 'Sort A-Z'"
              @click="cycleSortMode('name')"
            >
              <SortIcon icon="lucide:a-large-small" :direction="sortMode === 'name-asc' ? 'asc' : sortMode === 'name-desc' ? 'desc' : null" />
            </button>
            <!-- Sort by duration -->
            <button
              class="w-5 h-5 flex items-center justify-center rounded transition-colors"
              :class="sortMode.startsWith('duration') ? 'text-primary bg-primary/10' : 'text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5'"
              :title="sortMode === 'duration-desc' ? 'Sort shortest running first' : sortMode === 'duration-asc' ? 'Clear sort' : 'Sort longest running first'"
              @click="cycleSortMode('duration')"
            >
              <SortIcon icon="lucide:hourglass" :direction="sortMode === 'duration-asc' ? 'asc' : sortMode === 'duration-desc' ? 'desc' : null" />
            </button>
            <!-- Sort by start time -->
            <button
              class="w-5 h-5 flex items-center justify-center rounded transition-colors"
              :class="sortMode.startsWith('started') ? 'text-primary bg-primary/10' : 'text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5'"
              :title="sortMode === 'started-asc' ? 'Sort latest started first' : sortMode === 'started-desc' ? 'Clear sort' : 'Sort earliest started first'"
              @click="cycleSortMode('started')"
            >
              <SortIcon icon="lucide:play" :direction="sortMode === 'started-asc' ? 'asc' : sortMode === 'started-desc' ? 'desc' : null" />
            </button>
            <!-- Sort by finish time -->
            <button
              class="w-5 h-5 flex items-center justify-center rounded transition-colors"
              :class="sortMode.startsWith('finished') ? 'text-primary bg-primary/10' : 'text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5'"
              :title="sortMode === 'finished-asc' ? 'Sort latest finished first' : sortMode === 'finished-desc' ? 'Clear sort' : 'Sort earliest finished first'"
              @click="cycleSortMode('finished')"
            >
              <SortIcon icon="lucide:circle-check" :direction="sortMode === 'finished-asc' ? 'asc' : sortMode === 'finished-desc' ? 'desc' : null" />
            </button>
            <div class="w-px h-3.5 bg-base-content/10 mx-0.5" />
            <!-- View mode toggle -->
            <button
              class="w-5 h-5 flex items-center justify-center rounded transition-colors text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5"
              :title="viewMode === 'grouped' ? 'Switch to timeline view' : 'Switch to grouped view'"
              @click="viewMode = viewMode === 'grouped' ? 'timeline' : 'grouped'"
            >
              <Icon :icon="viewMode === 'grouped' ? 'lucide:list-tree' : 'lucide:list'" class="w-3.5 h-3.5" />
            </button>
            <!-- Expand/collapse all -->
            <button
              class="w-5 h-5 flex items-center justify-center rounded transition-colors"
              :class="viewMode === 'grouped'
                ? 'text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5 cursor-pointer'
                : 'text-base-content/15 cursor-not-allowed'"
              :disabled="viewMode !== 'grouped'"
              :title="viewMode !== 'grouped' ? 'Only available in grouped view' : allCollapsed ? 'Expand all' : 'Collapse all'"
              @click="viewMode === 'grouped' && (allCollapsed ? expandAll() : collapseAll())"
            >
              <Icon v-if="allCollapsed && viewMode === 'grouped'" icon="lucide:chevrons-up" class="w-3.5 h-3.5" />
              <Icon v-else icon="lucide:chevrons-down" class="w-3.5 h-3.5" />
            </button>
          </div>
        </div>

        <!-- Search input -->
        <div class="px-3 pt-1 pb-2">
          <div class="relative">
            <Icon icon="lucide:search" class="absolute left-2 top-1/2 -translate-y-1/2 w-3 h-3 text-base-content/30" />
            <input
              ref="searchInputRef"
              v-model="searchQuery"
              type="text"
              placeholder="Filter tests... (f)"
              class="w-full text-xs bg-base-300/40 border border-base-content/10 rounded pl-7 pr-6 py-1 placeholder:text-base-content/25 focus:outline-none focus:border-primary/30"
            />
            <button
              v-if="searchQuery"
              class="absolute right-1.5 top-1/2 -translate-y-1/2 w-4 h-4 flex items-center justify-center rounded text-base-content/30 hover:text-base-content/60"
              @click="searchQuery = ''"
            >
              <Icon icon="lucide:x" class="w-3 h-3" />
            </button>
          </div>
        </div>

        <!-- Status filter pills -->
        <div v-if="visibleStatuses.length > 1" class="px-4 pb-2 flex flex-wrap gap-1">
          <button
            v-for="status in visibleStatuses"
            :key="status"
            class="inline-flex items-center gap-1 h-5 px-1.5 rounded text-[10px] tabular-nums font-bold transition-[opacity,background-color,color] duration-150 ease-out"
            :class="[
              activeFilters.has(status) ? 'opacity-100' : 'opacity-30',
              statusFilterClass(status)
            ]"
            :title="statusTitle(status)"
            @click="toggleFilter(status)"
          >
            <StatusIcon :status="status" :size="10" />
            {{ statusCounts[status] }}
          </button>
        </div>

        <!-- Grouped view (flat: classes directly, no collection wrapper) -->
        <div v-if="viewMode === 'grouped'">
          <template v-for="cls in flatClasses" :key="`cls:${cls.name}`">
            <TestTreeItem
              :label="cls.name"
              :status="classStatus(cls)"
              :duration-ms="classDuration(cls)"
              :indent="0"
              :clickable="true"
              :is-group="true"
              :is-expanded="isExpanded(`cls:${cls.name}`)"
              :is-focused="focusedKey === `cls:${cls.name}`"
              :count="cls.tests.length"
              :count-title="cls.tests.length === 1 ? '1 test' : `${cls.tests.length} tests`"
              :data-focused="focusedKey === `cls:${cls.name}` || undefined"
              @click="toggleCollapse(`cls:${cls.name}`); focusedKey = `cls:${cls.name}`"
            />
            <TestTreeItem
              v-for="test in isExpanded(`cls:${cls.name}`) ? cls.tests : []"
              :key="test.displayName"
              :label="shortTestName(test.displayName)"
              :status="test.status"
              :duration-ms="test.durationMs"
              :queue-duration-ms="test.queueDurationMs"
              :flaky-info="flakyFor(test)"
              :indent="1"
              :clickable="true"
              :is-selected="store.selectedTest?.displayName === test.displayName"
              :is-focused="focusedKey === `test:${test.displayName}`"
              :data-focused="focusedKey === `test:${test.displayName}` || undefined"
              @click="selectTest(test); focusedKey = `test:${test.displayName}`"
            />
          </template>
        </div>

        <!-- Timeline view -->
        <div v-else>
          <TestTreeItem
            v-for="test in timelineTests"
            :key="test.displayName"
            :label="shortTestName(test.displayName)"
            :subtitle="test.className"
            :status="test.status"
            :duration-ms="test.durationMs"
            :queue-duration-ms="test.queueDurationMs"
            :flaky-info="flakyFor(test)"
            :indent="0"
            :clickable="true"
            :is-selected="store.selectedTest?.displayName === test.displayName"
            :is-focused="focusedKey === `test:${test.displayName}`"
            :data-focused="focusedKey === `test:${test.displayName}` || undefined"
            @click="selectTest(test); focusedKey = `test:${test.displayName}`"
          />
        </div>
      </template>

      <!-- Filtered-out empty state -->
      <div v-if="store.collections.value.length > 0 && filteredCollections.length === 0"
           class="flex flex-col items-center justify-center py-8 px-4 text-base-content/40">
        <span class="text-sm">No tests match the current filter</span>
      </div>

      <!-- Empty state -->
      <EmptyState v-if="store.collections.value.length === 0 && store.state.setupPhases.length === 0"
                  label="Waiting for tests..." class="py-16 px-4" />
    </div>

  </div>
</template>

