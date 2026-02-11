import { ref, watch, onMounted, onUnmounted } from "vue";

/**
 * Composable for components that need to:
 * 1. Detect medium screen size (768px - 1280px) for VPNavBarExtra teleport
 * 2. Find the VPNavBarExtra menu target for teleporting content
 * 3. Handle inline accordion state for mobile nav-screen
 */
export function useNavBarExtra(observerKey: string) {
    const isMediumScreen = ref(false);
    const extraMenuTarget = ref<Element | null>(null);
    const isInlineOpen = ref(false);

    function checkScreenSize() {
        if (typeof window === "undefined") return;
        // Medium screen: 768px - 1280px (where VPNavBarExtra triple-dot menu is visible)
        isMediumScreen.value = window.innerWidth >= 768 && window.innerWidth < 1280;
    }

    function findExtraMenuTarget() {
        if (typeof window === "undefined") return;
        if (isMediumScreen.value) {
            extraMenuTarget.value = document.querySelector('.VPNavBarExtra .VPMenu');
        } else {
            extraMenuTarget.value = null;
        }
    }

    function toggleInline() {
        isInlineOpen.value = !isInlineOpen.value;
    }

    // Watch for medium screen changes and find target when it becomes true
    watch(isMediumScreen, (newVal) => {
        if (newVal) {
            // Use setTimeout to ensure DOM is ready
            setTimeout(findExtraMenuTarget, 100);
        } else {
            extraMenuTarget.value = null;
        }
    });

    onMounted(() => {
        checkScreenSize();
        findExtraMenuTarget();
        window.addEventListener('resize', checkScreenSize);

        // Watch for the menu to appear (it's lazy rendered)
        const observer = new MutationObserver(() => {
            if (isMediumScreen.value && !extraMenuTarget.value) {
                findExtraMenuTarget();
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });

        // Store observer for cleanup using unique key
        (window as any)[observerKey] = observer;
    });

    onUnmounted(() => {
        if (typeof window !== "undefined") {
            window.removeEventListener('resize', checkScreenSize);
            const observer = (window as any)[observerKey];
            if (observer) {
                observer.disconnect();
                delete (window as any)[observerKey];
            }
        }
    });

    return {
        isMediumScreen,
        extraMenuTarget,
        isInlineOpen,
        toggleInline,
    };
}
