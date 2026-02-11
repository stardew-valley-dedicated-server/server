<script setup lang="ts">
import { ref, onMounted } from "vue";
import { withBase } from "vitepress";
import DefaultTheme from "vitepress/theme";
import ThemeSelector from "./ThemeSelector.vue";
import VersionSwitcher from "./VersionSwitcher.vue";

const { Layout } = DefaultTheme;

const announcementLink = withBase("/admins/operations/upgrading#preview-builds");

// Check localStorage immediately if available (for SSR, default to true so CSS offset is applied)
function getInitialState(): boolean {
    if (typeof localStorage === "undefined") return true;
    try {
        return localStorage.getItem("announcement-closed") !== "true";
    } catch {
        return true;
    }
}

const showAnnouncement = ref(getInitialState());

onMounted(() => {
    // Re-check on mount in case SSR state differs
    const isClosed = localStorage.getItem("announcement-closed") === "true";
    showAnnouncement.value = !isClosed;
});

function closeAnnouncement() {
    showAnnouncement.value = false;
    localStorage.setItem("announcement-closed", "true");
    document.documentElement.style.setProperty("--announcement-offset", "0px");
}
</script>

<template>
    <Layout>
        <template #layout-top>
            <div v-if="showAnnouncement" class="announcement-bar">
                <span class="announcement-content">
                    ⚠️ The latest release is unstable — use
                    <a :href="announcementLink" class="announcement-link"
                        >preview builds</a
                    >
                    instead
                </span>
                <button
                    class="announcement-close"
                    @click="closeAnnouncement"
                    aria-label="Close announcement"
                >
                    ✕
                </button>
            </div>
        </template>
        <template #nav-bar-content-after>
            <ThemeSelector />
            <VersionSwitcher />
        </template>
        <template #nav-screen-content-after>
            <ThemeSelector :inline="true" />
            <VersionSwitcher :inline="true" />
        </template>
    </Layout>
</template>

<style>
:root {
    --announcement-height: 42px;
}

/* Hide version switcher and theme selector in navbar on mobile - they appear in nav-screen instead */
@media (max-width: 767px) {
    .VPNavBar .VPVersionSwitcher,
    .VPNavBar .VPThemeSelector {
        display: none !important;
    }
}

/* Position custom accordions between Appearance and social links in mobile nav */
.VPNavScreen > .container {
    display: flex;
    flex-direction: column;
}

.VPNavScreen > .container > .VPNavScreenMenu {
    order: 1;
}

.VPNavScreen > .container > .VPNavScreenAppearance {
    order: 2;
}

.VPNavScreen > .container > .VPThemeSelectorInline {
    order: 3;
}

.VPNavScreen > .container > .VPVersionSwitcherInline {
    order: 4;
}

.VPNavScreen > .container > .VPNavScreenSocialLinks {
    order: 5;
}

.announcement-bar {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    height: var(--announcement-height);
    background: linear-gradient(90deg, #d97706 0%, #b45309 100%);
    z-index: 100;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0 1rem;
}

.announcement-content {
    color: white;
    font-size: 0.9rem;
    font-weight: 500;
    flex: 1;
    text-align: center;
}

.announcement-link {
    color: #93c5fd;
    text-decoration: underline;
}

.announcement-link:hover {
    color: #bfdbfe;
}

.announcement-close {
    background: none;
    border: none;
    color: white;
    font-size: 1.25rem;
    cursor: pointer;
    padding: 0.25rem 0.5rem;
    line-height: 1;
    opacity: 0.8;
    transition: opacity 0.2s;
}

.announcement-close:hover {
    opacity: 1;
}
</style>
