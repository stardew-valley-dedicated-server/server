<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch } from 'vue'
import { Icon } from '@iconify/vue'
import type { InstanceSnapshot, SetupStepSnapshot } from '../types/state'
import { tpsTextClass as _tpsText, fpsTextClass as _fpsText, cpuTextClass as _cpuText, memTextClass as _memText, displayMem as _displayMem, formatMem, queueTextClass as _queueText } from '../utils/metrics'
import { instanceStatusDotClass, instanceStatusLabel, instanceStatusBadgeClass } from '../utils/instance-status'
import { acquireInstanceSlot, releaseInstanceSlot } from '../composables/useIframeLayer'

const props = withDefaults(defineProps<{
  instance: InstanceSnapshot
  spinnerColor: string
  expanded?: boolean
  connectionCount?: number
  connectedServerLabel?: string | null
  stats?: { cpuPercent: number; memoryMb: number; cpuCount: number; totalMemoryMb: number; fps: number | null; tps: number | null; avgTickMs: number | null; gameMemoryMb: number | null; targetTps: number | null; targetFps: number | null; gcRate: number | null; pendingActions: number | null; gameThreadWaitMs: number | null; netRxBytesPerSec: number | null; netTxBytesPerSec: number | null; blkReadBytesPerSec: number | null; blkWriteBytesPerSec: number | null; memoryLimitMb: number }
  setupSteps?: SetupStepSnapshot[]
  setupStatus?: 'pending' | 'running' | 'completed' | 'failed' | null
  instanceCount?: number
  /** Run ended; status is shown as stopped, external-link hidden. */
  stopped?: boolean
  /** Container disposed mid-run; treated as stopped for status display. */
  retained?: boolean
  /** Show the enlarge button. The enlarged layout is owned by the parent. */
  showExpand?: boolean
}>(), {
  expanded: false,
  connectionCount: undefined,
  connectedServerLabel: undefined,
  stats: undefined,
  setupSteps: undefined,
  setupStatus: undefined,
  instanceCount: 1,
  stopped: false,
  retained: false,
  showExpand: true
})

const emit = defineEmits<{
  toggleExpand: []
  inspect: []
}>()

function tpsTextClass(): string { return _tpsText(props.stats) }
function fpsTextClass(): string { return _fpsText(props.stats) }
function cpuTextClass(): string { return _cpuText(props.stats, props.instanceCount!) }
function memTextClass(): string { return _memText(props.stats, props.instanceCount!) }
function queueTextClass(): string { return _queueText(props.stats) }
function displayMem(): number | null { return _displayMem(props.stats) }

function statusDotClass(): string {
  return instanceStatusDotClass(props.instance.status, props.instance.connected, props.stopped || props.retained, 'sm')
}

function statusLabel(): string {
  return instanceStatusLabel(props.instance.status, props.instance.connected, props.stopped || props.retained, props.retained)
}

function statusBadgeClass(): string {
  return instanceStatusBadgeClass(props.instance.status)
}

function latestSetupStep(): string | null {
  const steps = props.setupSteps
  if (!steps || steps.length === 0) return null
  const latest = steps[steps.length - 1]
  return latest.details || latest.step
}

// ── Iframe pool wiring ──
// Acquire on mount, publish placeholder, release on unmount. The pool's
// iframe (rendered by <VncIframePool>) tracks this placeholder's rect.
// Untrack on unmount clears the stale placeholder so the iframe hides
// until another tile takes over (the wrapper itself stays alive — the
// pool keeps it pinned for the session).
const placeholderRef = ref<HTMLDivElement | null>(null)
let activeSlot: ReturnType<typeof acquireInstanceSlot> | null = null

function publishPlaceholder() {
  if (!placeholderRef.value) return
  if (!activeSlot) activeSlot = acquireInstanceSlot(props.instance.instanceId)
  activeSlot.track(placeholderRef.value)
}

onMounted(() => {
  if (props.instance.vncUrl) publishPlaceholder()
})

watch(() => props.instance.vncUrl, (url) => {
  if (url && !activeSlot) publishPlaceholder()
})

watch(placeholderRef, (el) => {
  if (el && props.instance.vncUrl && !activeSlot) publishPlaceholder()
})

onBeforeUnmount(() => {
  if (activeSlot) {
    activeSlot.untrack()
    releaseInstanceSlot(props.instance.instanceId)
    activeSlot = null
  }
})
</script>

