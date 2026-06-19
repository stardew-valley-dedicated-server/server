<script setup lang="ts">
// Imported (not a public/ URL) so Vite hashes it and verifies it exists at
// build time. Resolves under the report's deploy subpath via the relative
// base + <base href>.
import logoUrl from "../assets/img/logo.svg";

defineProps<{
    label: string;
}>();
</script>

<template>
  <div class="flex flex-col items-center justify-center gap-3 text-base-content/40">
    <img :src="logoUrl" alt="" class="w-10 h-10 opacity-20" />
    <span class="text-sm">{{ label }}</span>
  </div>
</template>
