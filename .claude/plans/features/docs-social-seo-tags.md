# Docs: rich link previews, social cards & SEO metadata

## Goal

Every VitePress docs page emits correct, per-page Open Graph / Twitter / SEO meta so that links shared on Discord, Twitter/X, Slack, iMessage, etc. render a rich preview (title + description + image + URL), and search engines get clean canonical/title/description signals.

## Current state (assessed)

All social/SEO config lives in `docs/.vitepress/config.ts` `head: [...]` and is **static and site-wide**:

- `og:type=website`, `og:title=JunimoServer`, `og:description="Stardew Valley dedicated server documentation"`, `og:image=…/server/logo.svg`.
- `twitter:card=summary`, `twitter:title`, `twitter:description` (same static strings).
- `<link rel=icon href=…/logo.svg>`.
- `sitemap.hostname` is set; `lastUpdated: true`.

Gaps:

1. **Every page shares the same OG title/description.** A shared link to `/admins/quick-start/installation` previews as "JunimoServer / Stardew Valley dedicated server documentation" — no page-specific title or description. This is the biggest miss.
2. **`og:image` is an SVG.** Discord, Twitter, Facebook, iMessage, LinkedIn do **not** render SVG OG images — they silently show no image. Needs a PNG/JPG.
3. **No `og:url` / `<link rel=canonical>`.** No per-page canonical URL; weakens SEO and some scrapers want `og:url`.
4. **`twitter:card=summary`** is the small-square card. With a proper wide image, `summary_large_image` gives the big banner preview.
5. **No `og:image:width/height`, `og:image:alt`, `og:site_name`, `twitter:image`.** Missing `twitter:image` means Twitter falls back to nothing even though OG image exists in some scrapers.
6. **No `keywords`/author or `theme-color`** (minor).
7. **`titleTemplate`** is not set, so the `<title>` is VitePress default (`Page Title | JunimoServer`) — acceptable, but the *OG* title doesn't track it at all (it's hardcoded).
8. No page uses per-page frontmatter to override any of this (verified: zero real `og:`/`head:` frontmatter across `docs/**/*.md`).

## Mechanism (verified against installed VitePress types)

`defineConfig` supports a build-time hook:

```ts
transformHead(ctx: TransformContext): HeadConfig[] | void
```

`TransformContext` exposes (`docs/node_modules/vitepress/dist/node/index.d.ts:2186`):
- `ctx.pageData.relativePath` — e.g. `admins/quick-start/installation.md`
- `ctx.pageData.frontmatter` — per-page frontmatter (for overrides)
- `ctx.title` — resolved page title
- `ctx.description` — resolved page description (frontmatter `description` → site description fallback)

This runs per page at SSG build time, so we can compute canonical URL + per-page OG/Twitter tags and **append** them to the static head. This is the clean VitePress-native approach — no theme component hacks, no client-side JS.

Site origin (confirmed from `sitemap.hostname` + `base` + deploy-docs.yml):
- latest: `https://stardew-valley-dedicated-server.github.io/server/`
- preview: `https://stardew-valley-dedicated-server.github.io/server/preview/`

## Plan

### 1. Create a proper OG image asset (1200×630 PNG)

