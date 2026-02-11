<script setup lang="ts">
import { ref, computed, onMounted } from "vue";
import { useTheme } from "./useTheme";
import { useNavBarExtra } from "./useNavBarExtra";

defineProps<{
    /** When true, renders inline for mobile nav screen (no dropdown) */
    inline?: boolean;
}>();

const { themes, currentThemeId, setTheme, initTheme } = useTheme();
const { isMediumScreen, extraMenuTarget, isInlineOpen, toggleInline } = useNavBarExtra('__themeSelectorObserver');
const isOpen = ref(false);

const currentTheme = computed(() => {
    return themes.find((t) => t.id === currentThemeId.value) || themes[0];
});

function handleThemeChange(themeId: string) {
    setTheme(themeId);
}

function toggleDropdown() {
    isOpen.value = !isOpen.value;
}

function closeDropdown() {
    isOpen.value = false;
}

onMounted(() => {
    initTheme();
});
</script>

<template>
    <!-- INLINE MODE: For mobile nav-screen, collapsible accordion -->
    <div v-if="inline" class="VPThemeSelectorInline">
        <div class="inline-header" @click="toggleInline">
            <span class="inline-title">Color Theme</span>
            <svg class="inline-icon" :class="{ open: isInlineOpen }" xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <line v-if="!isInlineOpen" x1="12" y1="5" x2="12" y2="19" />
                <line x1="5" y1="12" x2="19" y2="12" />
            </svg>
        </div>
        <Transition name="accordion">
            <div v-if="isInlineOpen" class="inline-items">
                <div
                    v-for="theme in themes"
                    :key="theme.id"
                    class="inline-item"
                    :class="{ active: theme.id === currentThemeId }"
                    @click="handleThemeChange(theme.id)"
                >
                    <span
                        class="inline-color-dot"
                        :style="{
                            background: `linear-gradient(135deg, ${theme.colors.brand1} 0%, ${theme.colors.brand2} 50%, ${theme.colors.brand3} 100%)`,
                        }"
                    />
                    <span class="inline-item-text">{{ theme.name }}</span>
                </div>
            </div>
        </Transition>
    </div>

    <!-- TELEPORT MODE: For tablet/medium screens, inject into triple-dot menu -->
    <Teleport v-else-if="isMediumScreen && extraMenuTarget" :to="extraMenuTarget">
        <div class="group color-themes">
            <p class="group-title">Color Theme</p>
            <div
                v-for="theme in themes"
                :key="theme.id"
                class="menu-item"
                :class="{ active: theme.id === currentThemeId }"
                @click="handleThemeChange(theme.id)"
            >
                <span
                    class="menu-color-dot"
                    :style="{
                        background: `linear-gradient(135deg, ${theme.colors.brand1} 0%, ${theme.colors.brand2} 50%, ${theme.colors.brand3} 100%)`,
                    }"
                />
                <span class="menu-item-text">{{ theme.name }}</span>
            </div>
        </div>
    </Teleport>

    <!-- DROPDOWN MODE: For desktop (>= 1280px), show standalone dropdown -->
    <div v-else-if="!inline && !isMediumScreen" class="VPThemeSelector" @click.stop>
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
/*******************************************************************************
 * INLINE MODE STYLES (Mobile nav-screen)
 * Collapsible accordion matching VPNavScreenAppearance styling
 ******************************************************************************/
.VPThemeSelectorInline {
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
    gap: 12px;
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

.inline-color-dot {
    width: 16px;
    height: 16px;
    border-radius: 50%;
    border: 2px solid var(--vp-c-divider);
    flex-shrink: 0;
}

.inline-item-text {
    font-size: 14px;
    font-weight: 500;
    color: inherit;
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

<!-- Unscoped styles for teleported content into VPNavBarExtra menu -->
<style>
/*******************************************************************************
 * TELEPORT MODE STYLES (Tablet 768px - 1280px)
 ******************************************************************************/
.VPNavBarExtra .VPMenu .group.color-themes {
    margin: 0 -12px;
    padding: 12px 12px 8px;
    border-top: 1px solid var(--vp-c-divider);
}

.VPNavBarExtra .VPMenu .group.color-themes .group-title {
    padding: 0 12px 4px;
    font-size: 12px;
    font-weight: 500;
    color: var(--vp-c-text-2);
}

.VPNavBarExtra .VPMenu .group.color-themes .menu-item {
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

.VPNavBarExtra .VPMenu .group.color-themes .menu-item:hover {
    background-color: var(--vp-c-default-soft);
}

.VPNavBarExtra .VPMenu .group.color-themes .menu-item.active {
    color: var(--vp-c-brand-1);
}

.VPNavBarExtra .VPMenu .group.color-themes .menu-color-dot {
    width: 14px;
    height: 14px;
    border-radius: 50%;
    border: 2px solid var(--vp-c-divider);
    flex-shrink: 0;
}

.VPNavBarExtra .VPMenu .group.color-themes .menu-item-text {
    flex: 1;
}
</style>
