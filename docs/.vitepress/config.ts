import { defineConfig } from "vitepress";

export default defineConfig({
    base: "/server/",
    title: "JunimoServer",
    description: "Stardew Valley dedicated server documentation",
    head: [
        ["link", { rel: "icon", href: "/server/logo.svg" }],
        ["meta", { property: "og:type", content: "website" }],
        ["meta", { property: "og:title", content: "JunimoServer" }],
        ["meta", { property: "og:description", content: "Stardew Valley dedicated server documentation" }],
        ["meta", { property: "og:image", content: "https://stardew-valley-dedicated-server.github.io/server/logo.svg" }],
        ["meta", { name: "twitter:card", content: "summary" }],
        ["meta", { name: "twitter:title", content: "JunimoServer" }],
        ["meta", { name: "twitter:description", content: "Stardew Valley dedicated server documentation" }],
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
                    { text: "Managing Mods", link: "/guide/managing-mods" },
                    { text: "Upgrading", link: "/guide/upgrading" },
                    { text: "Advanced Topics", link: "/guide/advanced-topics" },
                ],
            },
            {
                text: "Community",
                items: [
                    { text: "Getting Help", link: "/community/getting-help" },
                    { text: "Reporting Bugs", link: "/community/reporting-bugs" },
                    { text: "Contributing", link: "/community/contributing" },
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
            pattern: "https://github.com/stardew-valley-dedicated-server/server/edit/master/website/docs/:path",
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
});
