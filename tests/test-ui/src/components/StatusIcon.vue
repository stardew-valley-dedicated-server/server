<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { statusIcon, statusIconPulse, statusIconSpin, statusTextClass } from "../utils/status";

defineProps<{
    status: string;
    size?: number;
}>();
</script>

<template>
  <Icon
    :icon="statusIcon(status)"
    :width="size ?? 14"
    :height="size ?? 14"
    :class="[
      statusTextClass(status),
      statusIconSpin(status) ? 'animate-spin gpu-accel' : '',
      statusIconPulse(status) ? 'animate-pulse gpu-accel' : ''
    ]"
  />
</template>
