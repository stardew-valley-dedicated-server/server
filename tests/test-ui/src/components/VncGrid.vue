<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { animateTransition, setClipHost } from "../composables/useIframeLayer";
import { useTestUI } from "../composables/useTestUI";
import { type TopologyGroup, useTopologyLines } from "../composables/useTopologyLines";
import { useVncInteractive } from "../composables/useVncInteractive";
import type { InstanceSnapshot } from "../types/state";
import { instanceStatusDotClass, instanceStatusLabel } from "../utils/instance-status";
import CombinedCharts from "./CombinedCharts.vue";
import InfrastructureTimeline from "./InfrastructureTimeline.vue";
import InstanceCard from "./InstanceCard.vue";
import SectionHeader from "./SectionHeader.vue";

const { store, inspect } = useTestUI();
const { interactive } = useVncInteractive();

// ── localStorage persistence ──
const LS_KEY = "vnc-grid-prefs";
const DEFAULT_SPLIT = 0.6;

interface VncGridPrefs {
    layout: "grid" | "split" | "topology" | "charts";
    splitFraction: number;
}

function loadPrefs(): VncGridPrefs {
    try {
        const raw = localStorage.getItem(LS_KEY);
        if (raw) {
            const parsed = JSON.parse(raw);
            const validLayouts = ["grid", "split", "topology", "charts"];
            return {
                layout: validLayouts.includes(parsed.layout) ? parsed.layout : "grid",
                splitFraction:
                    typeof parsed.splitFraction === "number"
                        ? Math.max(0.15, Math.min(0.85, parsed.splitFraction))
                        : DEFAULT_SPLIT,
            };
        }
    } catch {
        /* ignore corrupt data */
    }
    return { layout: "grid", splitFraction: DEFAULT_SPLIT };
}

function savePrefs() {
    try {
        const raw = localStorage.getItem(LS_KEY);
        const parsed = raw ? JSON.parse(raw) : {};
        parsed.layout = layout.value;
        parsed.splitFraction = splitFraction.value;
        localStorage.setItem(LS_KEY, JSON.stringify(parsed));
    } catch {
        /* ignore */
    }
}

const prefs = loadPrefs();
type LayoutMode = "grid" | "split" | "topology" | "charts";
const layout = ref<LayoutMode>(prefs.layout);
const expandedId = ref<string | null>(null);

const instances = computed(() => store.state.instances ?? []);
const { openInspect } = inspect;
const servers = computed(() => instances.value.filter((i) => i.instanceType === "server"));
const clients = computed(() => instances.value.filter((i) => i.instanceType === "client"));
const hasAnyEndpoints = computed(() => instances.value.length > 0);
const hasStoppedInstances = computed(() => store.stoppedInstances.length > 0);
const stoppedServers = computed(() => store.stoppedInstances.filter((i) => i.instanceType === "server"));
const stoppedClients = computed(() => store.stoppedInstances.filter((i) => i.instanceType === "client"));

// ── Active VNC layout: expanded overrides layout ──
const activeLayout = computed<"expanded" | LayoutMode>(() => (expandedId.value ? "expanded" : layout.value));

// ── Root ref: hosts the iframe overlay layer ──
const rootRef = ref<HTMLElement | null>(null);

// ── Layout container refs ──
// For 'grid' / 'split' / 'topology' the slot container IS the scroll panel
// (overflow-auto and the `data-slot-id` children sit on the same element).
// For 'expanded' they differ: the outer panel scrolls, the inner div hosts the slot.
const gridContainerRef = ref<HTMLElement | null>(null);
const splitContainerRef = ref<HTMLElement | null>(null);
const topologyContainerRef = ref<HTMLElement | null>(null);
const expandedPanelRef = ref<HTMLElement | null>(null);
const expandedContainerRef = ref<HTMLElement | null>(null);

function activeContainerRef(): HTMLElement | null {
    if (expandedId.value) {
        return expandedContainerRef.value;
    }
    switch (layout.value) {
        case "grid":
            return gridContainerRef.value;
        case "split":
            return splitContainerRef.value;
        case "topology":
            return topologyContainerRef.value;
        case "charts":
            return null;
    }
}

// The active scrollable panel — used as the iframe-overlay clip host so VNC
// tiles disappear at the panel's top edge instead of painting over the toolbar
// and Infrastructure Timeline above it when the panel is scrolled.
function activeScrollPanelRef(): HTMLElement | null {
    if (expandedId.value) {
        return expandedPanelRef.value;
    }
    switch (layout.value) {
        case "grid":
            return gridContainerRef.value;
        case "split":
            return splitContainerRef.value;
        case "topology":
            return topologyContainerRef.value;
        case "charts":
            return null;
    }
}

