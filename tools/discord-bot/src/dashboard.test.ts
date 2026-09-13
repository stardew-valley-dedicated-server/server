import { describe, expect, test } from "bun:test";
import {
    buildDashboardEmbed,
    classifyDashboardEmbed,
    DASHBOARD_TITLE,
    type EmbedLike,
    formatFooter,
    formatRefreshRate,
    isDashboardEmbed,
    parseDashboardState,
    parseOwnerId,
} from "./dashboard";
import { resolveServerState, type ServerStatus } from "./discordState";

const OWNER_ID = "3f2c8a1e-9b4d-4c6f-8a2e-1d5b7c9e0f3a";
const OTHER_ID = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";
const LAST_CHECKED = "1:30 PM";
// A fixed "now" 3d 4h after ONLINE_STATUS.startedAtUtc, so uptime renders deterministically.
const NOW = Date.parse("2026-01-01T00:00:00Z") + (3 * 1440 + 4 * 60) * 60000;

function dashboardEmbed(footerText: string | null): EmbedLike {
    return {
        title: DASHBOARD_TITLE,
        footer: footerText === null ? null : { text: footerText },
    };
}

const ONLINE_STATUS: ServerStatus = {
    isOnline: true,
    isReady: true,
    playerCount: 1,
    maxPlayers: 10,
    steamInviteCode: "SGF0LUHHTYF5",
    steamRelayReady: true,
    galaxyLobby: "connected",
    steamSession: "connected",
    authReadiness: "ok",
    serverVersion: "1.5.0-preview.134",
    gameVersion: "1.6.15",
    dayTransitionComplete: true,
    lastUpdated: "2026-01-01T00:00:00Z",
    farmName: "Junimo",
    serverName: "",
    startedAtUtc: "2026-01-01T00:00:00Z",
    day: 14,
    season: "spring",
    year: 1,
    timeOfDay: 650,
    farmTypeKey: "Standard",
    isPaused: false,
    tps: 5.04,
    version: 1,
};

