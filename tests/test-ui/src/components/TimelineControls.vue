<script setup lang="ts">
import { Icon } from '@iconify/vue'
import { formatTimeShort } from '../utils/time'

defineProps<{
  playing: boolean
  stopped: boolean
  loop: boolean
  playbackSpeed: number
  zoomLevel: number
  timelinePos: number
  totalDuration: number
}>()

const emit = defineEmits<{
  'toggle-play': []
  'stop': []
  'toggle-loop': []
  'set-speed': [speed: number]
  'zoom-in': []
  'zoom-out': []
  'download-all': []
}>()
</script>

<template>
  <div class="flex items-center gap-4 px-4 py-3 bg-base-300 border-t border-base-content/5">
    <div class="flex items-center gap-1.5">
      <button class="w-[36px] h-[36px] flex items-center justify-center rounded-lg border transition-colors"
              :class="playing
                ? 'border-primary/50 bg-primary/15 text-primary'
                : 'border-base-content/15 bg-neutral text-base-content/50 hover:text-primary hover:border-primary/30'"
              @click="emit('toggle-play')">
        <Icon :icon="playing ? 'lucide:pause' : 'lucide:play'" class="w-5 h-5" :class="!playing ? 'ml-px' : ''" />
      </button>
      <button class="w-[36px] h-[36px] flex items-center justify-center rounded-lg border transition-colors"
              :class="stopped
                ? 'border-accent/50 bg-accent/15 text-accent'
                : 'border-base-content/15 bg-neutral text-base-content/50 hover:text-accent hover:border-accent/30'"
              @click="emit('stop')">
        <Icon icon="lucide:square" class="w-4 h-4" />
      </button>
      <button class="w-[36px] h-[36px] flex items-center justify-center rounded-lg border transition-colors"
              :class="loop
                ? 'border-primary/50 bg-primary/15 text-primary'
                : 'border-base-content/15 bg-neutral text-base-content/50 hover:text-primary hover:border-primary/30'"
              :title="loop ? 'Loop: on' : 'Loop: off'"
              @click="emit('toggle-loop')">
        <Icon icon="lucide:repeat" class="w-4 h-4" />
      </button>
    </div>

    <div class="w-px h-5 bg-base-content/10" />

    <div class="flex items-center gap-0.5 bg-neutral rounded-lg border border-base-content/10 p-0.5">
      <button v-for="speed in [0.1, 0.5, 1, 2, 3, 4]" :key="speed"
              class="h-[30px] w-[36px] flex items-center justify-center rounded-md text-[14px] font-bold transition-colors"
              :class="playbackSpeed === speed
                ? 'bg-primary/20 text-primary border border-primary/40'
                : 'text-base-content/40 hover:text-base-content/70 border border-transparent'"
              @click="emit('set-speed', speed)">
        {{ speed }}x
      </button>
    </div>

    <div class="w-px h-5 bg-base-content/10" />

    <!-- Zoom indicator -->
    <div class="flex items-center gap-1.5">
      <button class="w-6 h-6 flex items-center justify-center rounded text-base-content/40 hover:text-base-content/70 transition-colors"
              @click="emit('zoom-out')">
        <Icon icon="lucide:minus" class="w-3.5 h-3.5" />
      </button>
      <span class="text-[11px] text-base-content/40 tabular-nums font-mono w-10 text-center">
        {{ Math.round(zoomLevel * 100) }}%
      </span>
      <button class="w-6 h-6 flex items-center justify-center rounded text-base-content/40 hover:text-base-content/70 transition-colors"
              @click="emit('zoom-in')">
        <Icon icon="lucide:plus" class="w-3.5 h-3.5" />
      </button>
    </div>

    <div class="w-px h-5 bg-base-content/10" />

    <span class="text-sm text-base-content/50 tabular-nums font-mono whitespace-nowrap">
      {{ formatTimeShort(timelinePos) }}
      <span class="text-base-content/20"> / </span>
      {{ formatTimeShort(totalDuration) }}
    </span>

    <div class="flex-1" />

    <button class="h-[36px] flex items-center gap-2 px-5 rounded-lg border border-base-content/15 bg-base-300/50 text-base-content/50 hover:text-base-content/70 hover:border-base-content/25 text-[13px] font-medium transition-colors"
            @click="emit('download-all')">
      <Icon icon="lucide:download" class="w-4 h-4" />
      Download All
    </button>
  </div>
</template>
