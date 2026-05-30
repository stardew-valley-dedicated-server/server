<script setup lang="ts">
import { ref, computed } from 'vue'
import { Icon } from '@iconify/vue'
import { formatDuration } from '../utils/format'
import { useTestUI, useShowFailed } from '../composables/useTestUI'

const { store } = useTestUI()
const { showFailedTests } = useShowFailed()

// Read counts directly from the store's incrementally-maintained map
const sc = store.statusCounts

const completedCount = computed(() =>
  sc.passed + sc.failed + sc.skipped + sc.canceled + sc.notDispatched + sc.aborted
)

const progress = computed(() => {
  const total = store.state.totalTests
  if (total === 0) return 0
  return Math.round((completedCount.value / total) * 100)
})

// Final elapsed (only set when run finishes; during the run, rAF updates the DOM directly)
const elapsed = computed(() => formatDuration(store.elapsedMs))

const statusLabel = computed(() => {
  if (store.state.status === 'aborted') return 'aborted'
  if (store.state.status === 'running') return 'running'
  if (store.state.status === 'finished') {
    if (sc.failed > 0) return 'failed'
    if (sc.canceled > 0) return 'canceled'
    if (sc.passed > 0) return 'passed'
    return 'no tests ran'
  }
  return 'pending'
})

const statusTextClass = computed(() => {
  if (store.state.status === 'running') return 'text-success'
  if (store.state.status === 'finished') {
    if (sc.failed > 0) return 'text-error'
    if (sc.canceled > 0) return 'text-warning'
    if (sc.passed > 0) return 'text-success'
    return 'text-warning'
  }
  if (store.state.status === 'aborted') return 'text-warning'
  return 'text-base-content/50'
})

// Stop = nuke. Single click force-exits the runner via Program.cs's
// ForceExitNow path (bulk Docker cleanup by run-id label + Environment.Exit).
// The runner process dies in ~1-3 s, the WebSocket disconnects, and the
// frontend's onReconnectFailed flips state.status to 'aborted'.
// Disable the button after the click so it doesn't look unresponsive while
// the runner shuts down.
const stopClicked = ref(false)

function onStopClicked() {
  stopClicked.value = true
  void store.sendCommand('stop')
}

const shortcutsOpen = ref(false)
const detailsOpen = ref(false)

const runMeta = computed(() => store.state.runMetadata?.data ?? null)
const gitLabel = computed(() => {
  const g = runMeta.value?.git
  if (!g) return null
  const sha = g.sha ? g.sha.slice(0, 7) : '?'
  return `${g.branch ?? '?'} @ ${sha}${g.dirty ? ' ●' : ''}`
})
</script>

