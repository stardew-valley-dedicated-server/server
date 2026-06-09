<script setup lang="ts">
import { ref, onMounted } from "vue";
import { formatDateTime } from "./formatDate";

// Baked in at build time via vite `define` in config.ts.
declare const __BUILD_TIME__: string;

// Defer formatting to mount: it's rendered in the visitor's local timezone, which
// differs from the build machine's, so formatting at SSR would mismatch on hydration.
const builtAt = ref("");
onMounted(() => {
    builtAt.value = formatDateTime(__BUILD_TIME__);
});
</script>

<template>
    <div class="build-stamp">Last built: {{ builtAt }}</div>
</template>

<style scoped>
.build-stamp {
    font-size: 12px;
    color: var(--vp-c-text-3);
}
</style>
