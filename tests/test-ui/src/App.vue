<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { computed, onMounted, onUnmounted, provide, ref, watch } from "vue";
import AbortBanner from "./components/AbortBanner.vue";
import InstanceInspect from "./components/InstanceInspect.vue";
import OutputPanel from "./components/OutputPanel.vue";
import OverviewPanel from "./components/OverviewPanel.vue";
import StatusBar from "./components/StatusBar.vue";
import TestTree from "./components/TestTree.vue";
import VncGrid from "./components/VncGrid.vue";
import VncIframePool from "./components/VncIframePool.vue";
import { useInspectNavigation } from "./composables/useInspectNavigation";
import { useRouteSync } from "./composables/useRouteSync";
import { useTestStore } from "./composables/useTestStore";
import type { ActiveView } from "./composables/useTestUI";

const store = useTestStore();
provide("store", store);

// ── Inspect modal: hosted at app scope so it overlays either view ──
const instances = computed(() => store.state.instances ?? []);
const inspect = useInspectNavigation({ instances, stoppedInstances: store.stoppedInstances });
provide("inspect", inspect);

function getServerLabel(serverInstanceId: string | null): string | null {
    if (!serverInstanceId) {
        return null;
    }
    const srv = instances.value.find((i) => i.instanceId === serverInstanceId && i.instanceType === "server");
    return srv?.label ?? serverInstanceId;
}

function resolveLabel(id: string): string {
    const all = [...instances.value, ...store.stoppedInstances];
    return all.find((i) => i.instanceId === id)?.label ?? id;
}

function navigateToTest(testName: string) {
    const t = store.findTest(testName);
    if (t) {
        store.selectTest(t);
        activeView.value = "tests";
        inspect.closeInspect();
    }
}

// ── Theme persistence ──
// DaisyUI's theme-controller mutates data-theme on <html>.
// Observe it and persist to localStorage (restored in index.html inline script).
let themeObserver: MutationObserver | null = null;
onMounted(() => {
    themeObserver = new MutationObserver(() => {
        const theme = document.documentElement.getAttribute("data-theme");
        if (theme) {
            localStorage.setItem("ui-theme", theme);
        }
    });
    themeObserver.observe(document.documentElement, {
        attributes: true,
        attributeFilter: ["data-theme"],
    });
});
onUnmounted(() => themeObserver?.disconnect());

// ── localStorage persistence for layout ──
const LS_KEY = "app-layout-prefs";

interface LayoutPrefs {
    sidebarWidth: number;
    sidebarCollapsed: boolean;
    // No "overview": it's a cold-visit default, not a persisted preference.
    activeView: "tests" | "vnc";
}

function loadLayoutPrefs(): LayoutPrefs {
    try {
        const raw = localStorage.getItem(LS_KEY);
        if (raw) {
            const p = JSON.parse(raw);
            return {
                sidebarWidth: typeof p.sidebarWidth === "number" ? Math.max(0, Math.min(p.sidebarWidth, 600)) : 260,
                sidebarCollapsed: p.sidebarCollapsed === true,
                activeView: p.activeView === "vnc" ? "vnc" : "tests",
            };
        }
    } catch {
        /* ignore corrupt data */
    }
    return { sidebarWidth: 260, sidebarCollapsed: false, activeView: "tests" };
}

function saveLayoutPrefs() {
    try {
        localStorage.setItem(
            LS_KEY,
            JSON.stringify({
                sidebarWidth: sidebarWidth.value,
                sidebarCollapsed: sidebarCollapsed.value,
                // Coerce "overview" away so a reload lands on the URL/selection, not the landing page.
                activeView: activeView.value === "vnc" ? "vnc" : "tests",
            }),
        );
    } catch {
        /* quota exceeded or storage disabled */
    }
}

const prefs = loadLayoutPrefs();
const sidebarWidth = ref(prefs.sidebarWidth);
const sidebarCollapsed = ref(prefs.sidebarCollapsed);
const isResizing = ref(false);
const widthBeforeCollapse = ref(prefs.sidebarCollapsed ? 260 : prefs.sidebarWidth);

