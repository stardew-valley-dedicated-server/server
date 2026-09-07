import { describe, expect, test } from "bun:test";
import { formatStardewTime } from "./serverState";

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
