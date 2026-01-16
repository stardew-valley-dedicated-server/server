<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from "vue";
import { useTheme } from "./useTheme";

const { themes, currentThemeId, setTheme, initTheme } = useTheme();
const isOpen = ref(false);
const themeSelectorRef = ref<HTMLElement | null>(null);

onMounted(() => {
    initTheme();

    // Move the theme selector next to the appearance toggle
    const moveToAppearance = () => {
        const appearanceButton = document.querySelector('.VPSwitch.VPSwitchAppearance');
        if (appearanceButton && themeSelectorRef.value) {
            const container = appearanceButton.parentElement;
            if (container) {
                // Insert after the appearance toggle
                container.insertBefore(themeSelectorRef.value, appearanceButton.nextSibling);
            }
        }
    };

    // Try immediately and after a short delay to handle dynamic rendering
    moveToAppearance();
    setTimeout(moveToAppearance, 100);
    setTimeout(moveToAppearance, 500);

    // Watch for route changes (in case VitePress re-renders the nav)
    const observer = new MutationObserver(() => {
        if (!document.querySelector('.VPThemeSelector')) {
            moveToAppearance();
        }
    });

    observer.observe(document.body, { childList: true, subtree: true });

    onUnmounted(() => {
        observer.disconnect();
    });
});

const currentTheme = computed(() => {
    return themes.find((t) => t.id === currentThemeId.value) || themes[0];
});

function handleThemeChange(themeId: string) {
    setTheme(themeId);
    // Don't close the dropdown, let users pick multiple themes to compare
}

function toggleDropdown() {
    isOpen.value = !isOpen.value;
}

function closeDropdown() {
    isOpen.value = false;
}
</script>

<template>
    <div ref="themeSelectorRef" class="VPThemeSelector" @click.stop>
        <button
            type="button"
            class="button"
            :title="`Theme: ${currentTheme.name}`"
            @click="toggleDropdown"
            :style="{
                background: `linear-gradient(135deg, ${currentTheme.colors.brand1} 0%, ${currentTheme.colors.brand2} 50%, ${currentTheme.colors.brand3} 100%)`,
            }"
        />

        <Transition name="flyout">
            <div v-if="isOpen" class="flyout" @click.stop>
                <div class="items">
                    <div
                        v-for="theme in themes"
                        :key="theme.id"
                        class="item"
                        :class="{ active: theme.id === currentThemeId }"
                        @click="handleThemeChange(theme.id)"
                    >
                        <div class="color-preview">
                            <span
                                class="color-dot"
                                :style="{
                                    background: `linear-gradient(135deg, ${theme.colors.brand1} 0%, ${theme.colors.brand2} 50%, ${theme.colors.brand3} 100%)`,
                                }"
                            />
                        </div>
                        <span class="text">{{ theme.name }}</span>
                        <span v-if="theme.id === currentThemeId" class="check">✓</span>
                    </div>
                </div>
            </div>
        </Transition>

        <div v-if="isOpen" class="backdrop" @click="closeDropdown" />
    </div>
</template>

<style scoped>
.VPThemeSelector {
    position: relative;
    display: flex;
    align-items: center;
    margin-left: 8px;
}

.button {
    width: 20px;
    height: 20px;
    border: 2px solid var(--vp-c-divider);
    border-radius: 50%;
    cursor: pointer;
    transition: all 0.25s;
    padding: 0;
    flex-shrink: 0;
}

.button:hover {
    border-color: var(--vp-c-text-1);
    transform: scale(1.15);
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
    min-width: 200px;
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
    gap: 12px;
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

.color-preview {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 20px;
    height: 20px;
}

.color-dot {
    width: 16px;
    height: 16px;
    border-radius: 50%;
    border: 2px solid var(--vp-c-divider);
}

.text {
    flex: 1;
}

.check {
    font-size: 16px;
    color: var(--vp-c-brand-1);
}
</style>