// ── Slot map: instanceId → placeholder HTMLElement in active layout ──
const slotMap = ref<Record<string, HTMLElement>>({});
const slotVersion = ref(0);

function syncSlots() {
    const container = activeContainerRef();
    const map: Record<string, HTMLElement> = {};
    if (container) {
        for (const el of container.querySelectorAll<HTMLElement>("[data-slot-id]")) {
            map[el.dataset.slotId!] = el;
        }
    }
    slotMap.value = map;
    slotVersion.value++;
}

// Re-sync when layout/expand changes or instances change
watch(
    [activeLayout, instances],
    () => {
        nextTick(syncSlots);
    },
    { deep: true },
);

// ── Connection counts for server badges ──
const serverConnectionCounts = computed(() => {
    const counts: Record<string, number> = {};
    for (const c of clients.value) {
        if (c.connectedServerId) {
            counts[c.connectedServerId] = (counts[c.connectedServerId] || 0) + 1;
        }
    }
    return counts;
});

function getServerLabel(serverInstanceId: string | null): string | null {
    if (!serverInstanceId) {
        return null;
    }
    const srv = servers.value.find((s) => s.instanceId === serverInstanceId);
    return srv?.label ?? serverInstanceId;
}

// ── Topology grouping ──
const topologyGroups = computed((): TopologyGroup[] =>
    servers.value.map((srv) => ({
        server: srv,
        clients: clients.value.filter((c) => c.connectedServerId === srv.instanceId),
    })),
);
const unconnectedClients = computed(() => clients.value.filter((c) => !c.connectedServerId));

function toggleExpand(id: string) {
    expandedId.value = expandedId.value === id ? null : id;
}

const expandedInstance = computed(() =>
    expandedId.value ? (instances.value.find((i) => i.instanceId === expandedId.value) ?? null) : null,
);

function switchLayout(newLayout: LayoutMode) {
    // 1. Snapshot current positions (animateTransition freezes them)
    animateTransition();
    // 2. Switch the layout. Vue updates the DOM on next tick.
    layout.value = newLayout;
    expandedId.value = null;
    // 3. Sync slot map immediately after DOM update so wrappers animate to new positions
    nextTick(() => {
        syncSlots();
    });
}

// ── Helper: props for an InstanceCard ──
function cardProps(inst: InstanceSnapshot) {
    void slotVersion.value; // reactive dep
    const isServer = inst.instanceType === "server";
    return {
        instance: inst,
        interactive: interactive.value,
        spinnerColor: isServer ? "text-success" : "text-info",
        connectionCount: isServer ? serverConnectionCounts.value[inst.instanceId] : undefined,
        connectedServerLabel: !isServer ? getServerLabel(inst.connectedServerId) : undefined,
        instanceCount: instances.value.length,
        stats: store.instanceStats.get(inst.instanceId),
        setupSteps: inst.setupSteps,
        setupStatus: inst.setupStatus,
        stopped: store.runDone,
        retained: inst.disposed,
        expanded: expandedId.value === inst.instanceId,
        placeholderEl: slotMap.value[inst.instanceId] ?? null,
    };
}

// ── Split resizing ──
const splitFraction = ref(prefs.splitFraction);
const isResizing = ref(false);

const viewportWidth = ref(window.innerWidth);

const { updateConnectionLines, linesForGroup, linePath } = useTopologyLines({
    topologyContainerRef,
    topologyGroups,
    unconnectedClients,
    layout,
    viewportWidth,
});

function onWindowResize() {
    viewportWidth.value = window.innerWidth;
}

// Re-bind the iframe-overlay clip host to the active scrollable panel whenever
// the layout (or expanded state) changes. nextTick lets v-show / v-if mount the
// new panel before we read its ref. Falls back to rootRef for 'charts' mode.
watch(
    activeLayout,
    () => {
        nextTick(() => {
            setClipHost(activeScrollPanelRef() ?? rootRef.value);
        });
    },
    { immediate: true },
);

onMounted(() => {
    onWindowResize();
    nextTick(syncSlots);
    window.addEventListener("resize", onWindowResize);
    window.addEventListener("resize", updateConnectionLines);
});

onUnmounted(() => {
    window.removeEventListener("resize", onWindowResize);
    window.removeEventListener("resize", updateConnectionLines);
});