describe("buildDashboardEmbed", () => {
    const FOOTER = "footer";

    function fields(status: ServerStatus | null, lastKnown: ServerStatus | null = null): Record<string, string> {
        const embed = buildDashboardEmbed(status, resolveServerState(status), FOOTER, lastKnown, NOW).toJSON();
        return Object.fromEntries((embed.fields ?? []).map((f) => [f.name, f.value]));
    }

    test("online: headline with uptime, players/date inline, build version and invite full-width", () => {
        const embed = buildDashboardEmbed(ONLINE_STATUS, resolveServerState(ONLINE_STATUS), FOOTER, null, NOW).toJSON();
        expect(embed.title).toBe(DASHBOARD_TITLE);
        expect(embed.footer?.text).toBe(FOOTER);
        expect(embed.description).toBe("**🟢 Online** · up 3d 4h");
        expect(embed.color).toBe(resolveServerState(ONLINE_STATUS).color);
        expect(embed.fields?.map((f) => [f.name, f.inline])).toEqual([
            ["Players", true],
            ["In-game date", true],
            ["Build Version", false],
            ["Invite code", false],
        ]);
        expect(fields(ONLINE_STATUS)).toMatchObject({
            Players: "`1 / 10`",
            "In-game date": "`Spring 14, Year 1 · 6:50 AM`",
            "Build Version": "`1.5.0-preview.134`",
            "Invite code": "`SGF0LUHHTYF5`",
        });
    });

    test("busy keeps the fields but leads with the busy headline and color", () => {
        const status = { ...ONLINE_STATUS, isReady: false };
        const embed = buildDashboardEmbed(status, resolveServerState(status), FOOTER, null, NOW).toJSON();
        expect(embed.description).toBe("**🟠 Busy** · up 3d 4h");
        expect(embed.color).toBe(resolveServerState(status).color);
        expect(fields(status).Players).toBe("`1 / 10`");
    });

    test("a paused server is marked on the players value", () => {
        expect(fields({ ...ONLINE_STATUS, isPaused: true }).Players).toBe("`1 / 10` _(paused)_");
    });

    test("no code yet shows the reason from the connectivity state", () => {
        expect(fields({ ...ONLINE_STATUS, steamInviteCode: null, galaxyLobby: "down" })["Invite code"]).toBe(
            "_not yet available (connecting…)_",
        );
        expect(fields({ ...ONLINE_STATUS, steamInviteCode: null, galaxyLobby: "recovering" })["Invite code"]).toBe(
            "_not yet available (Galaxy lobby reconnecting)_",
        );
    });

    test("a code with the Steam relay not ready shows the code plus a note (GOG can join)", () => {
        expect(fields({ ...ONLINE_STATUS, steamRelayReady: false })["Invite code"]).toBe(
            "`SGF0LUHHTYF5` _(Steam relay connecting — GOG players can join now)_",
        );
    });

    test("a version the mod has not reported yet shows a dash", () => {
        expect(fields({ ...ONLINE_STATUS, serverVersion: "" })["Build Version"]).toBe("—");
    });

    test("uptime is left off the headline when the server's start time is unknown", () => {
        const status = { ...ONLINE_STATUS, startedAtUtc: null };
        const embed = buildDashboardEmbed(status, resolveServerState(status), FOOTER, null, NOW).toJSON();
        expect(embed.description).toBe("**🟢 Online**");
    });

    test("a state with no game data shows its hint in place of fields", () => {
        const starting = { ...ONLINE_STATUS, isOnline: false, isReady: false, phase: "starting" };
        const embed = buildDashboardEmbed(starting, resolveServerState(starting), FOOTER, null, NOW).toJSON();
        expect(embed.description).toBe("**🟠 Starting**\n\nGame data appears once the save is loaded.");
        expect(embed.fields).toBeUndefined();
    });

    test("a starting server with a last-known snapshot still shows its hint, not stale fields", () => {
        const starting = { ...ONLINE_STATUS, isOnline: false, isReady: false, phase: "starting" };
        const embed = buildDashboardEmbed(starting, resolveServerState(starting), FOOTER, ONLINE_STATUS, NOW).toJSON();
        expect(embed.description).toBe("**🟠 Starting**\n\nGame data appears once the save is loaded.");
        expect(embed.fields).toBeUndefined();
    });

    test("offline without a last-known snapshot: headline + hint only, no fields", () => {
        const embed = buildDashboardEmbed(null, resolveServerState(null), FOOTER).toJSON();
        expect(embed.description).toBe("**🔴 Offline**\n\nNo game data can be pulled right now. Check back later!");
        expect(embed.color).toBe(resolveServerState(null).color);
        expect(embed.fields).toBeUndefined();
    });

    test("offline with a last-known snapshot shows its fields, without the invite", () => {
        const embed = buildDashboardEmbed(null, resolveServerState(null), FOOTER, ONLINE_STATUS).toJSON();
        expect(embed.description).toBe("**🔴 Offline**");
        expect(embed.fields?.map((f) => f.name)).toEqual(["Players", "In-game date", "Build Version"]);
        expect(fields(null, ONLINE_STATUS)).toMatchObject({
            Players: "`1 / 10`",
            "In-game date": "`Spring 14, Year 1 · 6:50 AM`",
            "Build Version": "`1.5.0-preview.134`",
        });
    });
});

describe("formatRefreshRate", () => {
    test("seconds unless the rate is a whole number of minutes", () => {
        expect(formatRefreshRate(30)).toBe("30s");
        expect(formatRefreshRate(90)).toBe("90s");
        expect(formatRefreshRate(60)).toBe("1m");
        expect(formatRefreshRate(120)).toBe("2m");
    });
});

describe("formatFooter", () => {
    test("cadence, last-checked time, and the owner id's prefix", () => {
        expect(formatFooter(30, OWNER_ID, LAST_CHECKED)).toBe(
            "↻ Updates every 30s · Last checked 1:30 PM · Server ID: 3f2c8a1e",
        );
    });

    test("omits the stamp when persistence is unavailable", () => {
        expect(formatFooter(120, null, LAST_CHECKED)).toBe("↻ Updates every 2m · Last checked 1:30 PM");
    });
});

describe("parseOwnerId", () => {
    test("round-trips the stamp written by formatFooter", () => {
        expect(parseOwnerId(formatFooter(30, OWNER_ID, LAST_CHECKED))).toBe(OWNER_ID.slice(0, 8));
    });

    test("still reads the legacy footer format so older dashboards are re-adopted", () => {
        expect(parseOwnerId("Automatically updates every 30 seconds • id:3f2c8a1e")).toBe("3f2c8a1e");
    });

    test("returns null for an unstamped footer", () => {
        expect(parseOwnerId(formatFooter(30, null, LAST_CHECKED))).toBeNull();
    });

    test("returns null for empty, null, and undefined input", () => {
        expect(parseOwnerId("")).toBeNull();
        expect(parseOwnerId(null)).toBeNull();
        expect(parseOwnerId(undefined)).toBeNull();
    });

    test("returns null when the separator is present but the id is empty", () => {
        expect(parseOwnerId("↻ Updates every 30s · Last checked 1:30 PM · Server ID: ")).toBeNull();
        expect(parseOwnerId("↻ Updates every 30s · Last checked 1:30 PM · Server ID:   ")).toBeNull();
    });
});

