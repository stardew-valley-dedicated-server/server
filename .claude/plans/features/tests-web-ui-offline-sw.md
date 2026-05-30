# Service Worker + Offline UX for Test UI

## Context

Two modes of running the test UI:
1. **Dev mode** (`bun run dev`) — Vite dev server with HMR, loads mock-data.json, for iterating on the UI itself
2. **Live mode** (`make test-web`) — Built app served by the test runner backend (port 5000), live WebSocket data

In both cases, when the server goes down and the user reloads the tab, they get a dead "page not found". Adding a service worker lets the cached app shell load even offline, showing a meaningful "disconnected" state instead of nothing. The existing WebSocket reconnection logic in `useWebSocket.ts` (exponential backoff, 1s→10s max) handles auto-reconnect when the server returns.

## Design Decisions

1. **Cache the app shell only** — HTML, JS/CSS (hashed bundles in prod, ESM modules in dev), fonts, logo. NOT mock-screenshots (100+ PNGs), NOT API responses, NOT `mock-data.json` (stale test data would be misleading).

2. **No last-known state caching** — An empty "pending" shell with a disconnected banner is the honest representation. Showing stale test results from a previous run would confuse developers.

3. **Inline disconnected banner, not a separate page** — The full app chrome loads from cache (sidebar, theme, layout prefs from localStorage). A slim warning strip appears below the StatusBar. Auto-hides when server reconnects.

4. **Active in both dev and prod modes** — The SW registers unconditionally. In dev mode, it uses network-first for everything (so Vite HMR always gets fresh modules when the server is up). In prod mode, hashed assets use cache-first. The SW passthrough-skips Vite's HMR WebSocket (`/@vite/`), backend routes (`/api/`, `/ws`), and data files.

5. **No `vite-plugin-pwa`** — A hand-written ~60-line service worker in `public/sw.js` is simpler and avoids adding Workbox as a dependency. No build-time manifest injection needed — we pre-cache only known static paths (index.html, fonts, logo) and let other assets cache themselves on first fetch.

## Implementation

### Step 1: Create the service worker

**New file: `tests/test-ui/public/sw.js`**

Plain JS service worker placed in `public/` so Vite serves it in dev mode and copies it as-is to the build output root in prod.

```js
const CACHE = 'test-ui-v1'

// Pre-cache the known static shell (unhashed files that exist in both dev and prod)
const PRECACHE = ['/', '/index.html', '/logo.svg',
  '/fonts/inter-latin-400.woff2', '/fonts/inter-latin-500.woff2',
  '/fonts/inter-latin-600.woff2', '/fonts/inter-latin-700.woff2']

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(PRECACHE)))
  self.skipWaiting()
})

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys().then(keys =>
      Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k)))
    ).then(() => self.clients.claim())
  )
})

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url)

  // Pass-through: non-GET, Vite internals, backend routes, data files
  if (e.request.method !== 'GET') return
  if (url.pathname.startsWith('/@')) return            // /@vite/client, /@fs/, /@id/
  if (url.pathname.startsWith('/node_modules/')) return // Vite pre-bundled deps
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/ws')
      || url.pathname.startsWith('/artifacts/')
      || url.pathname.startsWith('/mock-artifacts/')) return  // covers mock-data.json + screenshots

  // Prod hashed assets (/assets/*): cache-first (hash = immutable)
  if (url.pathname.startsWith('/assets/')) {
    e.respondWith(
      caches.match(e.request).then(cached => cached || fetch(e.request).then(res => {
        const clone = res.clone()
        caches.open(CACHE).then(c => c.put(e.request, clone))
        return res
      }))
    )
    return
  }

  // Everything else (HTML, fonts, logo, dev-mode ESM sources): network-first, cache fallback
  e.respondWith(
    fetch(e.request).then(res => {
      if (res.ok) {
        const clone = res.clone()
        caches.open(CACHE).then(c => c.put(e.request, clone))
      }
      return res
    }).catch(() => caches.match(e.request))
  )
})
```

Strategies:
- **Vite internals** (`/@vite/*`, `/@fs/*`, `/node_modules/`): Pass-through. These are Vite HMR/dev infrastructure — never cache or intercept.
- **Backend routes / mock data** (`/api/*`, `/ws`, `/artifacts/*`, `/mock-artifacts/*`): Pass-through. `/mock-artifacts/` is the static-file root for both `mock-data.json` and mock screenshots (verified at `tests/test-ui/src/composables/useScreenshotCache.ts:24,44-45` and `tests/test-ui/src/composables/useTestStore.ts:1208`).
- **Prod hashed assets** (`/assets/*`): Cache-first. Content hash in filename = immutable.
- **Everything else** (HTML, fonts, logo, dev-mode `.vue`/`.ts`/`.css` source files): Network-first with cache fallback. When server is up → always fresh (HMR works normally). When server is down → serves from cache.

