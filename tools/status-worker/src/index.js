// Proxies GET /status from a JunimoServer API over HTTPS with a 30 s cache.
// /status is a public endpoint, so no API key is needed.

const CACHE_TTL_MS = 30_000;

const HEADERS = {
    "Content-Type": "application/json",
    "Access-Control-Allow-Origin": "*",
    "Cache-Control": "public, max-age=30",
};

// Memoized per isolate. The Cache API only works on custom domains, and a per-isolate memo
// covers a docs page read a few hundred times a day just as well.
let cached = { body: null, expiresAt: 0 };

export default {
    async fetch(request, env) {
        if (request.method !== "GET") {
            return new Response(null, { status: 405, headers: { Allow: "GET" } });
        }

        const now = Date.now();
        if (cached.body !== null && now < cached.expiresAt) {
            return new Response(cached.body, { status: 200, headers: HEADERS });
        }

        let upstream;
        try {
            upstream = await fetch(`${env.UPSTREAM_URL}/status`, { headers: { Accept: "application/json" } });
        } catch {
            upstream = null;
        }
        if (!upstream || upstream.status !== 200) {
            // Empty 502 so the widget renders "unreachable" instead of stale data.
            return new Response(null, { status: 502, headers: { "Access-Control-Allow-Origin": "*" } });
        }

        const body = await upstream.text();
        cached = { body, expiresAt: now + CACHE_TTL_MS };
        return new Response(body, { status: 200, headers: HEADERS });
    },
};