<template>
  <header class="flex items-center gap-4 px-6 h-12 bg-base-200 border-b border-base-content/5 flex-none">
    <!-- Terminal icon + title -->
    <div class="flex items-center gap-2 flex-none">
      <Icon icon="lucide:terminal" class="w-4 h-4 text-base-content" />
      <span class="font-semibold text-sm tracking-tight text-base-content">Test Runner:</span>
    </div>

    <!-- Run status (inline colored text) -->
    <span v-if="store.state.status !== 'pending'"
          class="text-sm font-semibold flex-none"
          :class="statusTextClass"
          :title="store.state.status === 'aborted'
            ? `Run aborted: ${store.state.abortReason ?? 'connection lost'}`
            : `Test run status: ${statusLabel}`">
      {{ statusLabel }}
    </span>

    <!-- Counters -->
    <div class="flex items-center gap-2.5 text-xs tabular-nums flex-none">
      <span class="text-success font-medium">{{ sc.passed }} passed</span>
      <span v-if="sc.failed > 0"
            class="text-error font-medium cursor-pointer hover:underline"
            title="Show failed tests only"
            @click="showFailedTests()">{{ sc.failed }} failed</span>
      <span v-if="sc.canceled > 0" class="text-warning font-medium">{{ sc.canceled }} canceled</span>
      <span v-if="sc.notDispatched > 0" class="text-base-content/50 font-medium">{{ sc.notDispatched }} not dispatched</span>
      <span v-if="sc.queued > 0" class="text-primary font-medium">{{ sc.queued }} queued</span>
      <span v-if="sc.skipped > 0" class="text-base-content/50 font-medium">{{ sc.skipped }} skipped</span>
    </div>

    <!-- Progress bar (inline) -->
    <div class="flex-1 max-w-sm flex items-center gap-2">
      <div class="flex-1 bg-base-content/20 rounded-full h-1.5 overflow-visible">
        <div
          class="h-full rounded-full transition-[width] duration-300 ease-out"
          :class="[
            sc.failed > 0 ? 'bg-error' : sc.canceled > 0 ? 'bg-warning' : 'bg-primary',
            store.state.status === 'running' ? 'progress-glow' : ''
          ]"
          :style="{ width: `${progress}%` }"
        />
      </div>
      <span class="text-[11px] text-base-content/50 tabular-nums flex-none"
            :title="`${completedCount} of ${store.state.totalTests} tests completed (${progress}%)`">
        {{ completedCount }}/{{ store.state.totalTests }}
      </span>
    </div>

    <!-- Stop button. Hidden in static report mode (no backend); during a
         transient WS hiccup the click still works because sendCommand falls
         back to POST /api/command. Click force-exits the runner — in-flight
         test recordings/screenshots are lost, but stop completes in a few
         seconds (vs 30+ s of graceful drain). -->
    <div v-if="!store.isReportMode && store.state.status === 'running'"
         class="flex items-center gap-1 flex-none">
      <button class="btn btn-error btn-xs gap-1"
              :disabled="stopClicked"
              title="Stop the run (force-exit; in-flight test artifacts are lost)"
              @click="onStopClicked">
        <Icon icon="lucide:square" class="w-3.5 h-3.5" />
        <span>{{ stopClicked ? 'Stopping…' : 'Stop' }}</span>
      </button>
    </div>

    <div class="flex-1" />

    <!-- Duration: rAF updates this element directly while running; Vue renders final value when stopped -->
    <span class="flex items-center gap-1 flex-none">
      <Icon icon="lucide:clock" class="w-3.5 h-3.5 text-base-content/30" />
      <span :ref="store.elapsedTimerRef"
            class="text-xs text-base-content/50 tabular-nums"
            title="Elapsed time">{{ elapsed || '--' }}</span>
    </span>

    <!-- Run details drawer (git, env, runtime, server-config plan) -->
    <div v-if="runMeta" class="relative flex-none">
      <button class="flex items-center gap-1.5 px-2 h-6 rounded-md text-xs text-base-content/70 hover:text-base-content hover:bg-base-content/5 transition-colors tabular-nums"
              title="Run details"
              @click.stop="detailsOpen = !detailsOpen"
              @blur="detailsOpen = false">
        <Icon icon="lucide:git-branch" class="w-3 h-3" />
        <span v-if="gitLabel">{{ gitLabel }}</span>
        <Icon icon="lucide:chevron-down" class="w-3 h-3" />
      </button>
      <div v-if="detailsOpen"
           class="absolute right-0 top-full mt-1.5 z-40 bg-base-300 border border-base-content/10 rounded-lg shadow-lg p-3 text-xs space-y-2 w-80 max-h-96 overflow-auto"
           @mousedown.stop>
        <div>
          <div class="text-base-content/50 uppercase text-[10px] tracking-wider mb-1">Run</div>
          <div class="font-mono break-all">{{ runMeta.runId }}</div>
        </div>
        <div v-if="runMeta.git">
          <div class="text-base-content/50 uppercase text-[10px] tracking-wider mb-1">Git</div>
          <div>branch: <span class="font-mono">{{ runMeta.git.branch ?? '?' }}</span></div>
          <div>sha: <span class="font-mono">{{ runMeta.git.sha ?? '?' }}</span></div>
          <div v-if="runMeta.git.dirty" class="text-warning">dirty working tree</div>
        </div>
        <div v-if="runMeta.runtime">
          <div class="text-base-content/50 uppercase text-[10px] tracking-wider mb-1">Runtime</div>
          <div v-if="runMeta.runtime.os">{{ runMeta.runtime.os }}</div>
          <div v-if="runMeta.runtime.dotnet">{{ runMeta.runtime.dotnet }}</div>
          <div v-if="runMeta.runtime.docker">docker {{ runMeta.runtime.docker }}</div>
        </div>
        <div v-if="runMeta.env && Object.keys(runMeta.env).length > 0">
          <div class="text-base-content/50 uppercase text-[10px] tracking-wider mb-1">Env</div>
          <div v-for="(v, k) in runMeta.env" :key="k" class="font-mono">{{ k }}={{ v }}</div>
        </div>
        <div v-if="runMeta.serverConfigs && runMeta.serverConfigs.length > 0">
          <div class="text-base-content/50 uppercase text-[10px] tracking-wider mb-1">Server config plan</div>
          <div v-for="cfg in runMeta.serverConfigs" :key="cfg.key" class="flex justify-between gap-2">
            <span class="truncate">{{ cfg.label }}</span>
            <span class="tabular-nums text-base-content/60 flex-none">{{ cfg.testCount }} tests · {{ cfg.prestartedInstanceCount }} prestarted</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Keyboard shortcuts hint (hover + click toggle) -->
    <div class="relative group flex-none">
      <button class="w-6 h-6 flex items-center justify-center rounded-full text-base-content/30 hover:text-base-content/60 hover:bg-base-content/5 transition-colors text-xs font-bold"
              title="Keyboard shortcuts"
              @click.stop="shortcutsOpen = !shortcutsOpen"
              @blur="shortcutsOpen = false">?</button>
      <div class="absolute right-0 top-full mt-1.5 z-40"
           :class="shortcutsOpen ? 'block' : 'hidden group-hover:block'">
        <div class="bg-base-300 border border-base-content/10 rounded-lg shadow-lg px-3 py-2 whitespace-nowrap space-y-1">
          <div class="flex items-center gap-1.5 text-base-content/50">
            <kbd class="kbd kbd-xs">↑</kbd><kbd class="kbd kbd-xs">↓</kbd>
            <span class="text-[10px]">Navigate</span>
          </div>
          <div class="flex items-center gap-1.5 text-base-content/50">
            <kbd class="kbd kbd-xs">←</kbd><kbd class="kbd kbd-xs">→</kbd>
            <span class="text-[10px]">Fold / Expand</span>
          </div>
          <div class="flex items-center gap-1.5 text-base-content/50">
            <kbd class="kbd kbd-xs text-[9px]">Esc</kbd>
            <span class="text-[10px]">Go to parent</span>
          </div>
          <div class="flex items-center gap-1.5 text-base-content/50">
            <kbd class="kbd kbd-xs">f</kbd>
            <span class="text-[10px]">Filter tests</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Theme toggle -->
    <label class="swap swap-rotate btn btn-ghost btn-xs btn-circle flex-none" title="Toggle light/dark theme">
      <input type="checkbox" class="theme-controller" value="sdvd-light" />
      <Icon icon="lucide:sun" class="swap-on w-4 h-4" />
      <Icon icon="lucide:moon" class="swap-off w-4 h-4" />
    </label>
  </header>
</template>
