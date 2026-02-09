import { defineConfig } from "vitepress";
import { withMermaid } from "vitepress-plugin-mermaid";
import { useSidebar } from "vitepress-openapi";
import { groupIconVitePlugin } from "vitepress-plugin-group-icons";
import spec from "../assets/openapi.json" with { type: "json" };

// Docs version: "latest" or "preview" (set via DOCS_VERSION env var during build)
const docsVersion = process.env.DOCS_VERSION || "latest";
const isPreview = docsVersion === "preview";
const base = isPreview ? "/server/preview/" : "/server/";

const openApiSidebar = useSidebar({ spec, linkPrefix: "/api/" });

export default withMermaid(defineConfig({
    vite: {
        plugins: [
            groupIconVitePlugin({
                // Add custom icons which are not available otherwise
                customIcon: {
                    curl: "simple-icons:curl",
                    ".cs": "vscode-icons:file-type-csharp2",
                },
                // Set default labels for code blocks (labels for API samples are defined separately in `theme/index.ts`)
                defaultLabels: ["curl", ".cs", ".ts", ".py"],
            }),
        ],
    },
    base,
    title: isPreview ? "JunimoServer (Preview)" : "JunimoServer",
    description: "Stardew Valley dedicated server documentation",
    head: [
        ["link", { rel: "icon", href: `${base}logo.svg` }],
        ["meta", { property: "og:type", content: "website" }],
        ["meta", { property: "og:title", content: "JunimoServer" }],
        ["meta", { property: "og:description", content: "Stardew Valley dedicated server documentation" }],
        ["meta", { property: "og:image", content: "https://stardew-valley-dedicated-server.github.io/server/logo.svg" }],
        ["meta", { name: "twitter:card", content: "summary" }],
        ["meta", { name: "twitter:title", content: "JunimoServer" }],
        ["meta", { name: "twitter:description", content: "Stardew Valley dedicated server documentation" }],
        // Inline script to prevent FOUC for theme and announcement bar
        ["script", {}, `
(function() {
    // Theme colors map (must match themes.ts)
    var themes = {
        "aqua-gold": { brand1: "#63dbe4ff", brand2: "#25ac8aff", brand3: "#dda122ff" },
        "blue-green": { brand1: "#0571d7ff", brand2: "#2b7eb8ff", brand3: "#25ac8aff" },
        "blue-deep": { brand1: "#0571d7ff", brand2: "#0b4373ff", brand3: "#0a2969ff" },
        "green": { brand1: "#066636ff", brand2: "#25ac8aff", brand3: "#39c63cff" },
        "night-market": { brand1: "#281075ff", brand2: "#41b824ff", brand3: "#420375ff" },
        "purple-1": { brand1: "#9370db", brand2: "#6e2bff", brand3: "#34327a" },
        "purple-2": { brand1: "#9370db", brand2: "#A014DC", brand3: "#34327a" }
    };
    var defaultTheme = "purple-1";

    try {
        // Apply saved theme immediately
        var savedTheme = localStorage.getItem("vitepress-theme-preference");
        var theme = themes[savedTheme] || themes[defaultTheme];
        if (theme) {
            document.documentElement.style.setProperty("--vp-c-brand-1", theme.brand1);
            document.documentElement.style.setProperty("--vp-c-brand-2", theme.brand2);
            document.documentElement.style.setProperty("--vp-c-brand-3", theme.brand3);
        }

        // Apply announcement offset if not closed
        var announcementClosed = localStorage.getItem("announcement-closed") === "true";
        document.documentElement.style.setProperty("--announcement-offset", announcementClosed ? "0px" : "42px");
    } catch (e) {}
})();
        `],
    ],
    lastUpdated: true,
    sitemap: {
        hostname: "https://stardew-valley-dedicated-server.github.io",
    },

    themeConfig: {
        logo: "/logo.svg",

        nav: [
            { text: "Home", link: "/" },
            { text: "Guide", link: "/getting-started/introduction" },
            { text: "Community", link: "/community/getting-help" },
        ],

        notFound: {
            title: "Page Not Found",
            quote: "Looks like this page wandered off to the mines...",
            linkText: "Return to farm",
        },

        sidebar: [
            {
                text: "Getting Started",
                items: [
                    { text: "Introduction", link: "/getting-started/introduction" },
                    { text: "Prerequisites", link: "/getting-started/prerequisites" },
                    { text: "Installation", link: "/getting-started/installation" },
                    { text: "Configuration", link: "/getting-started/configuration" },
                    { text: "Authentication", link: "/getting-started/auth" },
                    { text: "FAQ", link: "/getting-started/faq" },
                ],
            },
            {
                text: "Guide",
                items: [
                    { text: "Using the Server", link: "/guide/using-the-server" },
                    {
                        text: "Password Protection",
                        collapsed: false,
                        items: [
                            { text: "Overview", link: "/guide/password-protection/" },
                            { text: "Lobby Layouts", link: "/guide/password-protection/lobby-layouts" },
                            { text: "Commands", link: "/guide/password-protection/commands" },
                            { text: "Security & Config", link: "/guide/password-protection/security" },
                        ],
                    },
                    { text: "Networking", link: "/guide/networking" },
                    { text: "Managing Mods", link: "/guide/managing-mods" },
                    { text: "Upgrading", link: "/guide/upgrading" },
                    { text: "CI/CD Pipelines", link: "/guide/ci-cd" },
                    {
                        text: "REST API",
                        collapsed: false,
                        items: [
                            { text: "Introduction", link: "/api/introduction" },
                            ...openApiSidebar.generateSidebarGroups().map(group => ({
                                ...group,
                                collapsed: true,
                            })),
                        ],
                    },
                    { text: "Advanced Topics", link: "/guide/advanced-topics" },
                ],
            },
            {
                text: "Community",
                items: [
                    { text: "Getting Help", link: "/community/getting-help" },
                    { text: "Reporting Bugs", link: "/community/reporting-bugs" },
                    { text: "Contributing", link: "/community/contributing" },
                    { text: "Resources", link: "/community/resources" },
                    { text: "Roadmap", link: "/community/roadmap" },
                    { text: "Changelog", link: "/community/changelog" },
                    { text: "Contributors", link: "/community/contributors" },
                ],
            },
        ],

        socialLinks: [
            { icon: "github", link: "https://github.com/stardew-valley-dedicated-server/server" },
            { icon: "discord", link: "https://discord.gg/w23GVXdSF7" },
        ],

        search: {
            provider: "local",
        },

        editLink: {
            pattern: "https://github.com/stardew-valley-dedicated-server/server/edit/master/docs/:path",
            text: "Edit this page on GitHub",
        },

        footer: {
            message: "Released under the MIT License.",
            copyright: "Copyright © 2024-present JunimoServer Contributors",
        },

        outline: {
            level: [2, 3],
            label: "On this page",
        },
    },
}));
