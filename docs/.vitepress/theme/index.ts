import type { Theme } from "vitepress";
import DefaultTheme from "vitepress/theme";
import AnnouncementBar from "./AnnouncementBar.vue";
import { initRandomIconAnimation } from "./randomIconAnimation";
// import AnimatedIcon from "./web-components/animated-icon";
import "./custom.css";

// Custom elements work inside markdown frontmatter, vue components don't
// customElements.define("animated-icon", AnimatedIcon);

export default {
    extends: DefaultTheme,
    Layout: AnnouncementBar,
    async enhanceApp({ app }) {
        // if (typeof window !== "undefined") {
        //     initRandomIconAnimation();
        // }

        // lazy-load the class only in the browser
        // import("./web-components/animated-icon").then(({ default: AnimatedIcon }) => {
        //     if (!customElements.get("animated-icon")) {
        //         customElements.define("animated-icon", AnimatedIcon);
        //     }
        // });

        if (!import.meta.env.SSR) {
            const AnimatedIcon = (await import("./web-components/animated-icon")).default;
            customElements.define("animated-icon", AnimatedIcon);

            initRandomIconAnimation();
        }
    },
} satisfies Theme;