describe("isDashboardEmbed", () => {
    test("matches the dashboard title", () => {
        expect(isDashboardEmbed(dashboardEmbed(null))).toBeTrue();
    });

    test("still adopts a dashboard posted under the earlier title", () => {
        expect(isDashboardEmbed({ title: "🧑‍🌾 Stardew Valley Server Status Dashboard" })).toBeTrue();
    });

    test("rejects other embeds and missing embeds", () => {
        expect(isDashboardEmbed({ title: "Some other embed" })).toBeFalse();
        expect(isDashboardEmbed({ title: null })).toBeFalse();
        expect(isDashboardEmbed(null)).toBeFalse();
        expect(isDashboardEmbed(undefined)).toBeFalse();
    });
});

describe("classifyDashboardEmbed", () => {
    test("non-dashboard content is unrelated", () => {
        expect(classifyDashboardEmbed({ title: "Not a dashboard" }, OWNER_ID)).toBe("unrelated");
        expect(classifyDashboardEmbed(undefined, OWNER_ID)).toBe("unrelated");
    });

    test("our stamp is mine", () => {
        const embed = dashboardEmbed(formatFooter(30, OWNER_ID, LAST_CHECKED));
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("mine");
    });

    test("a footer stamped with our full id is still mine", () => {
        const embed = dashboardEmbed(`↻ Updates every 30s · Last checked 1:30 PM · Server ID: ${OWNER_ID}`);
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("mine");
    });

    test("an unstamped dashboard is legacy", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter(30, null, LAST_CHECKED)), OWNER_ID)).toBe("legacy");
        expect(classifyDashboardEmbed(dashboardEmbed(null), OWNER_ID)).toBe("legacy");
    });

    test("another deployment's stamp is foreign, in the current and legacy formats", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter(30, OTHER_ID, LAST_CHECKED)), OWNER_ID)).toBe(
            "foreign",
        );
        expect(
            classifyDashboardEmbed(
                dashboardEmbed(`Automatically updates every 30 seconds • id:${OTHER_ID.slice(0, 8)}`),
                OWNER_ID,
            ),
        ).toBe("foreign");
    });

    test("degraded mode adopts every dashboard by title, regardless of stamp", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter(30, OTHER_ID, LAST_CHECKED)), null)).toBe("mine");
        expect(classifyDashboardEmbed(dashboardEmbed(null), null)).toBe("mine");
    });

    test("degraded mode still ignores non-dashboard content", () => {
        expect(classifyDashboardEmbed({ title: "Not a dashboard" }, null)).toBe("unrelated");
    });
});

describe("parseDashboardState", () => {
    const CHANNEL_ID = "123456789012345678";
    const MESSAGE_ID = "987654321098765432";

    test("round-trips owner id and per-channel message ids", () => {
        const raw = JSON.stringify({ ownerId: OWNER_ID, messageIds: { [CHANNEL_ID]: MESSAGE_ID } });
        expect(parseDashboardState(raw)).toEqual({ ownerId: OWNER_ID, messageIds: { [CHANNEL_ID]: MESSAGE_ID } });
    });

    test("returns null without a usable owner id", () => {
        expect(parseDashboardState("not json")).toBeNull();
        expect(parseDashboardState("null")).toBeNull();
        expect(parseDashboardState("[]")).toBeNull();
        expect(parseDashboardState(JSON.stringify({ ownerId: "   " }))).toBeNull();
        expect(parseDashboardState(JSON.stringify({ messageIds: { [CHANNEL_ID]: MESSAGE_ID } }))).toBeNull();
    });

    test("drops entries whose ids are not snowflakes", () => {
        const raw = JSON.stringify({
            ownerId: OWNER_ID,
            messageIds: { [CHANNEL_ID]: "deleted", "not-a-channel": MESSAGE_ID, "1": 42 },
        });
        expect(parseDashboardState(raw)).toEqual({ ownerId: OWNER_ID, messageIds: {} });
    });
});
