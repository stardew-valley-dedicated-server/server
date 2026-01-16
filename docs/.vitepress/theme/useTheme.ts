import { ref, watch } from "vue";
import { themes, DEFAULT_THEME_ID, type Theme } from "./themes";

const STORAGE_KEY = "vitepress-theme-preference";

const currentThemeId = ref<string>(DEFAULT_THEME_ID);

function applyTheme(theme: Theme) {
    const root = document.documentElement;
    root.style.setProperty("--vp-c-brand-1", theme.colors.brand1);
    root.style.setProperty("--vp-c-brand-2", theme.colors.brand2);
    root.style.setProperty("--vp-c-brand-3", theme.colors.brand3);

    // Dispatch a custom event to notify components of theme change
    window.dispatchEvent(new CustomEvent("theme-changed", {
        detail: { theme }
    }));
}

function loadTheme(): string {
    if (typeof window === "undefined") {
        return DEFAULT_THEME_ID;
    }

    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored && themes.some((t) => t.id === stored)) {
            return stored;
        }
    } catch (e) {
        console.warn("Failed to load theme preference:", e);
    }

    return DEFAULT_THEME_ID;
}

function saveTheme(themeId: string) {
    if (typeof window === "undefined") {
        return;
    }

    try {
        localStorage.setItem(STORAGE_KEY, themeId);
    } catch (e) {
        console.warn("Failed to save theme preference:", e);
    }
}

export function useTheme() {
    function setTheme(themeId: string) {
        const theme = themes.find((t) => t.id === themeId);
        if (!theme) {
            console.warn(`Theme not found: ${themeId}`);
            return;
        }

        currentThemeId.value = themeId;
        applyTheme(theme);
        saveTheme(themeId);
    }

    function initTheme() {
        const themeId = loadTheme();
        setTheme(themeId);
    }

    return {
        themes,
        currentThemeId,
        setTheme,
        initTheme,
    };
}