This means in dev mode, the SW caches the `.vue`, `.ts`, `.css` source files that Vite transforms and serves. When Vite dies, reload serves these cached modules — the app loads (without HMR, but the shell renders). When Vite restarts, network-first ensures fresh modules take over immediately.

Cache versioning: Bump `CACHE` string (`test-ui-v1` → `test-ui-v2`) when the SW *logic* itself changes. Old caches purged on activation. Bundled assets are content-hashed by Vite, so `index.html` revalidation handles bundle freshness automatically — no SW bump needed for app-code changes.

### Step 2: Register the service worker

**Modify: `tests/test-ui/src/main.ts`**

Register unconditionally (both dev and prod):
```ts
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js')
}
```

### Step 3: Add disconnected banner to App.vue

**Modify: `tests/test-ui/src/App.vue`**

Add `navigator.onLine` tracking via `online`/`offline` window events (in the existing `onMounted`/`onUnmounted`).

**Banner visibility logic** — computed `showDisconnectedBanner`:
```ts
const showDisconnectedBanner = computed(() => {
  if (store.isReportMode) return false
  if (store.state.totalTests > 0) return false  // has data (mock or real) → no banner
  return !store.isConnected
})
```

Note: `store.isConnected` returns `false` in two cases: (a) WebSocket exists but is disconnected, (b) no WebSocket was created (mock/dev mode loaded data from `mock-data.json`). We distinguish by checking `store.state.totalTests > 0` — if mock data loaded successfully, there are tests, so no banner. The banner only shows when there's truly no data and no connection.

**Banner placement**: Inside the `<div class="flex-1 flex flex-col min-w-0">` block (`tests/test-ui/src/App.vue:248`), between the existing `<AbortBanner />` (`:249`) and `<StatusBar />` (`:250`), before the content `<div>` at `:251`. The disconnected banner is independent of `<AbortBanner />` — abort is a runner-side signal, disconnected is a transport-side signal — so both can be present simultaneously.

```html
<div v-if="showDisconnectedBanner"
     class="flex items-center gap-2 px-4 py-1.5 bg-warning/10 border-b border-warning/20 text-warning text-xs flex-none">
  <Icon icon="lucide:wifi-off" class="w-3.5 h-3.5 flex-none" />
  <span v-if="!browserOnline">
    Browser offline — showing cached app shell. Will reconnect when network returns...
  </span>
  <span v-else>
    Backend unreachable — reconnecting with backoff (max 10s)...
  </span>
</div>
```

The banner auto-hides when data arrives (mock-data loads or WebSocket connects and snapshot arrives).

### Step 4: No OutputPanel changes needed

The existing OutputPanel empty state (`<OutputPanel v-if="activeView === 'tests'" />` at `tests/test-ui/src/App.vue:252`, body in `tests/test-ui/src/components/OutputPanel.vue`) shows: logo + "Select a test to view details". This is appropriate when offline — the banner in Step 3 communicates the connection issue.

## Files

| Action | File |
|--------|------|
| Create | `tests/test-ui/public/sw.js` |
| Modify | `tests/test-ui/src/main.ts` |
| Modify | `tests/test-ui/src/App.vue` |

No new dependencies needed.

## Build verification

Run `make build-test-ui` after editing — `vue-tsc --noEmit` catches typed-store field typos (e.g. `state.total_tests` vs `state.totalTests`) that plain `vite build` silently passes. Per `.claude/rules/test-ui-build.md`.

## Verification

### Dev mode (`bun run dev`)
1. Start Vite dev server, load the app — SW registers, caches shell + ESM modules
2. Stop Vite dev server
3. Reload tab — cached app loads, shows disconnected banner (mock-data.json fetch fails so no test data)
4. Restart Vite → mock-data.json loads, banner disappears, full UI with mock data

### Prod mode (`bun run build && bun run preview`)
1. Build and preview — SW registers, caches shell + hashed bundles
2. Stop preview server
3. Reload tab — cached app loads, shows disconnected banner
4. Restart server — WebSocket reconnects, snapshot arrives, banner disappears

### Both modes
- Check DevTools > Application > Service Workers — `sw.js` active
- Check DevTools > Application > Cache Storage — `test-ui-v1` with cached entries
- Open new tab while server is down — loads from cache (SW is origin-scoped)
