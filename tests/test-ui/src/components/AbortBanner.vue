<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { ref } from "vue";
import { useTestUI } from "../composables/useTestUI";

const { store } = useTestUI();
const dismissed = ref(false);
</script>

<template>
  <div v-if="store.state.status === 'aborted' && !dismissed"
       class="flex items-center gap-2 px-4 h-9 bg-warning/15 border-b border-warning/30 text-warning text-xs flex-none">
    <Icon icon="lucide:alert-triangle" class="w-3.5 h-3.5 flex-none" />
    <span class="flex-1 truncate">
      Run aborted: <span class="font-mono">{{ store.state.abortReason ?? 'connection lost' }}</span>
    </span>
    <button class="w-5 h-5 flex items-center justify-center rounded hover:bg-warning/20 transition-colors flex-none"
            title="Dismiss"
            @click="dismissed = true">
      <Icon icon="lucide:x" class="w-3 h-3" />
    </button>
  </div>
</template>
