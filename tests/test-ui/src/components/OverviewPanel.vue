<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, inject, onBeforeUnmount, type Ref } from "vue";
// Imported (not a public/ URL) so Vite hashes it and verifies it at build time —
// resolves under the report's deploy subpath via the relative base. Same pattern
// as EmptyState.vue.
import logoUrl from "../assets/img/logo.svg";
import { useRunStatus } from "../composables/useRunStatus";
import { type ActiveView, useGoToExplorer, useShowFailed, useTestUI } from "../composables/useTestUI";
import type { TestSnapshot } from "../types/state";
import { formatDateTime, formatDuration, shortTestName } from "../utils/format";
import { TERM_HELP } from "../utils/glossary";
import EmptyState from "./EmptyState.vue";
import StatusIcon from "./StatusIcon.vue";

const { store, activeView } = useTestUI();
const { showFailedTests } = useShowFailed();
const { goToExplorer } = useGoToExplorer();
const { statusLabel, statusTextClass } = useRunStatus(store);

const sc = store.statusCounts;

// Lets a nav card light up its matching left-sidebar icon on hover (provided by
// App.vue). Optional inject — harmless no-op if the panel is ever mounted alone.
const hoveredNav = inject<Ref<ActiveView | null>>("hoveredNav");
function setHover(view: ActiveView | null) {
    if (hoveredNav) {
        hoveredNav.value = view;
    }
}
// Clear on unmount in case a click navigates away before mouseleave fires, which
// would otherwise leave a sidebar icon stuck in the hovered color.
onBeforeUnmount(() => setHover(null));

// Render gates: live progress (pending/running) vs final result, and whether
// there's any run to summarize at all (else the help block carries the page).
const isLive = computed(() => store.state.status === "pending" || store.state.status === "running");
const hasData = computed(() => store.state.totalTests > 0 || store.collections.value.length > 0);

const completedCount = computed(() => sc.passed + sc.failed + sc.skipped + sc.canceled + sc.notDispatched + sc.aborted);
const progress = computed(() => {
    const total = store.state.totalTests;
    return total === 0 ? 0 : Math.round((completedCount.value / total) * 100);
});

// The count chips shown in the summary, in display order. Slug drives the
// TERM_HELP tooltip and the color class; only nonzero counts (plus passed) render.
const COUNT_CHIPS: { key: string; label: string; cls: string }[] = [
    { key: "passed", label: "passed", cls: "text-success" },
    { key: "failed", label: "failed", cls: "text-error" },
    { key: "canceled", label: "canceled", cls: "text-warning" },
    { key: "aborted", label: "aborted", cls: "text-secondary" },
    { key: "notDispatched", label: "not dispatched", cls: "text-base-content/50" },
    { key: "running", label: "running", cls: "text-info" },
    { key: "queued", label: "queued", cls: "text-primary" },
    { key: "skipped", label: "skipped", cls: "text-base-content/50" },
];
const visibleChips = computed(() => COUNT_CHIPS.filter((c) => c.key === "passed" || (sc[c.key] ?? 0) > 0));

// Recent failures — walk the tree the same way findInitialSelection does. Most
// recently-executed first so the freshest failure is at the top; capped so the
// list stays scannable.
const recentFailures = computed<TestSnapshot[]>(() => {
    const failed: TestSnapshot[] = [];
    for (const col of store.collections.value) {
        for (const cls of col.classes) {
            for (const test of cls.tests) {
                if (test.status === "failed") {
                    failed.push(test);
                }
            }
        }
    }
    failed.sort((a, b) => (b.executionOrder ?? -1) - (a.executionOrder ?? -1));
    return failed.slice(0, 8);
});

const mostRecentFailure = computed<TestSnapshot | null>(() => recentFailures.value[0] ?? null);

const flakyTests = computed(() => store.state.flakyTests ?? []);

const runMeta = computed(() => store.state.runMetadata?.data ?? null);

// Run facts a tester cares about: what was tested, when, and how long.
const gitBranch = computed(() => runMeta.value?.git?.branch ?? null);
const gitSha = computed(() => {
    const sha = runMeta.value?.git?.sha;
    return sha ? sha.slice(0, 7) : null;
});
// Link the sha to its GitHub commit (full sha for an unambiguous URL). Same repo
// as the header subtitle link.
const commitUrl = computed(() => {
    const sha = runMeta.value?.git?.sha;
    return sha ? `https://github.com/stardew-valley-dedicated-server/server/commit/${sha}` : null;
});
const gitDirty = computed(() => runMeta.value?.git?.dirty === true);
const startedAt = computed(() => formatDateTime(store.state.runStartTime));
const totalDuration = computed(() => formatDuration(store.state.durationMs));

