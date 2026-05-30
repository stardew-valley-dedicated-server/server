<script setup lang="ts">
import { onMounted, onUnmounted, watch } from 'vue'
import type { InstanceSnapshot, SetupStepSnapshot } from '../types/state'
import VncTile from './VncTile.vue'
import { createIframeSlot } from '../composables/useIframeLayer'

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
  stopped?: boolean
  retained?: boolean
  /** Placeholder element in the active layout; the card-body wrapper tracks its rect. */
  placeholderEl?: HTMLElement | null
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
  placeholderEl: null
})

const emit = defineEmits<{
  toggleExpand: []
  inspect: []
}>()

// The card body lives in the global overlay layer so it survives layout
// switches. The wrapper tracks the active layout's placeholder rect, and
// VncTile (teleported inside) hosts the chrome + iframe placeholder.
const slot = createIframeSlot()

onMounted(() => {
  if (props.placeholderEl) slot.track(props.placeholderEl)
})

onUnmounted(() => {
  slot.destroy()
})

watch(() => props.placeholderEl, (el) => {
  if (el) slot.track(el)
  else slot.untrack()
})
</script>

<template>
  <Teleport :to="slot.wrapper">
    <VncTile :instance="instance"
             :spinner-color="spinnerColor"
             :expanded="expanded"
             :connection-count="connectionCount"
             :connected-server-label="connectedServerLabel"
             :stats="stats"
             :setup-steps="setupSteps"
             :setup-status="setupStatus"
             :instance-count="instanceCount"
             :stopped="stopped"
             :retained="retained"
             @toggle-expand="emit('toggleExpand')"
             @inspect="emit('inspect')" />
  </Teleport>
</template>