// View mode: 'tests' (default), 'vnc', or 'overview' (landing page). The cold-visit
// default to 'overview' is applied by useRouteSync from the initial URL.
const activeView = ref<ActiveView>(prefs.activeView);
provide("activeView", activeView);

watch(activeView, saveLayoutPrefs);

// Two-way URL <-> state sync (deep-linking). Hosted here because store, inspect,
// and activeView all live in this setup scope. No <router-view>; see useRouteSync.
useRouteSync(store, inspect, activeView);

// Shared filter trigger: StatusBar sets this, TestTree reacts to it
const filterToStatus = ref<string | null>(null);
provide("filterToStatus", filterToStatus);

// Cross-component hover link: the Overview's nav cards set this to their target
// view, and the icon-sidebar buttons light up the matching icon — so hovering a
// card previews where it leads. Null when nothing is hovered.
const hoveredNav = ref<ActiveView | null>(null);
provide("hoveredNav", hoveredNav);

function showFailedTests() {
    activeView.value = "tests";
    filterToStatus.value = "failed";
}
provide("showFailedTests", showFailedTests);

const vncCount = computed(() => store.state.instances?.length ?? 0);

// When collapsed, width is 0 (fully hidden). Dragging uses live value.
const effectiveWidth = computed(() => {
    if (isResizing.value) {
        return sidebarWidth.value;
    }
    if (sidebarCollapsed.value) {
        return 0;
    }
    return sidebarWidth.value;
});

function toggleSidebar() {
    if (sidebarCollapsed.value) {
        sidebarCollapsed.value = false;
        sidebarWidth.value = widthBeforeCollapse.value;
    } else {
        widthBeforeCollapse.value = sidebarWidth.value;
        sidebarCollapsed.value = true;
        sidebarWidth.value = 0;
    }
    saveLayoutPrefs();
}

function onSidebarDoubleClick() {
    sidebarWidth.value = 260;
    sidebarCollapsed.value = false;
    widthBeforeCollapse.value = 260;
    saveLayoutPrefs();
}

function onResizeStart(e: PointerEvent) {
    isResizing.value = true;
    const startX = e.clientX;
    const startW = sidebarCollapsed.value ? 32 : sidebarWidth.value;
    const target = e.currentTarget as HTMLElement;
    target.setPointerCapture(e.pointerId);

    function onMove(ev: PointerEvent) {
        const newW = startW + (ev.clientX - startX);
        sidebarWidth.value = Math.max(0, Math.min(newW, 600));
    }

    function onUp() {
        isResizing.value = false;
        if (sidebarWidth.value < 80) {
            widthBeforeCollapse.value = startW > 80 ? startW : 260;
            sidebarWidth.value = 0;
            sidebarCollapsed.value = true;
        } else {
            sidebarCollapsed.value = false;
        }
        saveLayoutPrefs();
        target.removeEventListener("pointermove", onMove);
        target.removeEventListener("pointerup", onUp);
    }

    target.addEventListener("pointermove", onMove);
    target.addEventListener("pointerup", onUp);
}
</script>