watch(layout, savePrefs);

// Close expanded VNC view when run ends (VNC iframes are stale), but keep inspect available
watch(
    () => store.runDone,
    (over) => {
        if (over) {
            expandedId.value = null;
        }
    },
);

function onSplitResizeStart(e: PointerEvent) {
    if (!splitContainerRef.value) {
        return;
    }
    isResizing.value = true;
    const target = e.currentTarget as HTMLElement;
    target.setPointerCapture(e.pointerId);

    const containerRect = splitContainerRef.value.getBoundingClientRect();
    const padding = 12; // p-3
    const handleWidth = 12;

    function onMove(ev: PointerEvent) {
        const totalW = containerRect.width - padding * 2 - handleWidth;
        const fraction = (ev.clientX - containerRect.left - padding) / totalW;
        splitFraction.value = Math.max(0.15, Math.min(0.85, fraction));
    }

    function onUp() {
        isResizing.value = false;
        savePrefs();
        target.removeEventListener("pointermove", onMove);
        target.removeEventListener("pointerup", onUp);
    }

    target.addEventListener("pointermove", onMove);
    target.addEventListener("pointerup", onUp);
}

function onSplitDoubleClick() {
    splitFraction.value = DEFAULT_SPLIT;
    savePrefs();
}
</script>

