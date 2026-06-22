import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vitepress";
import { useSidebar } from "vitepress-openapi";
import { groupIconVitePlugin } from "vitepress-plugin-group-icons";
import { withMermaid } from "vitepress-plugin-mermaid";
import spec from "../assets/openapi.json" with { type: "json" };
import { DEFAULT_THEME_ID, themes } from "./theme/themes";

// Docs version: "latest" or "preview" (set via DOCS_VERSION env var during build)
const docsVersion = process.env.DOCS_VERSION || "latest";
const isPreview = docsVersion === "preview";
const base = isPreview ? "/server/preview/" : "/server/";

const origin = "https://stardew-valley-dedicated-server.github.io";
const ogImage = `${origin}${base}og-image.png`;

const openApiSidebar = useSidebar({ spec, linkPrefix: "/developers/api/" });

// Single source of truth for the FOUC pre-paint script: derive the {id: colors}
// map from themes.ts so it can't drift from the runtime ThemeSelector.
const foucThemeMap = JSON.stringify(Object.fromEntries(themes.map((t) => [t.id, t.colors])));

export default withMermaid(
    defineConfig({
        vite: {
            // Bake the build time into the bundle so the sidebar can show "Last built: …"
            define: {
                __BUILD_TIME__: JSON.stringify(new Date().toISOString()),
            },
            resolve: {
                alias: [
                    {
                        // Swap VitePress's locale-dependent "Last updated" for our fixed DD.MM.YY HH:MM format.
                        find: /^.*\/VPDocFooterLastUpdated\.vue$/,
                        replacement: fileURLToPath(new URL("./theme/LastUpdated.vue", import.meta.url)),
                    },
                ],
            },
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
        // _partials/ holds reusable markdown fragments pulled in via <!--@include-->. They are not
        // standalone pages, so keep them out of routing, search, and the sitemap. README.md is the
        // contributor build-guide (not a user-facing doc), so exclude it too — otherwise it routes
        // as /README.html and lands in the sitemap.
        srcExclude: ["**/_partials/**", "README.md"],
        title: isPreview ? "JunimoServer (Preview)" : "JunimoServer",
        description: "A Docker-based dedicated server for Stardew Valley, run as a SMAPI mod.",
        head: [
            ["link", { rel: "icon", href: `${base}logo.svg` }],
            // Page-invariant social tags. Per-page og/twitter title+description+url and
            // canonical are emitted by transformHead (below), which replaces any matching
            // tag here via VitePress's mergeHead.
            ["meta", { property: "og:type", content: "website" }],
            ["meta", { property: "og:site_name", content: "JunimoServer" }],
            ["meta", { property: "og:locale", content: "en_US" }],
            ["meta", { property: "og:image", content: ogImage }],
            ["meta", { property: "og:image:width", content: "1200" }],
            ["meta", { property: "og:image:height", content: "630" }],
            [
                "meta",
                {
                    property: "og:image:alt",
                    content: "JunimoServer documentation — a Docker-based Stardew Valley dedicated server",
                },
            ],
            ["meta", { name: "twitter:card", content: "summary_large_image" }],
            ["meta", { name: "twitter:image", content: ogImage }],
            ["meta", { name: "theme-color", content: "#63dbe4" }],
            // Inline script to prevent FOUC for theme and announcement bar
            [
                "script",
                {},
                `
(function() {
    // Theme colors map — generated from themes.ts at build time (see foucThemeMap).
    var themes = ${foucThemeMap};
    var defaultTheme = ${JSON.stringify(DEFAULT_THEME_ID)};

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
        `,
            ],
        ],
        // Per-page social/SEO tags. `title`/`description` here are already resolved
        // (page value, falling back to the site title/description), so the home page
        // and description-less pages get sensible values. These tags replace the
        // page-invariant placeholders in `head` via mergeHead (property/name must be
        // the first attr key for the dedup to match).
        transformHead({ pageData, siteConfig, title, description }) {
            if (pageData.isNotFound) {
                return [];
            }
            // Match the canonical/og:url suffix to how the page is actually served:
            // cleanUrls strips the .html extension, otherwise it's kept (VitePress default).
            const suffix = siteConfig.cleanUrls ? "" : ".html";
            const path = pageData.relativePath.replace(/(^|\/)index\.md$/, "$1").replace(/\.md$/, suffix);
            const url = `${origin}${base}${path}`;
            // `title` arrives templated as "Page | JunimoServer" (VitePress's default
            // titleTemplate). og:site_name already carries the brand, so the social
            // title uses the page's own title alone — no " | JunimoServer" echo, and
            // no bare "JunimoServer / JunimoServer" duplicate on the home page (whose
            // frontmatter title is "Documentation"). The <title> tag stays templated.
            const socialTitle = pageData.title || siteConfig.site.title;
            return [
                ["meta", { property: "og:title", content: socialTitle }],
                ["meta", { property: "og:description", content: description }],
                ["meta", { property: "og:url", content: url }],
                ["link", { rel: "canonical", href: url }],
                ["meta", { name: "twitter:title", content: socialTitle }],
                ["meta", { name: "twitter:description", content: description }],
            ];
        },
        lastUpdated: true,
        sitemap: {
            // Include the base path: VitePress feeds each page's relative URL to the
            // sitemap as `new URL(relativeUrl, hostname)`, so the hostname must carry
            // the deploy subpath (and its trailing slash — without it `new URL` resolves
            // against the parent and drops the base). `base` already differs per deploy
            // (/server/ vs /server/preview/), keeping sitemap locs aligned with the
            // og:url/canonical tags, which also use `${origin}${base}`.
            hostname: `${origin}${base}`,
        },

        themeConfig: {
            logo: "/logo.svg",

            nav: [
                { text: "Home", link: "/" },
                { text: "Players", link: "/players/", activeMatch: "^/players/" },
                { text: "Admins", link: "/admins/", activeMatch: "^/admins/" },
                { text: "Features", link: "/features/", activeMatch: "^/features/" },
                { text: "Developers", link: "/developers/", activeMatch: "^/developers/" },
                { text: "Community", link: "/community/", activeMatch: "^/community/" },
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
                            { text: "Importing Saves", link: "/admins/operations/importing-saves" },
                            { text: "Networking", link: "/admins/operations/networking" },
                            { text: "Upgrading", link: "/admins/operations/upgrading" },
                            { text: "VNC (Advanced)", link: "/admins/operations/vnc" },
                            { text: "Modern Docker Image", link: "/admins/operations/modern-docker" },
                        ],
                    },
                    {
                        text: "Troubleshooting",
                        items: [{ text: "Common Issues", link: "/admins/troubleshooting" }],
                    },
                ],
                "/features/": [
                    {
                        text: "Features",
                        items: [{ text: "Overview", link: "/features/" }],
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
                        items: [{ text: "Backup & Recovery", link: "/features/backup" }],
                    },
                ],
                "/developers/": [
                    {
                        text: "Developers",
                        items: [{ text: "Overview", link: "/developers/" }],
                    },
                    {
                        text: "REST API",
                        collapsed: false,
                        items: [
                            { text: "Introduction", link: "/developers/api/introduction" },
                            ...openApiSidebar.generateSidebarGroups().map((group) => ({
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
                            { text: "Game Engine Reference", link: "/developers/architecture/game-engine-notes" },
                            { text: "Mod Architecture", link: "/developers/architecture/mod-architecture" },
                            { text: "Festival Handling", link: "/developers/architecture/festival-handling" },
                            { text: "Events Schema", link: "/developers/events-schema" },
                        ],
                    },
                    {
                        text: "Testing",
                        items: [
                            { text: "E2E Testing", link: "/developers/testing/e2e-testing" },
                            { text: "Test Harness Reference", link: "/developers/testing/test-harness" },
                            { text: "Remote Host Setup", link: "/developers/testing/remote-host-setup" },
                            { text: "Manual Festival Testing", link: "/developers/testing/festivals-manual" },
                            { text: "Test Failure Runbook", link: "/developers/testing/test-failure-runbook" },
                            { text: "CI Log Masking Runbook", link: "/developers/testing/ci-log-masking-runbook" },
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
                            {
                                text: "Client Manipulation Techniques",
                                link: "/developers/advanced/client-manipulation-techniques",
                            },
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
    }),
);
