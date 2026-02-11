<script setup lang="ts">
import { ref, computed } from "vue";
import { useNavBarExtra } from "./useNavBarExtra";

interface Version {
    id: string;
    name: string;
    path: string;
    badge?: string;
    badgeType?: "tip" | "warning";
}

defineProps<{
    /** When true, renders inline for mobile nav screen (no dropdown) */
    inline?: boolean;
}>();

const versions: Version[] = [
    { id: "latest", name: "Latest", path: "/server/", badge: "unstable", badgeType: "warning" },
    { id: "preview", name: "Preview", path: "/server/preview/", badge: "unstable", badgeType: "warning" },
];

const { isMediumScreen, extraMenuTarget, isInlineOpen, toggleInline } = useNavBarExtra('__versionSwitcherObserver');
const isOpen = ref(false);

const currentVersion = computed(() => {
    if (typeof window === "undefined") return versions[0];
    const path = window.location.pathname;
    if (path.startsWith("/server/preview")) {
        return versions.find((v) => v.id === "preview") || versions[0];
    }
    return versions[0];
});

function switchToVersion(version: Version) {
    if (typeof window === "undefined") return;

    const currentPath = window.location.pathname;
    let relativePath = currentPath;

    // Remove current version prefix to get relative path
    if (currentPath.startsWith("/server/preview/")) {
        relativePath = currentPath.replace("/server/preview", "");
    } else if (currentPath.startsWith("/server/")) {
        relativePath = currentPath.replace("/server", "");
    }

    if (!relativePath.startsWith("/")) {
        relativePath = "/" + relativePath;
    }

    const newPath = version.path.replace(/\/$/, "") + relativePath;
    window.location.href = newPath;
}

function toggleDropdown() {
    isOpen.value = !isOpen.value;
}

function closeDropdown() {
    isOpen.value = false;
}
</script>

<template>
    <!-- INLINE MODE: For mobile nav-screen, collapsible accordion -->
    <div v-if="inline" class="VPVersionSwitcherInline">
        <div class="inline-header" @click="toggleInline">
            <span class="inline-title">Version</span>
            <svg class="inline-icon" :class="{ open: isInlineOpen }" xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <line v-if="!isInlineOpen" x1="12" y1="5" x2="12" y2="19" />
                <line x1="5" y1="12" x2="19" y2="12" />
            </svg>
        </div>
        <Transition name="accordion">
            <div v-if="isInlineOpen" class="inline-items">
                <div
                    v-for="version in versions"
                    :key="version.id"
                    class="inline-item"
                    :class="{ active: version.id === currentVersion.id }"
                    @click="switchToVersion(version)"
                >
                    <span class="inline-item-text">{{ version.name }}</span>
                    <span v-if="version.badge" class="inline-item-badge" :class="version.badgeType">
                        {{ version.badge }}
                    </span>
                </div>
            </div>
        </Transition>
    </div>

    <!-- TELEPORT MODE: For tablet/medium screens, inject into triple-dot menu -->
    <Teleport v-else-if="isMediumScreen && extraMenuTarget" :to="extraMenuTarget">
        <div class="group versions">
            <p class="group-title">Version</p>
            <div
                v-for="version in versions"
                :key="version.id"
                class="menu-item"
                :class="{ active: version.id === currentVersion.id }"
                @click="switchToVersion(version)"
            >
                <span class="menu-item-text">{{ version.name }}</span>
                <span v-if="version.badge" class="menu-item-badge" :class="version.badgeType">
                    {{ version.badge }}
                </span>
            </div>
        </div>
    </Teleport>

    <!-- DROPDOWN MODE: For desktop (>= 1280px), show standalone dropdown -->
    <div v-else-if="!inline && !isMediumScreen" class="VPVersionSwitcher" @click.stop>
        <div class="divider" />
        <button type="button" class="button" :title="`Version: ${currentVersion.name}`" @click="toggleDropdown">
            <span class="version-label">{{ currentVersion.name }}</span>
            <svg class="chevron" :class="{ open: isOpen }" xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <polyline points="6 9 12 15 18 9"></polyline>
            </svg>
        </button>

        <Transition name="flyout">
            <div v-if="isOpen" class="flyout" @click.stop>
                <div class="items">
                    <div
                        v-for="version in versions"
                        :key="version.id"
                        class="item"
                        :class="{ active: version.id === currentVersion.id }"
                        @click="switchToVersion(version)"
                    >
                        <span class="text">{{ version.name }}</span>
                        <span v-if="version.badge" class="badge" :class="version.badgeType">
                            {{ version.badge }}
                        </span>
                        <span class="check">{{ version.id === currentVersion.id ? '✓' : '' }}</span>
                    </div>
                </div>
            </div>
        </Transition>

        <div v-if="isOpen" class="backdrop" @click="closeDropdown" />
    </div>
