import { describe, expect, test } from "bun:test";
import {
    describeInviteAvailability,
    formatStardewDate,
    formatStardewTime,
    formatUptime,
    joinableInviteCode,
} from "./serverState";

describe("formatStardewDate", () => {
    test("capitalized season, day, and year", () => {
        expect(formatStardewDate({ season: "spring", day: 14, year: 1 })).toBe("Spring 14, Year 1");
    });

    test("tolerates a missing season", () => {
        expect(formatStardewDate({ season: "", day: 1, year: 2 })).toBe("1, Year 2");
    });
});

describe("joinableInviteCode", () => {
    test("is the Steam code once published", () => {
        expect(joinableInviteCode({ steamInviteCode: "SABC" })).toBe("SABC");
    });

    test("is null before the Steam lobby is published, never the GOG code", () => {
        expect(joinableInviteCode({ steamInviteCode: null })).toBeNull();
        expect(joinableInviteCode({ steamInviteCode: "" })).toBeNull();
    });
});

describe("describeInviteAvailability", () => {
    test("no note when the code is fully joinable", () => {
        expect(
            describeInviteAvailability({
                steamInviteCode: "SABC",
                steamRelayReady: true,
                galaxyLobby: "connected",
                steamSession: "connected",
            }),
        ).toBeNull();
    });

    test("code present but relay not ready → GOG-can-join note", () => {
        expect(
            describeInviteAvailability({
                steamInviteCode: "SABC",
                steamRelayReady: false,
                galaxyLobby: "connected",
                steamSession: "connected",
            }),
        ).toBe("Steam relay connecting — GOG players can join now");
    });

    test("no code → the connectivity reason", () => {
        expect(
            describeInviteAvailability({
                steamInviteCode: null,
                steamRelayReady: false,
                galaxyLobby: "recovering",
                steamSession: "connected",
            }),
        ).toBe("Galaxy lobby reconnecting");
        expect(
            describeInviteAvailability({
                steamInviteCode: null,
                steamRelayReady: false,
                galaxyLobby: "down",
                steamSession: "lost",
            }),
        ).toBe("Steam session reconnecting");
    });

    test("LAN-only server explains there will never be a code", () => {
        expect(
            describeInviteAvailability({
                steamInviteCode: null,
                steamRelayReady: false,
                galaxyLobby: null,
                steamSession: "lost",
            }),
        ).toBe("invite codes are disabled in LAN-only mode");
    });
});

describe("formatStardewTime", () => {
    test("morning and afternoon", () => {
        expect(formatStardewTime(600)).toBe("6:00 AM");
        expect(formatStardewTime(1330)).toBe("1:30 PM");
    });

    test("noon and midnight", () => {
        expect(formatStardewTime(1200)).toBe("12:00 PM");
        expect(formatStardewTime(2400)).toBe("12:00 AM");
    });

    test("hours past 24 are after midnight", () => {
        expect(formatStardewTime(2550)).toBe("1:50 AM");
    });
});

describe("formatUptime", () => {
    const start = "2026-01-01T00:00:00Z";
    const startMs = Date.parse(start);

    test("days+hours, hours+minutes, or just minutes", () => {
        expect(formatUptime(start, startMs + (3 * 1440 + 4 * 60) * 60000)).toBe("3d 4h");
        expect(formatUptime(start, startMs + (4 * 60 + 12) * 60000)).toBe("4h 12m");
        expect(formatUptime(start, startMs + 12 * 60000)).toBe("12m");
    });

    test("clamps a start in the future to 0m", () => {
        expect(formatUptime(start, startMs - 60000)).toBe("0m");
    });
});
