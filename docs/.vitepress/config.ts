import { defineConfig } from "vitepress";
import { withMermaid } from "vitepress-plugin-mermaid";
import { useSidebar } from "vitepress-openapi";
import { groupIconVitePlugin } from "vitepress-plugin-group-icons";
import spec from "../assets/openapi.json" with { type: "json" };

// Docs version: "latest" or "preview" (set via DOCS_VERSION env var during build)
const docsVersion = process.env.DOCS_VERSION || "latest";
const isPreview = docsVersion === "preview";
const base = isPreview ? "/server/preview/" : "/server/";

const openApiSidebar = useSidebar({ spec, linkPrefix: "/developers/api/" });

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
            { text: "Players", link: "/players/" },
            { text: "Admins", link: "/admins/" },
            { text: "Features", link: "/features/" },
            { text: "Developers", link: "/developers/" },
            { text: "Community", link: "/community/" },
        ],

        notFound: {
            title: "Page Not Found",
            quote: "Looks like this page wandered off to the mines...",
            linkText: "Return to farm",
        },

        sidebar: {
            "/players/": [
                {
                    text: "Players",
                    items: [
                        { text: "Joining a Server", link: "/players/joining" },
                        { text: "Gameplay Differences", link: "/players/playing" },
                        { text: "Chat Commands", link: "/players/commands" },
                        { text: "Troubleshooting", link: "/players/troubleshooting" },
                    ],
                },
            ],
            "/admins/": [
                {
                    text: "Quick Start",
                    items: [
                        { text: "Overview", link: "/admins/" },
                        { text: "Prerequisites", link: "/admins/quick-start/prerequisites" },
                        { text: "Installation", link: "/admins/quick-start/installation" },
                        { text: "First Setup", link: "/admins/quick-start/first-setup" },
                    ],
                },
                {
                    text: "Configuration",
                    items: [
                        { text: "Overview", link: "/admins/configuration/" },
                        { text: "Server Settings", link: "/admins/configuration/server-settings" },
                        { text: "Environment Variables", link: "/admins/configuration/environment" },
                        { text: "Discord Setup", link: "/admins/configuration/discord" },
                    ],
                },
                {
                    text: "Operations",
                    items: [
                        { text: "Overview", link: "/admins/operations/" },
                        { text: "Commands", link: "/admins/operations/commands" },
                        { text: "Networking", link: "/admins/operations/networking" },
                        { text: "Upgrading", link: "/admins/operations/upgrading" },
                        { text: "VNC (Advanced)", link: "/admins/operations/vnc" },
                    ],
                },
                {
                    text: "Troubleshooting",
                    items: [
                        { text: "Common Issues", link: "/admins/troubleshooting" },
                    ],
                },
            ],
            "/features/": [
                {
                    text: "Features",
                    items: [
                        { text: "Overview", link: "/features/" },
                    ],
                },
                {
                    text: "Security",
                    items: [
                        {
                            text: "Password Protection",
                            collapsed: false,
                            items: [
                                { text: "Overview", link: "/features/password-protection/" },
                                { text: "Lobby Layouts", link: "/features/password-protection/lobby-layouts" },
                                { text: "Commands", link: "/features/password-protection/commands" },
                                { text: "Security Details", link: "/features/password-protection/security" },
                            ],
                        },
                    ],
                },
                {
                    text: "Gameplay",
                    items: [
                        { text: "Cabin Strategies", link: "/features/cabin-strategies" },
                        { text: "Server Mechanics", link: "/features/server-mechanics" },
                    ],
                },
                {
                    text: "Multiplayer",
                    items: [
                        { text: "Cross-Platform", link: "/features/cross-platform" },
                        { text: "Mod Support", link: "/features/mods" },
                    ],
                },
                {
                    text: "Integration",
                    items: [
                        { text: "Discord Bot", link: "/features/discord" },
                        { text: "REST API", link: "/features/rest-api" },
                    ],
                },
                {
                    text: "Administration",
                    items: [
                        { text: "Backup & Recovery", link: "/features/backup" },
                    ],
                },
            ],
            "/developers/": [
                {
                    text: "Developers",
                    items: [
                        { text: "Overview", link: "/developers/" },
                    ],
                },
                {
                    text: "REST API",
                    collapsed: false,
                    items: [
                        { text: "Introduction", link: "/developers/api/introduction" },
                        ...openApiSidebar.generateSidebarGroups().map(group => ({
                            ...group,
                            collapsed: true,
                        })),
                    ],
                },
                {
                    text: "Architecture",
                    items: [
                        { text: "Steam Auth", link: "/developers/architecture/steam-auth" },
                        { text: "Networking Internals", link: "/developers/architecture/networking" },
                    ],
                },
                {
                    text: "Contributing",
                    items: [
                        { text: "Development Setup", link: "/developers/contributing/" },
                        { text: "CI/CD Pipelines", link: "/developers/contributing/ci-cd" },
                    ],
                },
                {
                    text: "Advanced",
                    items: [
                        { text: "Building from Source", link: "/developers/advanced/building-from-source" },
                        { text: "Decompiling", link: "/developers/advanced/decompiling" },
                    ],
                },
            ],
            "/community/": [
                {
                    text: "Community",
                    items: [
                        { text: "Overview", link: "/community/" },
                        { text: "FAQ", link: "/community/faq" },
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
        },

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