<template>
    <div
        class="h-screen flex bg-base-300"
        :class="{ 'select-none': isResizing }"
    >
        <!-- Icon sidebar (full height, always visible) -->
        <aside class="icon-sidebar flex-none flex flex-col items-center py-3 gap-1">
            <button class="icon-sidebar-btn"
                    title="Toggle sidebar"
                    @click="toggleSidebar">
                <Icon icon="lucide:panel-left" class="w-5 h-5" />
            </button>
            <button class="icon-sidebar-btn" :class="{ active: activeView === 'overview' }"
                    title="Overview" @click="activeView = 'overview'">
                <Icon icon="lucide:layout-dashboard" class="w-5 h-5" />
            </button>
            <button class="icon-sidebar-btn" :class="{ active: activeView === 'tests', hovered: hoveredNav === 'tests' }"
                    title="Explorer" @click="activeView = 'tests'; if (sidebarCollapsed) toggleSidebar()">
                <Icon icon="lucide:file-text" class="w-5 h-5" />
            </button>
            <button class="icon-sidebar-btn" :class="{ active: activeView === 'vnc', hovered: hoveredNav === 'vnc' }"
                    title="Containers" @click="activeView = 'vnc'">
                <Icon icon="lucide:container" class="w-5 h-5" />
                <span v-if="vncCount > 0"
                      class="absolute -top-1 -right-1 text-[9px] min-w-[14px] h-[14px] flex items-center justify-center rounded-full bg-primary text-primary-content font-bold">
                    {{ vncCount }}
                </span>
            </button>
            <div class="flex-1" />
            <button class="icon-sidebar-btn opacity-40 cursor-default" title="Settings (coming soon)" disabled>
                <Icon icon="lucide:settings" class="w-5 h-5" />
            </button>
        </aside>

        <!-- Explorer sidebar (full height, visible when tests view active and not collapsed) -->
        <template v-if="activeView === 'tests' && !sidebarCollapsed">
            <div
                class="flex-none bg-base-200 overflow-hidden"
                :class="{ 'sidebar-animate': !isResizing }"
                :style="{ width: `${effectiveWidth}px` }"
            >
                <div
                    class="h-full panel-scroll"
                    style="min-width: 160px"
                >
                    <TestTree />
                </div>
            </div>

            <!-- Resize handle -->
            <div class="flex-none w-0 relative">
                <div
                    class="absolute inset-y-0 -left-[6px] w-[12px] cursor-col-resize group z-20"
                    @pointerdown="onResizeStart"
                    @dblclick="onSidebarDoubleClick"
                >
                    <div
                        class="absolute inset-y-0 left-1/2 -translate-x-1/2 w-[2px] transition-colors duration-150"
                        :class="
                            isResizing
                                ? 'bg-primary/30'
                                : 'bg-base-content/5 group-hover:bg-primary/30'
                        "
                    />
                </div>
            </div>
        </template>

        <!-- Right side: status bar on top, main content below -->
        <div class="flex-1 flex flex-col min-w-0">
            <AbortBanner />
            <StatusBar />
            <div class="flex-1 min-h-0 panel-scroll bg-base-100">
                <OverviewPanel v-if="activeView === 'overview'" />
                <OutputPanel v-else-if="activeView === 'tests'" />
                <VncGrid v-else />
            </div>
        </div>

        <!-- Singleton iframe pool: one <VncFrame> per instance, persists across view switches -->
        <VncIframePool />

        <!-- Inspect modal: hosted here so it overlays either view -->
        <InstanceInspect
            v-if="inspect.inspectInstance.value"
            :instance="inspect.inspectInstance.value"
            :stats="store.instanceStats.get(inspect.inspectId.value!)"
            :stats-history="store.instanceStatsHistory.get(inspect.inspectId.value!) ?? []"
            :instance-count="instances.length || store.stoppedInstances.length"
            :setup-steps="inspect.inspectInstance.value.setupSteps"
            :setup-status="inspect.inspectInstance.value.setupStatus"
            :stopped="store.stoppedInstances.some(i => i.instanceId === inspect.inspectId.value) || inspect.inspectInstance.value.disposed"
            :server-label="getServerLabel(inspect.inspectInstance.value.connectedServerId)"
            :connected-peers="inspect.inspectPeers.value"
            :resolve-label="resolveLabel"
            :can-go-back="inspect.canGoBack.value"
            :has-prev="inspect.hasPrevInspect.value"
            :has-next="inspect.hasNextInspect.value"
            :screenshot-src="store.screenshotSrc"
            @close="inspect.closeInspect"
            @back="inspect.goBackInspect"
            @prev="inspect.prevInspect"
            @next="inspect.nextInspect"
            @navigate-test="navigateToTest"
            @navigate-instance="inspect.pushInspect"
        />
    </div>
</template>
