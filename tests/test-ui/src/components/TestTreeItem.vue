<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { formatDuration } from "../utils/format";
import StatusIcon from "./StatusIcon.vue";

defineProps<{
    label: string;
    status: string;
    durationMs?: number | null;
    queueDurationMs?: number | null;
    indent?: number;
    isSelected?: boolean;
    isFocused?: boolean;
    clickable?: boolean;
    isGroup?: boolean;
    isExpanded?: boolean;
    count?: number;
    countTitle?: string;
    subtitle?: string;
    showProgress?: boolean;
    progressComplete?: boolean;
    icon?: "docker" | "none";
    flakyInfo?: { failRate: number; recentRuns: number } | null;
}>();

defineEmits<{
    click: [];
}>();
</script>

<template>
  <div>
    <div
      class="py-0.5 pr-3 select-none"
      :class="clickable ? 'cursor-pointer' : 'cursor-default'"
      :style="{ paddingLeft: `${(indent ?? 0) * 24 + 8}px` }"
      :aria-expanded="isGroup ? isExpanded : undefined"
      @click="$emit('click')"
    >
      <div
        class="tree-item group flex items-center py-1 px-2 rounded-md transition-[background-color,opacity] duration-150 ease-out"
        :class="[
          isSelected ? 'bg-primary/10 hover:bg-primary/20' : isFocused ? 'bg-base-content/10' : 'hover:bg-base-content/15',
          isSelected || isFocused ? 'opacity-100' : 'opacity-60 hover:opacity-100'
        ]"
      >
        <!-- Chevron for groups -->
        <Icon v-if="isGroup" icon="lucide:chevron-right" class="w-3 h-3 flex-none text-base-content/40 transition-transform duration-150" :class="{ 'rotate-90': isExpanded }" />

        <!-- Icon: custom or status -->
        <Icon v-if="icon === 'docker'" icon="simple-icons:docker" class="w-5 h-5 flex-none text-primary" :class="isGroup ? 'ml-1.5' : ''" />
        <StatusIcon v-else-if="icon !== 'none'" :status="status" :size="14" class="flex-none" :class="isGroup ? 'ml-1.5' : ''" />

        <!-- Label -->
        <span
          class="truncate flex-1 text-[13px] ml-1.5"
          :class="[isGroup ? 'font-medium' : '', isSelected ? 'text-primary' : 'text-base-content']"
          :title="label"
        >{{ label }}</span>

        <!-- Subtitle badge -->
        <span v-if="subtitle"
              class="flex-none text-[10px] truncate max-w-[120px] px-1.5 py-0.5 rounded ml-1"
              :class="isSelected ? 'bg-primary/10 text-primary/80' : 'bg-base-content/5 text-base-content/60'">
          {{ subtitle }}
        </span>

        <!-- Count badge for groups -->
        <span v-if="count != null"
              class="flex-none text-[10px] tabular-nums px-1.5 py-0.5 rounded-full font-medium ml-1"
              :class="isSelected ? 'bg-primary/15 text-primary/80' : 'bg-base-content/10 text-base-content/60'"
              :title="countTitle">
          {{ count }}
        </span>

        <!-- Flaky badge -->
        <span v-if="flakyInfo"
              class="flex-none text-[10px] tabular-nums ml-1.5 px-1.5 py-0.5 rounded font-medium bg-warning/20 text-warning"
              :title="`Flaky: failed ${Math.round(flakyInfo.failRate * 100)}% of the last ${flakyInfo.recentRuns} runs`">
          ~{{ Math.round(flakyInfo.failRate * 100) }}%
        </span>

        <!-- Duration -->
        <span v-if="durationMs != null"
              class="flex-none text-[11px] tabular-nums ml-1.5 px-1.5 py-0.5 rounded"
              :class="isSelected ? 'bg-primary/20 text-primary font-medium' : 'text-base-content/60'"
              :title="[
                durationMs != null ? `Test took: ${formatDuration(durationMs)}` : null,
                queueDurationMs != null ? `Queued for: ${formatDuration(queueDurationMs)}` : null
              ].filter(Boolean).join(', ') || undefined">
          {{ formatDuration(durationMs) }}
        </span>
      </div>
    </div>

    <!-- Progress bar (optional, for setup steps) -->
    <div v-if="showProgress" class="pr-3 mt-0.5 mb-1" :style="{ paddingLeft: `${(indent ?? 0) * 24 + 36}px` }">
      <div class="step-progress w-full bg-base-content/8 overflow-hidden">
        <div class="h-full rounded-full transition-[width] duration-300 ease-out"
             :class="progressComplete ? 'bg-success w-full' : 'bg-primary w-2/3 animate-pulse'" />
      </div>
    </div>
  </div>
</template>