<template>
  <div ref="rootRef" class="flex flex-col h-full" :class="{ 'select-none': isResizing }">
    <!-- Empty state (no live and no stopped instances) -->
    <div v-if="!hasAnyEndpoints && !hasStoppedInstances"
         class="flex-1 flex flex-col items-center justify-center text-base-content/30 gap-3">
      <Icon icon="lucide:container" class="w-12 h-12 opacity-20" />
      <span class="text-sm">No containers yet</span>
      <span class="text-xs text-base-content/20">Server and client containers appear here as the run starts them</span>
    </div>

    <!-- Has instances (live or stopped) -->
    <template v-else>
      <!-- One InstanceCard per instance, rendered once, never unmounted on layout switch.
           Each card creates its own fixed-position overlay and tracks its placeholder. -->
      <div v-if="hasAnyEndpoints" class="card-container">
        <InstanceCard v-for="inst in instances" :key="inst.instanceId"
                      v-bind="cardProps(inst)"
                      @toggle-expand="toggleExpand(inst.instanceId)"
                      @inspect="openInspect(inst.instanceId)" />
      </div>

      <!-- Toolbar -->
      <div class="flex items-center justify-between px-4 py-2 bg-base-200/50 flex-none">
        <div class="flex items-center gap-2">
          <Icon :icon="hasAnyEndpoints ? 'lucide:monitor' : 'lucide:history'" class="w-4 h-4 text-base-content/40" />
          <span v-if="hasAnyEndpoints" class="text-[12px] font-semibold text-base-content/60">
            {{ servers.length }} {{ servers.length === 1 ? 'server' : 'servers' }},
            {{ clients.length }} {{ clients.length === 1 ? 'client' : 'clients' }}
          </span>
          <span v-else class="text-[12px] font-semibold text-base-content/60">
            Past Containers
            <span class="text-[10px] text-base-content/25">({{ store.stoppedInstances.length }})</span>
          </span>
          <span v-if="hasAnyEndpoints && !interactive && layout !== 'charts'"
                class="text-[10px] text-base-content/30">(view only)</span>
        </div>
        <div class="flex items-center gap-1">
          <!-- Layout toggle -->
          <div class="join join-horizontal">
            <button class="join-item btn btn-ghost btn-xs px-1.5"
                    :class="layout === 'grid' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                    :disabled="!hasAnyEndpoints && !hasStoppedInstances"
                    title="Grid layout"
                    @click="switchLayout('grid')">
              <Icon icon="lucide:layout-grid" class="w-3.5 h-3.5" />
            </button>
            <button class="join-item btn btn-ghost btn-xs px-1.5"
                    :class="layout === 'split' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                    :disabled="!hasAnyEndpoints"
                    title="Split layout (servers left, clients right)"
                    @click="switchLayout('split')">
              <Icon icon="lucide:columns" class="w-3.5 h-3.5" />
            </button>
            <button class="join-item btn btn-ghost btn-xs px-1.5"
                    :class="layout === 'topology' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                    :disabled="!hasAnyEndpoints"
                    title="Topology layout (grouped by connection)"
                    @click="switchLayout('topology')">
              <Icon icon="lucide:network" class="w-3.5 h-3.5" />
            </button>
            <button class="join-item btn btn-ghost btn-xs px-1.5"
                    :class="layout === 'charts' ? 'btn-active text-base-content/70' : 'text-base-content/30'"
                    title="Combined performance charts"
                    @click="switchLayout('charts')">
              <Icon icon="lucide:bar-chart-3" class="w-3.5 h-3.5" />
            </button>
          </div>
          <!-- Interactive toggle (only relevant for VNC layouts) -->
          <button v-if="layout !== 'charts' && hasAnyEndpoints"
                  class="btn btn-ghost btn-xs gap-1 text-[11px]"
                  :class="interactive ? 'text-warning' : 'text-base-content/40'"
                  :title="interactive ? 'Disable interactive mode' : 'Enable interactive mode (mouse + keyboard)'"
                  aria-label="Toggle interactive mode"
                  :aria-pressed="interactive"
                  @click="interactive = !interactive">
            <Icon :icon="interactive ? 'lucide:mouse-pointer' : 'lucide:eye'" class="w-3.5 h-3.5" />
            {{ interactive ? 'Interactive' : 'View Only' }}
          </button>
        </div>
      </div>

      <!-- Run-scoped infrastructure timeline -->
      <InfrastructureTimeline />

      <!-- Combined performance charts -->
      <CombinedCharts v-if="layout === 'charts'" />

      <!-- Past containers list (stopped-only, non-charts layout) -->
      <div v-else-if="!hasAnyEndpoints && hasStoppedInstances" class="flex-1 overflow-auto p-4 space-y-4">
        <template v-for="group in [
          { type: 'server' as const, label: 'Servers', icon: 'lucide:server', items: stoppedServers },
          { type: 'client' as const, label: 'Clients', icon: 'lucide:monitor', items: stoppedClients }
        ]" :key="group.type">
          <div v-if="group.items.length > 0">
            <SectionHeader :label="group.label" :count="group.items.length" :icon="group.icon" />
            <div class="grid grid-cols-[repeat(auto-fill,minmax(200px,1fr))] gap-2">
              <button v-for="inst in group.items" :key="inst.instanceId"
                      :data-stopped-id="inst.instanceId"
                      class="flex items-center gap-2 px-3 py-2 rounded-lg bg-base-200/30 border border-base-content/5
                             hover:bg-base-200/60 transition-colors text-left group"
                      @click="openInspect(inst.instanceId)">
                <Icon :icon="group.icon" class="w-4 h-4 text-base-content/30 flex-none" />
                <span class="text-xs text-base-content/50 font-medium truncate flex-1">{{ inst.label }}</span>
                <span :class="instanceStatusDotClass(inst.status, inst.connected, true, 'sm')"
                      :title="instanceStatusLabel(inst.status, inst.connected, true, true)"
                      aria-hidden="true" />
                <Icon icon="lucide:chevron-right"
                      class="w-3 h-3 text-base-content/10 group-hover:text-base-content/40 transition-colors flex-none" />
              </button>
            </div>
          </div>
        </template>
      </div>

      <!-- Live VNC layouts (only when has live instances and not in charts mode) -->
      <template v-else-if="hasAnyEndpoints">

      <!-- Inline past containers (during run, when some containers have been disposed) -->
      <div v-if="hasStoppedInstances" class="flex items-center gap-1.5 px-4 py-1.5 bg-base-200/30 flex-none border-t border-base-content/5">
        <Icon icon="lucide:history" class="w-3 h-3 text-base-content/30 flex-none" />
        <span class="text-[11px] font-semibold uppercase tracking-widest text-base-content/40 flex-none">Past</span>
        <div class="flex flex-wrap gap-1 flex-1 min-w-0">
          <button v-for="inst in store.stoppedInstances" :key="inst.instanceId"
                  :data-stopped-id="inst.instanceId"
                  class="flex items-center gap-1.5 px-2 py-0.5 rounded bg-base-200/30
                         hover:bg-base-200/60 transition-colors text-left group"
                  @click="openInspect(inst.instanceId)">
            <Icon :icon="inst.instanceType === 'server' ? 'lucide:server' : 'lucide:monitor'"
                  class="w-3 h-3 text-base-content/30 flex-none" />
            <span class="text-[11px] text-base-content/50 font-medium truncate">{{ inst.label }}</span>
            <span :class="instanceStatusDotClass(inst.status, inst.connected, true, 'sm')"
                  :title="instanceStatusLabel(inst.status, inst.connected, true, true)"
                  aria-hidden="true" />
            <Icon icon="lucide:chevron-right"
                  class="w-3 h-3 text-base-content/10 group-hover:text-base-content/40 transition-colors flex-none" />
          </button>
        </div>
      </div>

      <!-- Expanded panel overlay -->
      <div v-show="activeLayout === 'expanded'" ref="expandedPanelRef"
           class="layout-panel overflow-auto p-3 flex flex-col">
        <div class="flex items-center gap-2 mb-2 px-1">
          <button class="btn btn-ghost btn-xs gap-1 text-[11px] text-base-content/50"
                  @click="expandedId = null">
            <Icon icon="lucide:minimize-2" class="w-3.5 h-3.5" />
            Collapse
          </button>
          <span v-if="expandedInstance" class="text-[11px] text-base-content/40">{{ expandedInstance.label }}</span>
        </div>
        <div ref="expandedContainerRef" class="flex-1 min-h-0">
          <div v-if="expandedId" :data-slot-id="expandedId" class="w-full h-full" />
        </div>
      </div>

      <!-- Grid layout -->
      <div v-show="activeLayout === 'grid'" ref="gridContainerRef"
           class="layout-panel overflow-auto p-3 space-y-3">
        <div v-if="servers.length > 0">
          <SectionHeader label="Servers" :count="servers.length" />
          <div class="vnc-grid">
            <div v-for="inst in servers" :key="inst.instanceId"
                 :data-slot-id="inst.instanceId" class="vnc-slot" />
          </div>
        </div>
        <div v-if="clients.length > 0">
          <SectionHeader label="Clients" :count="clients.length" />
          <div class="vnc-grid">
            <div v-for="inst in clients" :key="inst.instanceId"
                 :data-slot-id="inst.instanceId" class="vnc-slot" />
          </div>
        </div>
      </div>

      <!-- Split layout: two columns, page scrolls naturally -->
      <div v-show="activeLayout === 'split'"
           ref="splitContainerRef"
           class="layout-panel overflow-auto p-3">
        <div class="split-columns" :style="{ '--split': splitFraction }">
          <!-- Servers column -->
          <div class="min-w-0">
            <SectionHeader label="Servers" :count="servers.length" />
            <div class="split-server-list">
              <div v-for="inst in servers" :key="inst.instanceId"
                   :data-slot-id="inst.instanceId" class="vnc-slot" />
            </div>
          </div>

          <!-- Resize handle -->
          <div class="split-handle-track">
            <div class="split-handle group"
                 @pointerdown="onSplitResizeStart"
                 @dblclick="onSplitDoubleClick">
              <div class="split-handle-line"
                   :class="isResizing ? 'bg-primary/30' : 'bg-base-content/8 group-hover:bg-primary/30'" />
              <div class="split-handle-dots"
                   :class="isResizing ? 'text-primary/40' : 'text-base-content/15 group-hover:text-primary/40'">
                <span class="w-1 h-1 rounded-full bg-current" />
                <span class="w-1 h-1 rounded-full bg-current" />
                <span class="w-1 h-1 rounded-full bg-current" />
              </div>
            </div>
          </div>

          <!-- Clients column -->
          <div v-if="clients.length > 0" class="min-w-0">
            <SectionHeader label="Clients" :count="clients.length" />
            <div class="split-client-grid">
              <div v-for="inst in clients" :key="inst.instanceId"
                   :data-slot-id="inst.instanceId" class="vnc-slot" />
            </div>
          </div>
        </div>
      </div>

      <!-- Topology layout -->
      <div v-show="activeLayout === 'topology'"
           ref="topologyContainerRef"
           class="layout-panel topology-fit overflow-auto p-3 space-y-6">
        <div v-for="(group, groupIdx) in topologyGroups" :key="group.server.instanceId"
             data-topology-group
             class="relative bg-base-200/20 rounded-lg p-4">
          <div class="flex items-center gap-1.5 px-1 mb-3">
            <span class="text-[11px] font-semibold text-base-content/40">{{ group.server.label }}</span>
            <span class="text-[10px] text-base-content/25">
              ({{ group.clients.length }} {{ group.clients.length === 1 ? 'client' : 'clients' }})
            </span>
          </div>
          <!-- Server on top -->
          <div class="topology-server" data-topology-server
               :data-slot-id="group.server.instanceId" />
          <!-- Connection line space + client grid below -->
          <div v-if="group.clients.length > 0" class="topology-clients-area">
            <div class="vnc-grid">
              <div v-for="client in group.clients" :key="client.instanceId"
                   class="vnc-slot" data-topology-client
                   :data-topology-active="client.status === 'in_use' && client.connected"
                   :data-slot-id="client.instanceId" />
            </div>
          </div>
          <div v-else class="flex items-center justify-center py-4">
            <span class="text-[11px] text-base-content/20 italic">No connected clients</span>
          </div>
          <svg v-if="linesForGroup(groupIdx).length > 0 && viewportWidth > 600"
               class="absolute inset-0 pointer-events-none overflow-visible"
               :width="'100%'" :height="'100%'">
            <path v-for="(line, lineIdx) in linesForGroup(groupIdx)" :key="lineIdx"
                  :d="linePath(line)" fill="none"
                  :stroke="line.active ? 'var(--color-success)' : 'color-mix(in oklch, var(--color-base-content) 40%, transparent)'"
                  stroke-width="1.5"
                  :stroke-dasharray="line.active ? '6 3' : 'none'"
                  :class="{ 'topology-line-animated': line.active }" />
          </svg>
        </div>
        <div v-if="unconnectedClients.length > 0">
          <SectionHeader label="Idle Clients" :count="unconnectedClients.length" />
          <div class="vnc-grid">
            <div v-for="inst in unconnectedClients" :key="inst.instanceId"
                 :data-slot-id="inst.instanceId" class="vnc-slot" />
          </div>
        </div>
      </div>

      </template>
    </template>

  </div>
