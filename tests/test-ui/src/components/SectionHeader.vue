<script setup lang="ts">
import { Icon } from '@iconify/vue'

defineProps<{
  label: string
  count?: number
  icon?: string
}>()
</script>

<template>
  <div class="flex items-center gap-1.5 px-1 mb-2">
    <Icon v-if="icon" :icon="icon" class="w-3 h-3 text-base-content/30" />
    <span class="text-[11px] font-semibold uppercase tracking-widest text-base-content/40">{{ label }}</span>
    <span v-if="count != null" class="text-[10px] text-base-content/25">({{ count }})</span>
  </div>
</template>