<template>
  <div class="rounded-md overflow-hidden bg-base-200/30 flex flex-col w-full h-full">
    <!-- Header -->
    <div class="bg-base-300 flex-none">
      <!-- Row 1: identity + actions -->
      <div class="flex items-center justify-between px-3 py-1">
        <div class="flex items-center gap-1.5 min-w-0">
          <span :class="statusDotClass()" :title="statusLabel()" aria-hidden="true" />
          <span class="text-[11px] font-medium text-base-content/60 truncate" :title="instance.label">{{ instance.label }}</span>
          <!-- Host badge: which Docker daemon the container runs on (`local`,
               `vps-1`, etc.). Always present in any run that goes through the
               broker's HostPool.Place path. -->
          <span v-if="instance.hostId"
                class="badge badge-xs flex-none bg-info/15 text-info"
                :title="`Host: ${instance.hostId}`">{{ instance.hostId }}</span>
          <span class="badge badge-xs flex-none" :class="statusBadgeClass()" :title="`Instance status: ${statusLabel()}`">{{ statusLabel() }}</span>
        </div>
        <div class="flex items-center gap-0.5 flex-none">
          <button class="btn btn-ghost btn-xs px-1 text-base-content/30 hover:text-base-content/70"
                  title="Inspect (charts, setup steps)"
                  @click.stop="emit('inspect')">
            <Icon icon="lucide:bar-chart-2" class="w-3.5 h-3.5" />
          </button>
          <button v-if="showExpand && !expanded"
                  class="btn btn-ghost btn-xs px-1 text-base-content/30 hover:text-base-content/70"
                  title="Enlarge"
                  @click.stop="emit('toggleExpand')">
            <Icon icon="lucide:maximize-2" class="w-3.5 h-3.5" />
          </button>
          <a v-if="instance.vncUrl && !stopped && !retained" :href="instance.vncUrl" target="_blank" rel="noopener"
             class="btn btn-ghost btn-xs gap-1 text-[10px] text-base-content/40 hover:text-base-content/70 flex-none"
             title="Open VNC in new tab">
            <Icon icon="lucide:external-link" class="w-3 h-3" />
          </a>
        </div>
      </div>
      <!-- Row 2: stats + connection info -->
      <div class="flex items-center gap-x-3 px-3 pb-1 text-[10px] tabular-nums overflow-hidden h-4 flex-none">
        <template v-if="stats">
          <span class="flex-none" :class="tpsTextClass()"
                :title="stats.tps != null ? `TPS: ${stats.tps.toFixed(1)} (avg tick: ${stats.avgTickMs?.toFixed(1) ?? '-'}ms)` : 'TPS: not available'">
            <span class="text-base-content/25">TPS:</span> {{ stats.tps != null ? Math.round(stats.tps) : '-' }}
          </span>
          <span class="flex-none" :class="fpsTextClass()"
                :title="stats.fps != null ? `FPS: ${stats.fps.toFixed(1)}` : 'FPS: not available'">
            <span class="text-base-content/25">FPS:</span> {{ stats.fps != null ? Math.round(stats.fps) : '-' }}
          </span>
          <span class="flex-none" :class="cpuTextClass()"
                :title="stats ? `CPU: ${stats.cpuPercent.toFixed(1)}%` : 'CPU: not available'">
            <span class="text-base-content/25">CPU:</span> {{ stats.cpuPercent.toFixed(0) }}%
          </span>
          <span class="flex-none" :class="memTextClass()"
                :title="displayMem() != null ? `Memory: ${displayMem()! >= 1024 ? (displayMem()! / 1024).toFixed(1) + ' GB' : Math.round(displayMem()!) + ' MB'}` : 'Memory: not available'">
            <span class="text-base-content/25">MEM:</span> {{ displayMem() != null ? formatMem(displayMem()!) : '-' }}
          </span>
          <span v-if="stats.pendingActions != null" class="flex-none" :class="queueTextClass()"
                :title="`Game thread queue: ${stats.pendingActions} pending (avg wait: ${stats.gameThreadWaitMs?.toFixed(1) ?? '-'}ms)`">
            <span class="text-base-content/25">Q:</span> {{ stats.pendingActions }}
          </span>
        </template>
        <span v-if="connectionCount != null && connectionCount > 0"
              class="badge badge-xs badge-info gap-0.5 flex-none" :title="`${connectionCount} connected client(s)`">
          <Icon icon="lucide:users" class="w-2.5 h-2.5" />
          {{ connectionCount }}
        </span>
        <span v-if="connectedServerLabel"
              class="badge badge-xs badge-ghost gap-0.5 min-w-0 truncate" :title="`Connected to ${connectedServerLabel}`">
          <Icon icon="lucide:server" class="w-2.5 h-2.5 flex-none" />
          {{ connectedServerLabel }}
        </span>
      </div>
    </div>
    <!-- Running test / poison reason / setup status -->
    <div class="flex items-center gap-1.5 px-3 py-0.5 bg-base-200/20 border-t border-base-content/5 h-5 flex-none">
      <template v-if="instance.status === 'poisoned' && instance.poisonReason">
        <span class="text-[10px] text-error truncate" :title="instance.poisonReason">{{ instance.poisonReason }}</span>
      </template>
      <template v-else-if="instance.currentTest">
        <template v-if="!stopped">
          <span class="loading loading-spinner loading-xs" :class="spinnerColor" />
          <span class="text-[10px] text-base-content/50 truncate" :title="instance.currentTest">{{ instance.currentTest }}</span>
        </template>
      </template>
      <template v-else-if="!stopped && (setupStatus === 'running' || instance.status === 'starting')">
        <span class="loading loading-spinner loading-xs text-info" />
        <span class="text-[10px] text-base-content/40 truncate"
              :title="latestSetupStep() ?? undefined">Waiting for {{ instance.instanceType }} ready</span>
      </template>
    </div>
    <!-- Setup progress bar -->
    <div v-if="!stopped && setupStatus === 'running'" class="h-0.5 bg-info/30 flex-none overflow-hidden">
      <div class="h-full bg-info animate-pulse gpu-accel w-full" />
    </div>
    <!-- VNC iframe placeholder: pooled <VncFrame> overlays this rect. -->
    <div v-if="instance.vncUrl"
         ref="placeholderRef"
         class="vnc-frame"
         :class="expanded ? 'vnc-expanded flex-1' : ''" />
    <div v-else-if="!stopped && instance.status === 'starting'"
         class="vnc-frame flex flex-col items-center justify-center gap-2"
         :class="expanded ? 'vnc-expanded flex-1' : ''">
      <span class="loading loading-spinner loading-md text-info" />
      <span class="text-xs text-base-content/40">Starting container...</span>
    </div>
    <div v-else class="vnc-frame flex items-center justify-center text-xs text-base-content/30"
         :class="expanded ? 'vnc-expanded flex-1' : ''">
      No VNC (headless)
    </div>
  </div>
</template>

<style>
.vnc-frame {
  aspect-ratio: 16 / 9;
  width: 100%;
}
.vnc-frame.vnc-expanded {
  aspect-ratio: auto;
}
</style>
