<script setup lang="ts">
import { ref, onMounted } from "vue";
import { useData } from "vitepress";
import { formatDateTime } from "./formatDate";

const { theme, page } = useData();

const iso = new Date(page.value.lastUpdated!).toISOString();
const datetime = ref("");

// Defer formatting to mount: the visitor's local timezone differs from SSR, so
// rendering it server-side would cause a hydration mismatch.
onMounted(() => {
    datetime.value = formatDateTime(page.value.lastUpdated!);
});
</script>

<template>
    <p class="VPLastUpdated">
        {{ theme.lastUpdated?.text || theme.lastUpdatedText || "Last updated" }}:
        <time :datetime="iso">{{ datetime }}</time>
    </p>
</template>

<style scoped>
.VPLastUpdated {
    line-height: 24px;
    font-size: 14px;
    font-weight: 500;
    color: var(--vp-c-text-2);
}

@media (min-width: 640px) {
    .VPLastUpdated {
        line-height: 32px;
    }
}
</style>
