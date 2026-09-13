import { describe, expect, test } from "bun:test";
import {
    CONNECTION_STATUS_TEXT,
    type ConnectionStatusCode,
    connectionStatusText,
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
        expect(joinableInviteCode({ inviteCode: "SABC" })).toBe("SABC");
    });

    test("is null before the Steam lobby is published, never the GOG code", () => {
        expect(joinableInviteCode({ inviteCode: null })).toBeNull();
        expect(joinableInviteCode({ inviteCode: "" })).toBeNull();
    });
});

describe("connectionStatusText", () => {
    test("a ready code shows alone", () => {
        expect(connectionStatusText({ connectionStatusCode: "ready" })).toBeNull();
    });

    test("every other code maps to its display text", () => {
        expect(connectionStatusText({ connectionStatusCode: "steamRelayPending" })).toBe(
            "GOG ready · Steam connecting…",
        );
        expect(connectionStatusText({ connectionStatusCode: "reconnecting" })).toBe("reconnecting…");
        expect(connectionStatusText({ connectionStatusCode: "starting" })).toBe("starting up…");
        expect(connectionStatusText({ connectionStatusCode: "steamSessionDown" })).toBe("connecting to Steam…");
        expect(connectionStatusText({ connectionStatusCode: "inviteUnavailable" })).toBe("not used on this server");
    });

    test("in-progress states end in an ellipsis; the settled state does not", () => {
        for (const [code, text] of Object.entries(CONNECTION_STATUS_TEXT)) {
            if (text === null) {
                continue;
            }
            expect(text.endsWith("…")).toBe(code !== "inviteUnavailable");
        }
    });

    test("a server predating the field yields no text", () => {
        expect(connectionStatusText({ connectionStatusCode: undefined as unknown as ConnectionStatusCode })).toBeNull();
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
