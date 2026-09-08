import { describe, expect, test } from "bun:test";
import { formatStardewDate, formatStardewTime, joinableInviteCode } from "./serverState";

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