</template>

<style scoped>
/*******************************************************************************
 * INLINE MODE STYLES (Mobile nav-screen)
 * Collapsible accordion matching VPNavScreenAppearance styling
 ******************************************************************************/
.VPVersionSwitcherInline {
    display: flex;
    flex-direction: column;
    margin-top: 12px;
}

.inline-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 14px 12px 16px;
    border-radius: 8px;
    background-color: var(--vp-c-bg-soft);
    cursor: pointer;
    transition: color 0.25s;
}

.inline-header:hover .inline-title {
    color: var(--vp-c-text-1);
}

.inline-title {
    font-size: 12px;
    font-weight: 500;
    color: var(--vp-c-text-2);
    transition: color 0.25s;
}

.inline-icon {
    color: var(--vp-c-text-2);
    transition: transform 0.25s;
}

.inline-icon.open {
    transform: rotate(90deg);
}

.inline-items {
    display: flex;
    flex-direction: column;
    border-radius: 8px;
    background-color: var(--vp-c-bg-soft);
    overflow: hidden;
    margin-top: 8px;
}

.inline-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 14px 12px 16px;
    cursor: pointer;
    transition: color 0.25s;
}

.inline-item:not(:last-child) {
    border-bottom: 1px solid var(--vp-c-divider);
}

.inline-item:hover {
    color: var(--vp-c-brand-1);
}

.inline-item.active {
    color: var(--vp-c-brand-1);
}

.inline-item-text {
    font-size: 14px;
    font-weight: 500;
    color: inherit;
}

.inline-item-badge {
    font-size: 11px;
    padding: 2px 6px;
    border-radius: 4px;
    font-weight: 600;
    text-transform: uppercase;
}

.inline-item-badge.tip {
    background: var(--vp-c-tip-soft);
    color: var(--vp-c-tip-1);
}

.inline-item-badge.warning {
    background: var(--vp-c-warning-soft);
    color: var(--vp-c-warning-1);
}

/* Accordion transition */
.accordion-enter-active,
.accordion-leave-active {
    transition: opacity 0.2s ease, transform 0.2s ease;
}

.accordion-enter-from,
.accordion-leave-to {
    opacity: 0;
    transform: translateY(-8px);
}

/*******************************************************************************
 * DROPDOWN MODE STYLES (Desktop >= 1280px)
 ******************************************************************************/
.VPVersionSwitcher {
    position: relative;
    display: flex;
    align-items: center;
    order: 100;
}

.divider {
    width: 1px;
    height: 24px;
    background-color: var(--vp-c-divider);
    margin: 0 12px;
}

.button {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 4px 10px;
    border: 1px solid var(--vp-c-divider);
    border-radius: 6px;
    background: var(--vp-c-bg-soft);
    cursor: pointer;
    transition: all 0.25s;
    font-size: 13px;
    font-weight: 500;
    color: var(--vp-c-text-2);
}

.button:hover {
    border-color: var(--vp-c-brand-1);
    color: var(--vp-c-text-1);
}

