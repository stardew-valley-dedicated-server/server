<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, ref } from "vue";
import { useLogFile } from "../composables/useLogFile";
import { relativeRunPath } from "../composables/useTestStore";
import { useTestUI } from "../composables/useTestUI";
import type { InstanceHistoryEntry } from "../types/state";
import { shortTestName } from "../utils/format";

const props = defineProps<{
    instanceId: string;
    history?: InstanceHistoryEntry[];
}>();

const { store } = useTestUI();

const expanded = ref(false);
const search = ref("");

const live = computed(() => store.state.status === "running");

// container.log path is null when no runMetadata yet; useLogFile gates on that.
const logPath = computed<string | null>(() => {
    const runDir = store.state.runMetadata?.runDir;
    if (!runDir) {
        return null;
    }
    const rel = relativeRunPath(runDir);
    if (!rel) {
        return null;
    }
    return `/artifacts/${rel}/containers/${props.instanceId}/container.log`;
});

// Only mount the composable while the panel is open. Pass a path ref that flips
// to null when collapsed so useLogFile stops fetching.
const activePath = computed<string | null>(() => (expanded.value ? logPath.value : null));
const { lines, loading, error, refresh } = useLogFile(activePath, { live });

// Filtered view (case-insensitive substring match).
const filteredLines = computed(() => {
    const q = search.value.trim().toLowerCase();
    if (!q) {
        return lines.value;
    }
    return lines.value.filter((l) => l.toLowerCase().includes(q));
});

// Container.log is written without inline timestamps (timestampsEnabled: false
// in StreamLogsAsync). We can't draw per-line gutters; instead, surface the
// instance's leased/returned history above the table so the user can correlate
// by wall-clock time. Filter to test-window events with a testName.
const leaseEvents = computed(() => {
    const h = props.history ?? [];
    return h.filter((e) => e.event === "leased" || e.event === "returned");
});

function fmtTime(iso: string): string {
    try {
        const d = new Date(iso);
        return d.toLocaleTimeString([], { hour12: false }) + "." + String(d.getMilliseconds()).padStart(3, "0");
    } catch {
        return iso;
    }
}
</script>

<template>
  <div class="rounded-lg border border-base-content/5 bg-base-200/50 overflow-hidden">
    <button class="w-full flex items-center gap-2 px-3 py-2 text-left hover:bg-base-content/3 transition-colors"
            @click="expanded = !expanded">
      <Icon icon="lucide:chevron-right" class="w-3 h-3 text-base-content/30 transition-transform duration-150"
            :class="{ 'rotate-90': expanded }" />
      <Icon icon="lucide:scroll-text" class="w-3.5 h-3.5 text-base-content/40" />
      <span class="text-xs font-semibold uppercase tracking-widest text-base-content/40">Container Log</span>
      <span v-if="expanded && lines.length" class="text-[10px] text-base-content/30 tabular-nums">{{ lines.length }} lines</span>
      <span v-if="expanded && loading" class="text-[10px] text-info">…</span>
      <span v-if="expanded && error" class="text-[10px] text-error" :title="error">error</span>
    </button>

    <div v-if="expanded" class="border-t border-base-content/5">
      <!-- Toolbar: search + refresh -->
      <div class="flex items-center gap-2 px-3 py-1.5 border-b border-base-content/5 bg-base-300/30">
        <Icon icon="lucide:search" class="w-3 h-3 text-base-content/30 flex-none" />
        <input v-model="search" type="search" placeholder="Filter…"
               class="flex-1 bg-transparent text-[11px] outline-none placeholder:text-base-content/25" />
        <button class="text-[10px] px-1.5 py-0.5 rounded text-base-content/40 hover:text-base-content/70 hover:bg-base-content/5 transition-colors flex items-center gap-1"
                title="Refresh"
                @click="refresh">
          <Icon icon="lucide:refresh-cw" class="w-3 h-3" :class="{ 'animate-spin': loading }" />
        </button>
      </div>

      <!-- Lease/return strip: container.log has no inline timestamps, so we
           surface lease events here as a wall-clock correlation aid rather
           than a fake gutter. -->
      <div v-if="leaseEvents.length > 0"
           class="px-3 py-1.5 border-b border-base-content/5 flex flex-wrap gap-x-3 gap-y-1 text-[10px] text-base-content/50">
        <span v-for="(e, i) in leaseEvents" :key="i" class="inline-flex items-center gap-1 tabular-nums">
          <span class="w-1.5 h-1.5 rounded-full"
                :class="e.event === 'leased' ? 'bg-success' : 'bg-base-content/30'" />
          <span class="text-base-content/30">{{ fmtTime(e.timestamp) }}</span>
          <span v-if="e.testName" class="text-base-content/60">{{ shortTestName(e.testName) }}</span>
          <span v-else class="text-base-content/40 italic">{{ e.event }}</span>
        </span>
      </div>

      <!-- Log table -->
      <div class="font-mono text-[11px] leading-relaxed overflow-auto max-h-[420px] bg-base-300/20">
        <div v-if="lines.length === 0 && !loading" class="px-3 py-4 text-base-content/30 italic">
          No log content yet
        </div>
        <table v-else class="w-full border-collapse">
          <tr v-for="(line, i) in filteredLines" :key="i" class="hover:bg-base-content/3">
            <td class="text-right pr-3 pl-3 select-none text-base-content/20 align-top w-[1%] whitespace-nowrap tabular-nums">{{ i + 1 }}</td>
            <td class="whitespace-pre-wrap break-words pr-3 py-[1px] text-base-content/80">{{ line }}</td>
          </tr>
        </table>
      </div>
    </div>
  </div>
</template>
