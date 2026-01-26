import type { Theme } from "vitepress";
import DefaultTheme from "vitepress/theme";
import AnnouncementBar from "./AnnouncementBar.vue";
import { initRandomIconAnimation } from "./randomIconAnimation";
import { theme as openapiTheme, useTheme as useOpenapiTheme, useOpenapi } from "vitepress-openapi/client";
import "vitepress-openapi/dist/style.css";
import "virtual:group-icons.css";
import "./custom.css";
import spec from '../../assets/openapi.json';

export default {
    extends: DefaultTheme,
    Layout: AnnouncementBar,
    async enhanceApp({ app }) {
        // TODO: Show for local builds, but hide for public deployments
        const hideTryItOutButton = true;

        // Note: Calling it here is the recommended way, but triggers console warning which can be ignored?
        // `[Vue warn]: inject() can only be used inside setup() or functional components.`
        useOpenapi({
            spec: spec as any,
            config: {
                spec: {
                    lazyRendering: true,
                    wrapExamples: false,
                },
                schemaViewer: {
                    deep: 2,
                },
                jsonViewer: {
                    renderer: 'shiki',
                },
                codeSamples: {
                    availableLanguages: [
                        ...useOpenapiTheme().getCodeSamplesAvailableLanguages(["curl", "python"]),
                        { lang: "csharp", label: "C#", highlighter: "csharp", icon: ".cs", target: "csharp", client: "httpclient"},
                        { lang: "ts1", label: "TS Fetch", highlighter: "typescript", icon: ".ts", target: "js", client: "fetch"},
                        { lang: "ts2", label: "TS Axios", highlighter: "typescript", icon: ".ts", target: "js", client: "axios"},
                    ],
                },
                operation: {
                    hiddenSlots: hideTryItOutButton ? ['playground'] : [],
                    cols: 1,
                }
            }
        });
        openapiTheme.enhanceApp({ app });

        if (!import.meta.env.SSR) {
            const AnimatedIcon = (await import("./web-components/animated-icon")).default;
            customElements.define("animated-icon", AnimatedIcon);

            initRandomIconAnimation();
        }
    },
} satisfies Theme;