// Deeper run metadata for the collapsible "Run details" section: runtime, the env
// knobs that shaped the run, and the per-config server plan.
const runtimeLabel = computed(() => {
    const r = runMeta.value?.runtime;
    if (!r) {
        return null;
    }
    return [r.os, r.dotnet, r.docker ? `docker ${r.docker}` : null].filter(Boolean).join(" · ");
});
const envEntries = computed(() => Object.entries(runMeta.value?.env ?? {}));
const serverConfigs = computed(() => runMeta.value?.serverConfigs ?? []);

// Selecting a failure mirrors App.vue's navigateToTest: select the test and
// switch to the Explorer view (the route watcher reflects it into the URL).
function selectTest(test: TestSnapshot) {
    store.selectTest(test);
    activeView.value = "tests";
}

// Flaky entries carry only a display name; resolve to a tree node so the click
// can select it. Omit the link when the test isn't in the current tree.
function selectFlaky(displayName: string) {
    const t = store.findTest(displayName);
    if (t) {
        selectTest(t);
    }
}
</script>

<template>
  <div class="h-full overflow-auto panel-scroll">
    <div class="max-w-3xl mx-auto px-8 py-8 space-y-8">
      <!-- ── Header: brand logo + title ── -->
      <div class="flex items-center gap-3">
        <img :src="logoUrl" alt="" class="w-8 h-8 flex-none" />
        <div>
          <h1 class="text-xl font-semibold tracking-tight">E2E Test Results</h1>
          <p class="text-sm text-base-content/50">
            Automated end-to-end tests for
            <a href="https://github.com/stardew-valley-dedicated-server/server"
               target="_blank" rel="noopener"
               class="text-base-content/70 hover:text-primary underline underline-offset-2 transition-colors">JunimoServer</a>, a Stardew Valley dedicated server.
          </p>
        </div>
      </div>

      <!-- ── Summary block (only when there's a run to summarize) ── -->
      <section v-if="hasData" class="space-y-4">
        <!-- Headline result + counts. The full count breakdown (incl. total) lives
             here so passed/skipped/total read together; the header strip and the
             facts list below carry the orthogonal data (status word, timing). -->
        <div class="bg-base-200/50 border border-base-content/5 rounded-lg p-5 space-y-4">
          <div class="text-lg font-bold capitalize" :class="statusTextClass"
               :title="`Overall run status: ${statusLabel}`">
            {{ isLive ? "In progress" : statusLabel }}
          </div>

          <!-- Live progress bar -->
          <div v-if="isLive" class="flex items-center gap-2">
            <div class="flex-1 bg-base-content/20 rounded-full h-1.5">
              <div class="h-full rounded-full bg-primary transition-[width] duration-300"
                   :style="{ width: `${progress}%` }" />
            </div>
            <span class="text-[11px] text-base-content/50 tabular-nums">{{ completedCount }}/{{ store.state.totalTests }}</span>
          </div>

          <!-- Count chips + total, dot-separated so adjacent counts never glue. -->
          <div class="flex items-center gap-x-2.5 gap-y-1 flex-wrap text-sm tabular-nums">
            <template v-for="(chip, i) in visibleChips" :key="chip.key">
              <span v-if="i > 0" class="text-base-content/20">·</span>
              <span class="font-medium" :class="chip.cls" :title="TERM_HELP[chip.key]">
                {{ sc[chip.key] ?? 0 }} {{ chip.label }}
              </span>
            </template>
            <span v-if="store.state.totalTests > 0" class="text-base-content/20">·</span>
            <span v-if="store.state.totalTests > 0" class="text-base-content/50">{{ store.state.totalTests }} total</span>
          </div>
        </div>

        <!-- Recent failures -->
        <div v-if="recentFailures.length > 0" class="space-y-2">
          <div class="flex items-center justify-between">
            <h2 class="text-xs uppercase tracking-widest text-base-content/40 font-semibold">Recent failures</h2>
            <button class="text-xs text-error hover:underline cursor-pointer" @click="showFailedTests()">View all {{ sc.failed }} →</button>
          </div>
          <button v-for="test in recentFailures" :key="test.displayName"
                  class="w-full cursor-pointer flex items-center gap-2 px-3 py-2 rounded-md bg-error/5 hover:bg-error/10 text-left transition-colors"
                  @click="selectTest(test)">
            <StatusIcon status="failed" :size="14" />
            <span class="font-mono text-[13px] truncate flex-1">{{ shortTestName(test.displayName) }}</span>
            <span v-if="test.failureCategory" class="text-[11px] text-base-content/40 flex-none">{{ test.failureCategory }}</span>
          </button>
        </div>

        <!-- Flaky tests (needs-attention list) -->
        <div v-if="flakyTests.length > 0" class="space-y-2">
          <h2 class="text-xs uppercase tracking-widest text-base-content/40 font-semibold">Flaky tests</h2>
          <button v-for="f in flakyTests" :key="f.test"
                  class="w-full cursor-pointer flex items-center gap-2 px-3 py-2 rounded-md bg-warning/5 hover:bg-warning/10 text-left transition-colors"
                  @click="selectFlaky(f.test)">
            <Icon icon="lucide:zap" class="w-3.5 h-3.5 text-warning flex-none" />
            <span class="font-mono text-[13px] truncate flex-1">{{ shortTestName(f.test) }}</span>
            <span class="text-[11px] text-base-content/40 flex-none tabular-nums">
              {{ Math.round(f.failRate * 100) }}% over {{ f.recentRuns }} runs
            </span>
          </button>
        </div>

        <!-- Run facts: the metadata the header strip and verdict card don't carry —
             what was tested, when, and (the single Overview copy of) how long. -->
        <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-xs pt-1">
          <template v-if="gitBranch">
            <dt class="text-base-content/40 flex items-center gap-1.5">
              <Icon icon="lucide:git-branch" class="w-3.5 h-3.5" /> Branch
            </dt>
            <dd class="font-mono text-base-content/70">
              {{ gitBranch }}<span v-if="gitSha" class="text-base-content/40"> @ <a
                 :href="commitUrl!" target="_blank" rel="noopener"
                 class="hover:text-primary underline underline-offset-2 transition-colors"
                 title="View this commit on GitHub">{{ gitSha }}</a></span>
              <span v-if="gitDirty" class="text-warning" title="The working tree had uncommitted changes when the run started."> · uncommitted changes</span>
            </dd>
          </template>
          <template v-if="startedAt">
            <dt class="text-base-content/40 flex items-center gap-1.5">
              <Icon icon="lucide:calendar" class="w-3.5 h-3.5" /> Started
            </dt>
            <dd class="text-base-content/70 tabular-nums">{{ startedAt }}</dd>
          </template>
          <template v-if="!isLive && totalDuration">
            <dt class="text-base-content/40 flex items-center gap-1.5">
              <Icon icon="lucide:timer" class="w-3.5 h-3.5" /> Duration
            </dt>
            <dd class="text-base-content/70 tabular-nums">{{ totalDuration }}</dd>
          </template>
        </dl>

        <!-- Run details: deeper metadata, collapsed by default so it stays out of
             the way of the at-a-glance summary above. -->
        <details v-if="runMeta" class="group text-xs">
          <summary class="cursor-pointer text-base-content/40 hover:text-base-content/70 select-none flex items-center gap-1.5 w-fit">
            <Icon icon="lucide:chevron-right" class="w-3.5 h-3.5 transition-transform group-open:rotate-90" />
            Run details
          </summary>
          <div class="mt-3 pl-5 space-y-3">
            <div>
              <div class="text-base-content/40 uppercase text-[10px] tracking-widest font-semibold mb-1">Run ID</div>
              <div class="font-mono break-all text-base-content/70">{{ runMeta.runId }}</div>
            </div>
            <div v-if="runtimeLabel">
              <div class="text-base-content/40 uppercase text-[10px] tracking-widest font-semibold mb-1">Runtime</div>
              <div class="text-base-content/70">{{ runtimeLabel }}</div>
            </div>
            <div v-if="envEntries.length > 0">
              <div class="text-base-content/40 uppercase text-[10px] tracking-widest font-semibold mb-1">Environment</div>
              <div v-for="[k, v] in envEntries" :key="k" class="font-mono text-base-content/70">{{ k }}={{ v }}</div>
            </div>
            <div v-if="serverConfigs.length > 0">
              <div class="text-base-content/40 uppercase text-[10px] tracking-widest font-semibold mb-1">Server configurations</div>
              <div v-for="cfg in serverConfigs" :key="cfg.key" class="flex justify-between gap-2 text-base-content/70">
                <span class="truncate">{{ cfg.label }}</span>
                <span class="tabular-nums text-base-content/50 flex-none">{{ cfg.testCount }} {{ cfg.testCount === 1 ? "test" : "tests" }}</span>
              </div>
            </div>
          </div>
        </details>
      </section>

      <!-- ── Help / orientation block (always visible) ── -->
      <!-- mt-6! overrides the wrapper's space-y-8 so the divider has balanced
           breathing room (24px above via margin, 24px below via padding) instead
           of the 32px+24px the global gap would stack on top of the border. -->
      <section class="space-y-4 border-t border-base-content/5 mt-6! pt-6">
        <h2 class="text-xs uppercase tracking-widest text-base-content/40 font-semibold">Getting around</h2>
        <!-- Navigation cards: bordered + chevron so they read as clickable, not prose. -->
        <div class="grid sm:grid-cols-2 gap-3 text-sm">
          <button class="group cursor-pointer flex items-start gap-2.5 text-left rounded-lg border border-base-content/10 bg-base-200/30 p-3 hover:bg-base-200/70 hover:border-base-content/20 transition-colors"
                  @click="goToExplorer()"
                  @mouseenter="setHover('tests')" @mouseleave="setHover(null)">
            <Icon icon="lucide:file-text" class="w-4 h-4 mt-0.5 flex-none text-base-content/40 group-hover:text-base-content/80 transition-colors" />
            <p class="text-base-content/60 flex-1">
              <span class="text-base-content/90 font-medium">Explorer</span> — browse the full test tree, search by
              name, and filter by status. Each test opens its output, error, and screen recording.
            </p>
            <Icon icon="lucide:chevron-right" class="w-4 h-4 mt-0.5 flex-none text-base-content/20 group-hover:text-base-content/50 transition-colors" />
          </button>
          <button class="group cursor-pointer flex items-start gap-2.5 text-left rounded-lg border border-base-content/10 bg-base-200/30 p-3 hover:bg-base-200/70 hover:border-base-content/20 transition-colors"
                  @click="activeView = 'vnc'"
                  @mouseenter="setHover('vnc')" @mouseleave="setHover(null)">
            <Icon icon="lucide:container" class="w-4 h-4 mt-0.5 flex-none text-base-content/40 group-hover:text-base-content/80 transition-colors" />
            <p class="text-base-content/60 flex-1">
              <span class="text-base-content/90 font-medium">Containers</span> — server and client containers for the
              run: live screens while it runs, plus past containers, performance charts, and the infrastructure timeline.
            </p>
            <Icon icon="lucide:chevron-right" class="w-4 h-4 mt-0.5 flex-none text-base-content/20 group-hover:text-base-content/50 transition-colors" />
          </button>
        </div>
        <!-- Informational hint (not navigation) — kept visually distinct from the cards above. -->
        <p class="flex items-center gap-1.5 text-xs text-base-content/40">
          <Icon icon="lucide:keyboard" class="w-3.5 h-3.5 flex-none" />
          Hover the <span class="font-bold text-base-content/60">?</span> in the top bar to see keyboard shortcuts.
        </p>

        <!-- Data-driven shortcuts: only render what the current run can point at. -->
        <div v-if="sc.failed > 0" class="flex flex-wrap gap-2 pt-1">
          <button class="btn btn-sm btn-outline btn-error gap-1.5"
                  @click="showFailedTests()">
            <Icon icon="lucide:list-filter" class="w-3.5 h-3.5" />
            Jump to {{ sc.failed }} failed {{ sc.failed === 1 ? "test" : "tests" }}
          </button>
          <button v-if="mostRecentFailure"
                  class="btn btn-sm btn-ghost gap-1.5"
                  @click="selectTest(mostRecentFailure)">
            <Icon icon="lucide:arrow-right" class="w-3.5 h-3.5" />
            Most recent failure
          </button>
        </div>
      </section>

      <!-- No-data hint: when there's genuinely nothing to summarize yet. -->
      <EmptyState v-if="!hasData" label="Waiting for test results…" class="py-8" />
    </div>
  </div>
</template>
