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
    gogInviteCode: "GGF0LUHHTYF5",
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

    function fields(status: ServerStatus): Record<string, string> {
        const embed = buildDashboardEmbed(status, resolveServerState(status), FOOTER).toJSON();
        return Object.fromEntries((embed.fields ?? []).map((f) => [f.name, f.value]));
    }

    test("online: one row of state, players, and date; one row of versions and tick rate; the invite code", () => {
        const embed = buildDashboardEmbed(ONLINE_STATUS, resolveServerState(ONLINE_STATUS), FOOTER).toJSON();
        expect(embed.title).toBe(DASHBOARD_TITLE);
        expect(embed.footer?.text).toBe(FOOTER);
        expect(embed.description).toBeUndefined();
        expect(embed.fields?.map((f) => [f.name, f.inline])).toEqual([
            ["Status", true],
            ["Players", true],
            ["In-game date", true],
            ["Image", true],
            ["Stardew", true],
            ["Average TPS", true],
            ["Invite code", false],
        ]);
        expect(fields(ONLINE_STATUS)).toMatchObject({
            Status: "Online",
            Players: "**1** / 10",
            "In-game date": "Spring 14, Year 1 · 6:50 AM",
            Image: "`1.5.0-preview.134`",
            Stardew: "`1.6.15`",
            "Average TPS": "5.0",
            "Invite code": "`SGF0LUHHTYF5`",
        });
    });

    test("busy shows the reason after the state, paused is marked on the players", () => {
        const status = { ...ONLINE_STATUS, isReady: false, isPaused: true };
        expect(fields(status).Status).toBe("Busy — Saving, changing day, or running an event.");
        expect(fields(status).Players).toBe("**1** / 10 _(paused)_");
    });

    test("the invite code is pending until the Steam lobby is published", () => {
        expect(fields({ ...ONLINE_STATUS, steamInviteCode: null })["Invite code"]).toBe("_not yet available_");
    });

    test("a version the mod has not reported yet shows a dash", () => {
        expect(fields({ ...ONLINE_STATUS, gameVersion: "" }).Stardew).toBe("—");
    });

    test("not online: state and hint only, no fields", () => {
        const embed = buildDashboardEmbed(null, resolveServerState(null), FOOTER).toJSON();
        expect(embed.fields).toBeUndefined();
        expect(embed.description).toBe(
            "**Offline** — The server is offline.\n_No game data can be pulled right now. Check back later!_",
        );
    });

    test("no emoji anywhere in the embed", () => {
        for (const status of [ONLINE_STATUS, null]) {
            const json = JSON.stringify(buildDashboardEmbed(status, resolveServerState(status), FOOTER).toJSON());
            expect(json).not.toMatch(/\p{Extended_Pictographic}/u);
        }
    });
});

describe("formatRefreshRate", () => {
    test("seconds unless the rate is a whole number of minutes", () => {
        expect(formatRefreshRate(30)).toBe("30 seconds");
        expect(formatRefreshRate(90)).toBe("90 seconds");
        expect(formatRefreshRate(60)).toBe("1 minute");
        expect(formatRefreshRate(120)).toBe("2 minutes");
    });
});

describe("formatFooter", () => {
    test("stamps the owner id's prefix when present", () => {
        expect(formatFooter(30, OWNER_ID)).toBe("Automatically updates every 30 seconds • id:3f2c8a1e");
    });

    test("omits the stamp in degraded mode", () => {
        expect(formatFooter(120, null)).toBe("Automatically updates every 2 minutes");
    });
});

describe("parseOwnerId", () => {
    test("round-trips the stamp written by formatFooter", () => {
        expect(parseOwnerId(formatFooter(30, OWNER_ID))).toBe(OWNER_ID.slice(0, 8));
    });

    test("returns null for an unstamped footer", () => {
        expect(parseOwnerId(formatFooter(30, null))).toBeNull();
    });

    test("returns null for empty, null, and undefined input", () => {
        expect(parseOwnerId("")).toBeNull();
        expect(parseOwnerId(null)).toBeNull();
        expect(parseOwnerId(undefined)).toBeNull();
    });

    test("returns null when the separator is present but the id is empty", () => {
        expect(parseOwnerId("Automatically updates every 30 seconds • id:")).toBeNull();
        expect(parseOwnerId("Automatically updates every 30 seconds • id:   ")).toBeNull();
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
        const embed = dashboardEmbed(formatFooter(30, OWNER_ID));
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("mine");
    });

    test("a footer stamped with our full id is still mine", () => {
        const embed = dashboardEmbed(`Automatically updates every 30 seconds • id:${OWNER_ID}`);
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("mine");
    });

    test("an unstamped dashboard is legacy", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter(30, null)), OWNER_ID)).toBe("legacy");
        expect(classifyDashboardEmbed(dashboardEmbed(null), OWNER_ID)).toBe("legacy");
    });

    test("another deployment's stamp is foreign", () => {
        const embed = dashboardEmbed(formatFooter(30, OTHER_ID));
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("foreign");
    });

    test("degraded mode adopts every dashboard by title, regardless of stamp", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter(30, OTHER_ID)), null)).toBe("mine");
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
