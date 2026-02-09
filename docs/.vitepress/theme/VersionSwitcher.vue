<script setup lang="ts">
import { ref, computed } from "vue";

interface Version {
    id: string;
    name: string;
    path: string;
    badge?: string;
    badgeType?: "tip" | "warning";
}

const versions: Version[] = [
    { id: "latest", name: "Latest", path: "/server/", badge: "unstable", badgeType: "warning" },
    { id: "preview", name: "Preview", path: "/server/preview/", badge: "unstable", badgeType: "warning" },
];

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
    <div class="VPVersionSwitcher" @click.stop>
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
.VPVersionSwitcher {
    position: relative;
    display: flex;
    align-items: center;
    order: 100; /* Position after theme selector, before social links */
}

.divider {
    width: 1px;
    height: 24px;
    background-color: var(--vp-c-divider);
    margin: 0 12px;
}

@media (max-width: 768px) {
    .divider {
        display: none;
    }
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
