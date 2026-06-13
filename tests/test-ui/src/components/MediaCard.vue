<script setup lang="ts">
import { Icon } from "@iconify/vue";

withDefaults(
    defineProps<{
        /** Bottom bar label text, e.g. "Screenshot · server" */
        label?: string;
        /** Bottom bar icon (Iconify name) */
        icon?: string;
        /** Show loading spinner overlay */
        loading?: boolean;
        /** Force unavailable fallback state */
        unavailable?: boolean;
        /** Primary fallback line when media is unavailable */
        unavailableText?: string;
        /** Optional inline code token rendered as a chip below the primary line */
        unavailableCode?: string;
        /** Optional secondary detail line rendered below the code chip */
        unavailableDetail?: string;
        /** Fallback icon when media is unavailable */
        unavailableIcon?: string;
        /** Background class override (default: bg-base-300) */
        bgClass?: string;
        /** Whether to show the bottom label bar */
        showLabel?: boolean;
    }>(),
    {
        icon: "lucide:image",
        unavailableText: "Media unavailable",
        unavailableIcon: "lucide:image-off",
        bgClass: "bg-base-300",
        showLabel: true,
    },
);
</script>

<template>
  <div class="w-[320px] rounded-lg overflow-hidden border border-base-content/5 flex-none"
       :class="bgClass">
    <div class="relative aspect-video">
      <!-- Media content (always mounted, loads behind overlays) -->
      <slot />

      <!-- Loading spinner overlay (fades out when loading=false) -->
      <div class="absolute inset-0 flex flex-col items-center justify-center bg-base-300 transition-opacity duration-150 pointer-events-none"
           :class="loading ? 'opacity-100' : 'opacity-0'">
        <div class="loading loading-spinner loading-md text-base-content/20"></div>
        <span class="text-[10px] text-base-content/25 mt-1.5">Loading...</span>
      </div>

      <!-- Unavailable fallback (fades in when unavailable=true) -->
      <div class="absolute inset-0 flex flex-col items-center justify-center gap-1.5 px-6 text-center bg-base-300 transition-opacity duration-150 pointer-events-none"
           :class="unavailable && !loading ? 'opacity-100' : 'opacity-0'">
        <Icon :icon="unavailableIcon" class="w-7 h-7 text-base-content/25" />
        <span class="text-xs text-base-content/55 font-medium leading-snug">{{ unavailableText }}</span>
        <code v-if="unavailableCode"
              class="px-1.5 py-0.5 rounded bg-base-content/10 text-[10.5px] text-base-content/65 font-mono leading-none">
          {{ unavailableCode }}
        </code>
        <span v-if="unavailableDetail" class="text-[10.5px] text-base-content/35 leading-snug">{{ unavailableDetail }}</span>
      </div>

      <!-- Overlay slot (play buttons, state labels, etc.) -->
      <slot name="overlay" />
    </div>

    <!-- Bottom label bar -->
    <div v-if="showLabel && label"
         class="px-3 py-2 flex items-center justify-between bg-black/70">
      <div class="flex items-center gap-1.5">
        <Icon :icon="icon" class="w-3.5 h-3.5 text-white/30" />
        <span class="text-[11px] text-white/80 font-medium">{{ label }}</span>
      </div>
      <slot v-if="!unavailable && !loading" name="action">
        <Icon icon="lucide:external-link" class="w-3.5 h-3.5 text-white/25" />
      </slot>
    </div>
  </div>
</template>
