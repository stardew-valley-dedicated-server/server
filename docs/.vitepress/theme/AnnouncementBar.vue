<script setup lang="ts">
import { ref, onMounted } from "vue";
import DefaultTheme from "vitepress/theme";
import ThemeSelector from "./ThemeSelector.vue";

const { Layout } = DefaultTheme;

const announcementText = "🎉 Welcome to the new JunimoServer documentation!";
const announcementLink = "/guide";

const showAnnouncement = ref(false);

onMounted(() => {
    const isClosed = localStorage.getItem("announcement-closed") === "true";
    showAnnouncement.value = !isClosed;
    updateNavOffset();
});

function closeAnnouncement() {
    showAnnouncement.value = false;
    localStorage.setItem("announcement-closed", "true");
    updateNavOffset();
}

function updateNavOffset() {
    if (typeof document !== "undefined") {
        document.documentElement.style.setProperty(
            "--announcement-offset",
            showAnnouncement.value ? "42px" : "0px"
        );
    }
}
</script>

<template>
    <Layout>
        <template #layout-top>
            <div v-if="showAnnouncement" class="announcement-bar">
                <a :href="announcementLink" class="announcement-content">
                    {{ announcementText }}
                </a>
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
        </template>
        <template #nav-screen-content-after>
            <ThemeSelector />
        </template>
    </Layout>
</template>

<style>
:root {
    --announcement-height: 42px;
}

.announcement-bar {
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    height: var(--announcement-height);
    background: linear-gradient(
        90deg,
        var(--vp-c-brand-1) 0%,
        var(--vp-c-brand-3) 100%
    );
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
    text-decoration: none;
    flex: 1;
    text-align: center;
}

.announcement-content:hover {
    text-decoration: underline;
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
