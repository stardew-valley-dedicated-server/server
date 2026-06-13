<script setup lang="ts">
import { Icon } from "@iconify/vue";

defineProps<{
    icon: string;
    direction: "asc" | "desc" | null;
}>();
</script>

<template>
  <div class="inline-flex items-center justify-center w-3.5 h-3.5">
    <template v-if="direction">
      <Icon
        :icon="direction === 'asc' ? 'lucide:arrow-up' : 'lucide:arrow-down'"
        class="w-[7px] h-[7px] shrink-0 text-primary"
      />
      <Icon
        :icon="icon"
        class="w-2.5 h-2.5 shrink-0"
      />
    </template>
    <Icon v-else :icon="icon" class="w-3.5 h-3.5" />
  </div>
</template>
