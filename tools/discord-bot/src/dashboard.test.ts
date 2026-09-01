import { describe, expect, test } from "bun:test";
import {
    classifyDashboardEmbed,
    DASHBOARD_TITLE,
    type EmbedLike,
    formatFooter,
    isDashboardEmbed,
    parseOwnerId,
} from "./dashboard";

const OWNER_ID = "3f2c8a1e-9b4d-4c6f-8a2e-1d5b7c9e0f3a";
const OTHER_ID = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d";

function dashboardEmbed(footerText: string | null): EmbedLike {
    return {
        title: DASHBOARD_TITLE,
        footer: footerText === null ? null : { text: footerText },
    };
}

describe("formatFooter", () => {
    test("stamps the owner id when present", () => {
        expect(formatFooter("30 seconds", OWNER_ID)).toBe(`Automatically updates every 30 seconds • id:${OWNER_ID}`);
    });

    test("omits the stamp in degraded mode", () => {
        expect(formatFooter("2 minutes", null)).toBe("Automatically updates every 2 minutes");
    });
});

describe("parseOwnerId", () => {
    test("round-trips the id written by formatFooter", () => {
        expect(parseOwnerId(formatFooter("30 seconds", OWNER_ID))).toBe(OWNER_ID);
    });

    test("returns null for an unstamped footer", () => {
        expect(parseOwnerId(formatFooter("30 seconds", null))).toBeNull();
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
        const embed = dashboardEmbed(formatFooter("30 seconds", OWNER_ID));
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("mine");
    });

    test("an unstamped dashboard is legacy", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter("30 seconds", null)), OWNER_ID)).toBe("legacy");
        expect(classifyDashboardEmbed(dashboardEmbed(null), OWNER_ID)).toBe("legacy");
    });

    test("another deployment's stamp is foreign", () => {
        const embed = dashboardEmbed(formatFooter("30 seconds", OTHER_ID));
        expect(classifyDashboardEmbed(embed, OWNER_ID)).toBe("foreign");
    });

    test("degraded mode adopts every dashboard by title, regardless of stamp", () => {
        expect(classifyDashboardEmbed(dashboardEmbed(formatFooter("30 seconds", OTHER_ID)), null)).toBe("mine");
        expect(classifyDashboardEmbed(dashboardEmbed(null), null)).toBe("mine");
    });

    test("degraded mode still ignores non-dashboard content", () => {
        expect(classifyDashboardEmbed({ title: "Not a dashboard" }, null)).toBe("unrelated");
    });
});