</template>

<style scoped>
/* ── Card container: hidden, just keeps InstanceCards mounted ── */
.card-container {
  position: absolute;
  width: 0;
  height: 0;
  overflow: hidden;
  pointer-events: none;
}

/* ── Layout panels ── */
.layout-panel {
  flex: 1 1 0%;
  min-height: 0;
}
/* Topology groups must grow to fit content; never clip children */
.layout-panel.topology-fit {
  flex-basis: auto;
  min-height: min-content;
}

/* ── Placeholder slots: width from layout, height synced from card content.
   flex-shrink: 0 prevents panels from compressing in flex/scroll containers.
   min-height prevents collapse before first RAF sync. ── */
.vnc-slot {
  width: 100%;
  min-height: 48px;
  flex-shrink: 0;
}

/* ── Grid layout ── */
.vnc-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
}
@media (max-width: 1200px) { .vnc-grid { grid-template-columns: repeat(3, 1fr); } }
@media (max-width: 900px) { .vnc-grid { grid-template-columns: repeat(2, 1fr); } }
@media (max-width: 600px) { .vnc-grid { grid-template-columns: 1fr; } }

/* ── Split layout: two resizable columns, natural page scroll ── */
.split-columns {
  display: grid;
  grid-template-columns: calc(var(--split) * 100% - 6px) 12px 1fr;
  align-items: start;
}
.split-server-list { display: flex; flex-direction: column; gap: 8px; }
.split-client-grid { display: flex; flex-direction: column; gap: 8px; }
.split-handle-track { position: relative; }
.split-handle {
  position: sticky;
  top: 12px;
  cursor: col-resize;
  width: 12px;
  height: 48px;
  margin: 0 auto;
}
.split-handle-line {
  position: absolute;
  inset: 0;
  width: 2px;
  margin: 0 auto;
  transition: background-color 150ms;
}
.split-handle-dots {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  display: flex;
  flex-direction: column;
  gap: 3px;
  transition: color 150ms;
}
@media (max-width: 900px) {
  .split-columns { grid-template-columns: 1fr; }
  .split-handle-track { display: none; }
  .split-client-grid { display: grid; grid-template-columns: repeat(2, 1fr); }
}
@media (max-width: 600px) { .split-client-grid { grid-template-columns: 1fr; } }

/* ── Topology layout ── */
.topology-server {
  width: 50%;
  margin: 0 auto;
}
.topology-clients-area {
  margin-top: 48px;  /* whitespace for connection lines */
}

@keyframes dash-flow { to { stroke-dashoffset: -18; } }
.topology-line-animated { animation: dash-flow 1s linear infinite; }

/* ── Scroll-to-instance highlight ── */
@keyframes instance-pulse {
  0%, 100% { box-shadow: 0 0 0 0 transparent; }
  50% { box-shadow: 0 0 0 3px color-mix(in oklch, var(--color-primary) 40%, transparent); }
}
.instance-highlight {
  animation: instance-pulse 0.6s ease-in-out 3;
  border-radius: 6px;
}
</style>