.version-label {
    line-height: 1;
}

.chevron {
    transition: transform 0.2s;
}

.chevron.open {
    transform: rotate(180deg);
}

.flyout {
    position: absolute;
    top: calc(100% + 8px);
    right: 0;
    background: color-mix(in srgb, var(--vp-c-bg-elev) 70%, transparent);
    backdrop-filter: blur(12px);
    -webkit-backdrop-filter: blur(12px);
    border: 1px solid var(--vp-c-divider);
    border-radius: 12px;
    box-shadow: var(--vp-shadow-3);
    padding: 8px;
    z-index: 100;
    min-width: 160px;
}

.flyout-enter-active,
.flyout-leave-active {
    transition: opacity 0.2s, transform 0.2s;
}

.flyout-enter-from,
.flyout-leave-to {
    opacity: 0;
    transform: translateY(-8px);
}

.backdrop {
    position: fixed;
    inset: 0;
    z-index: 99;
}

.items {
    display: flex;
    flex-direction: column;
    gap: 2px;
}

.item {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px;
    cursor: pointer;
    transition: background-color 0.2s;
    border-radius: 6px;
    font-size: 14px;
    font-weight: 500;
    color: var(--vp-c-text-1);
}

.item:hover {
    background-color: var(--vp-c-default-soft);
}

.item.active {
    color: var(--vp-c-brand-1);
}

.text {
    flex: 1;
}

.badge {
    font-size: 11px;
    padding: 2px 6px;
    border-radius: 4px;
    font-weight: 600;
    text-transform: uppercase;
    min-width: 42px;
    text-align: center;
}

.badge.tip {
    background: var(--vp-c-tip-soft);
    color: var(--vp-c-tip-1);
}

.badge.warning {
    background: var(--vp-c-warning-soft);
    color: var(--vp-c-warning-1);
}

.check {
    font-size: 14px;
    color: var(--vp-c-brand-1);
    width: 16px;
    text-align: center;
}
</style>

<!-- Unscoped styles for teleported content into VPNavBarExtra menu -->
<style>
/*******************************************************************************
 * TELEPORT MODE STYLES (Tablet 768px - 1280px)
 * Copies exact styles from desktop .flyout dropdown
 ******************************************************************************/
.VPNavBarExtra .VPMenu .group.versions {
    margin: 0 -12px;
    padding: 12px 12px 8px;
    border-top: 1px solid var(--vp-c-divider);
}

.VPNavBarExtra .VPMenu .group.versions .group-title {
    padding: 0 12px 4px;
    font-size: 12px;
    font-weight: 500;
    color: var(--vp-c-text-2);
}

.VPNavBarExtra .VPMenu .group.versions .menu-item {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 14px;
    font-weight: 500;
    color: var(--vp-c-text-1);
    cursor: pointer;
    white-space: nowrap;
    transition: background-color 0.2s;
}

.VPNavBarExtra .VPMenu .group.versions .menu-item:hover {
    background-color: var(--vp-c-default-soft);
}

.VPNavBarExtra .VPMenu .group.versions .menu-item.active {
    color: var(--vp-c-brand-1);
}

.VPNavBarExtra .VPMenu .group.versions .menu-item-text {
    flex: 1;
}

.VPNavBarExtra .VPMenu .group.versions .menu-item-badge {
    font-size: 11px;
    padding: 2px 6px;
    border-radius: 4px;
    font-weight: 600;
    text-transform: uppercase;
    min-width: 42px;
    text-align: center;
}

.VPNavBarExtra .VPMenu .group.versions .menu-item-badge.tip {
    background: var(--vp-c-tip-soft);
    color: var(--vp-c-tip-1);
}

.VPNavBarExtra .VPMenu .group.versions .menu-item-badge.warning {
    background: var(--vp-c-warning-soft);
    color: var(--vp-c-warning-1);
}
</style>