- Add `docs/public/og-image.png` — 1200×630 (the universal OG/Twitter `summary_large_image` size), JunimoServer logo + wordmark + tagline on a branded background. Existing `logo.png` is 1024×1024 (square, wrong aspect for a banner) and `logo.svg` won't render in scrapers.
- Optionally a second `og-image-square.png` (logo only) is unnecessary — one wide image is the standard.
- **Open question for the user:** do they have/want a designed banner, or should we composite one from `logo.png` + a lobby screenshot? (See "Asset decision" below — needs their input; I won't fabricate a design.)

### 2. Make the static head generic-but-correct

In `config.ts` `head`, change/add the *site-level defaults* (these become fallbacks; `transformHead` overrides per page):
- `og:image` → `…/server/og-image.png` (absolute, PNG).
- Add `og:image:width=1200`, `og:image:height=630`, `og:image:alt`.
- Add `og:site_name=JunimoServer`.
- Add `twitter:image` (same PNG), upgrade `twitter:card` → `summary_large_image`.
- Add `twitter:site`/`twitter:creator` only if the project has an X handle (open question; omit if none rather than invent).
- Keep `og:type=website` as the site default (per-page article type is optional, see step 3).

### 3. Add `transformHead` for per-page tags

Add to `defineConfig`:

```ts
transformHead({ pageData, title, description }) {
    if (pageData.isNotFound) return [];   // 404 has no canonical URL
    const origin = "https://stardew-valley-dedicated-server.github.io";
    // `base` already encodes latest vs preview ("/server/" | "/server/preview/")
    const path = pageData.relativePath
        .replace(/(^|\/)index\.md$/, "$1")   // index.md -> "" (dir root)
        .replace(/\.md$/, ".html");           // verified: SSG writes *.html (no cleanUrls)
    const url = `${origin}${base}${path}`;
    // `title` is already pageData.title || siteData.title (verified chunk:17351),
    // so it handles the home page (layout:home, no title) correctly. Do NOT use
    // frontmatter.title here — that loses the site-title fallback.
    return [
        ["meta", { property: "og:title", content: title }],
        ["meta", { property: "og:description", content: description }],
        ["meta", { property: "og:url", content: url }],
        ["link", { rel: "canonical", href: url }],
        ["meta", { name: "twitter:title", content: title }],
        ["meta", { name: "twitter:description", content: description }],
    ];
}
```

De-dup mechanism — **verified against `vitepress/dist/node/chunk-D3CUZ4fa.js`**, not assumed:
- The page renderer calls `mergeHead(staticHead, transformHeadOutput)` (chunk:49434). `mergeHead(prev,curr)` (chunk:17383) drops every `prev` tag for which `hasTag(curr,…)` is true, then appends `curr` — so **transformHead output replaces matching static tags**. The "remove static duplicates" step is therefore *belt-and-suspenders*, not strictly required — but we still do it for a clean source.
- **Load-bearing constraint:** `hasTag` keys ONLY on the tag's **first attribute** (`Object.entries(tagAttrs)[0]`, chunk:17377). De-dup fires only if the transformHead tag's first attr key+value equals the static tag's. So every OG meta MUST list `property` first and every Twitter meta MUST list `name` first (the snippet above does). If a static tag were written `{ content, property }` it would NOT be de-duped against `{ property, content }`. Keep attribute order consistent across static + transformHead.
- Attribute values are escaped by `renderAttrs` → `escapeHtml` (chunk:49539), so descriptions containing `"`/`&`/`<` are safe; do **not** pre-escape.
- VitePress auto-emits `<title>` and `<meta name="description">` itself (chunk:49468–69); we are *adding* OG/Twitter/canonical, not replacing those. No conflict.

Other verified facts:
- **URL suffix:** no `cleanUrls`/`rewrites` in config (confirmed). SSG writes `page.replace(/\.md$/,".html")` (chunk:49483) → canonical uses `.html`. Home (`index.md`) → `${base}` (root). Still inspect one built file as the final gate.
- **404 page:** guarded by `pageData.isNotFound` early-return above.

### 4. Per-page description hygiene (content pass)

`transformHead` is only as good as each page's `description`. Most pages currently have **no** frontmatter `description`, so they all fall back to the site description — defeating the per-page win for description.

- Add a short (`<=155 char`) `description:` frontmatter to the high-traffic landing pages at minimum: `index.md` (home), and each section index: `players/index.md`, `admins/index.md`, `features/index.md`, `developers/index.md`, `community/index.md`, plus the top funnel pages (`admins/quick-start/*`).
- This is the bulk of the lasting value; the rest of the tree can get descriptions incrementally. **Scope question for user:** all ~60 pages, or just the landing/funnel set (~15)? Recommend the landing/funnel set now; leave a tracked TODO for the long tail rather than rushing thin auto-generated descriptions.

### 5. Optional polish

- `theme-color` meta (matches brand) for mobile browser chrome.
- `og:image:alt` describing the banner.
- Per-page `og:type=article` for non-index docs pages (minor; `website` is fine everywhere and simpler — recommend skipping unless wanted).
- A `robots`/`googlebot` default is unnecessary (Pages is indexable by default).

## Verification (gates — run, don't assume)

1. **Build inspection (`verify-edit-landed-in-artifact`):** after `npm run build` in `docs/`, open `dist/index.html` and `dist/admins/quick-start/installation.html` and confirm: exactly one `og:title`/`og:description`/`og:url`/`canonical` each, page-specific values, absolute PNG `og:image`, `twitter:card=summary_large_image`. No duplicate OG tags.
2. **Preview-build path:** run with `DOCS_VERSION=preview` and confirm canonical/og:url include `/server/preview/`.
3. **External scraper validation:** after deploy (or via a tunnel), check at least one real validator — Discord (paste link in a private channel), [opengraph.xyz](https://www.opengraph.xyz), Twitter Card Validator, or `curl -A 'Discordbot' <url>` and grep the returned `<head>`. The OG image must actually load (PNG, absolute, 200).
4. Confirm `og:image` is reachable at its absolute URL (404 = no preview image).

## Out of scope

- Redesigning the docs theme or homepage.
- Per-page hand-authored social images (one site-wide banner is the standard; dynamic per-page images via satori/og-image generation is a much larger feature — note as possible future work, don't build now).
- Anything under `docs/node_modules` or generated `dist/`.

## Decisions (confirmed with user)

1. **OG banner image:** composite a 1200×630 `docs/public/og-image.png` from existing assets (`logo.png` + brand-colored background, tagline optional). No external design dependency.
2. **Description scope:** landing + funnel only now (~15 pages: `index.md`, the six section index pages, `admins/quick-start/*`). Long tail gets a tracked TODO, not rushed thin descriptions.
3. **Twitter handle:** none — omit `twitter:site` / `twitter:creator` entirely (don't invent one). `summary_large_image` still works without a handle.
