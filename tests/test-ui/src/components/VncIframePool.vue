<script setup lang="ts">
import { computed, ref, watch, onBeforeUnmount } from 'vue'
import VncFrame from './VncFrame.vue'
import { useTestUI } from '../composables/useTestUI'
import { useVncInteractive } from '../composables/useVncInteractive'
import { acquireInstanceSlot, releaseInstanceSlot, destroyInstanceSlot } from '../composables/useIframeLayer'
import { vncIframeSrc } from '../utils/vnc'
import type { InstanceSnapshot } from '../types/state'

// One <VncFrame> per known instance with a VNC url. Each frame is Teleported
// into its pooled wrapper, which lives in the global iframe overlay layer.
// Consumers (VncTile) acquire the same wrapper to publish their placeholder
// rect, so the iframe positions itself over whichever surface is currently
// showing the tile. Iframes survive component unmount/remount cycles
// (test switches, view switches) because the wrapper persists.

const { store } = useTestUI()
const { interactive } = useVncInteractive()

const instancesWithVnc = computed<InstanceSnapshot[]>(() => {
  const all = [...(store.state.instances ?? []), ...store.stoppedInstances]
  const seen = new Set<string>()
  const out: InstanceSnapshot[] = []
  for (const inst of all) {
    if (!inst.vncUrl) continue
    if (seen.has(inst.instanceId)) continue
    seen.add(inst.instanceId)
    out.push(inst)
  }
  return out
})

// Per-instance wrapper ref, kept in sync with instancesWithVnc by the watcher
// below. The pool owns the acquire/release lifecycle so the computed stays pure.
const wrappers = ref<Record<string, HTMLDivElement>>({})

watch(instancesWithVnc, (current, previous) => {
  const liveIds = new Set(current.map(i => i.instanceId))
  const prevIds = new Set((previous ?? []).map(i => i.instanceId))

  // Newly seen instances: acquire a slot.
  for (const inst of current) {
    if (!prevIds.has(inst.instanceId)) {
      const slot = acquireInstanceSlot(inst.instanceId)
      wrappers.value = { ...wrappers.value, [inst.instanceId]: slot.wrapper }
    }
  }

  // Instances that disappeared entirely from the run: release and destroy.
  for (const id of prevIds) {
    if (!liveIds.has(id)) {
      releaseInstanceSlot(id)
      destroyInstanceSlot(id)
      const next = { ...wrappers.value }
      delete next[id]
      wrappers.value = next
    }
  }
}, { immediate: true, flush: 'post' })

onBeforeUnmount(() => {
  // Releasing every slot we own. Wrappers stay alive (per-pool semantics).
  for (const id of Object.keys(wrappers.value)) {
    releaseInstanceSlot(id)
    destroyInstanceSlot(id)
  }
})

function isStopped(inst: InstanceSnapshot): boolean {
  return store.runDone || store.stoppedInstances.some(s => s.instanceId === inst.instanceId)
}

function retainedMessage(inst: InstanceSnapshot): string {
  return inst.recordingPath ? 'Recording captured' : 'Finalizing recording...'
}
</script>

<template>
  <template v-for="inst in instancesWithVnc" :key="inst.instanceId">
    <Teleport v-if="wrappers[inst.instanceId]" :to="wrappers[inst.instanceId]">
      <VncFrame :src="vncIframeSrc(inst.vncUrl!, interactive)"
                :base-url="inst.vncUrl!"
                :title="`${inst.label} VNC`"
                :interactive="interactive"
                :stopped="isStopped(inst)"
                :retained="inst.disposed"
                :retained-message="retainedMessage(inst)"
                :retained-busy="!inst.recordingPath" />
    </Teleport>
  </template>
</template>
