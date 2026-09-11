import type { IncomingMessage, ServerResponse } from "node:http";
import type { Plugin } from "vite";
import type { ServerStatus } from "../../tools/discord-bot/src/serverState";

/** Base URL the page fetches `/status` under when no real API URL is configured. */
export const STUB_BASE = "/__status-stub";

/**
 * Fake `/status` for `vitepress dev`, so the Public Test Server card works offline. Typed as the
 * shared contract: a field added to `ServerStatus` without a value here fails the type-check.
 * Never part of a build (`apply: "serve"`). `vitepress preview` serves the built site through its
 * own static server, which runs no Vite plugins, so the stub does not exist there.
 */
export function statusStubPlugin(): Plugin {
    const startedAtUtc = new Date(Date.now() - 3 * 86_400_000 - 5 * 3_600_000).toISOString();
    let version = 0;

    const fakeStatus = (): ServerStatus => ({
        isOnline: true,
        isReady: true,
        dayTransitionComplete: true,
        isPaused: false,
        playerCount: 31,
        maxPlayers: 37,
        steamInviteCode: "S123456789",
        steamRelayReady: true,
        galaxyLobby: "connected",
        steamSession: "connected",
        authReadiness: "ok",
        serverName: "Preview",
        farmName: "Junimo",
        serverVersion: "1.5.0-preview.133",
        gameVersion: "1.6.15",
        season: "summer",
        day: 12,
        year: 2,
        timeOfDay: 1330,
        farmTypeKey: "Standard",
        tps: 59.6,
        startedAtUtc,
        lastUpdated: new Date().toISOString(),
        version: ++version,
    });

    const handle = (req: IncomingMessage, res: ServerResponse, next: () => void) => {
        if (req.url !== `${STUB_BASE}/status`) {
            next();
            return;
        }
        res.setHeader("Content-Type", "application/json");
        res.setHeader("Cache-Control", "no-store");
        res.end(JSON.stringify(fakeStatus()));
    };

    return {
        name: "status-stub",
        apply: "serve",
        configureServer(server) {
            server.middlewares.use(handle);
        },
    };
}
