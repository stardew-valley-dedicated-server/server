import { describe, expect, test } from "bun:test";
import { ChannelType } from "discord.js";
import { type ChannelLike, describeChannelRef, matchesChannelRef, parseChannelRef } from "./channels";

const ID = "123456789012345678";

function channel(overrides: Partial<ChannelLike>): ChannelLike {
    return { id: ID, name: "server-info", type: ChannelType.GuildText, ...overrides };
}

describe("parseChannelRef", () => {
    test("unset, empty, and blank values yield null", () => {
        expect(parseChannelRef(undefined)).toBeNull();
        expect(parseChannelRef("")).toBeNull();
        expect(parseChannelRef("   ")).toBeNull();
    });

    test("a snowflake is an id", () => {
        expect(parseChannelRef(ID)).toEqual({ kind: "id", id: ID });
        expect(parseChannelRef(`  ${ID}\n`)).toEqual({ kind: "id", id: ID });
    });

    test("anything else is a name, lowercased and without a leading #", () => {
        expect(parseChannelRef("server-info")).toEqual({ kind: "name", name: "server-info" });
        expect(parseChannelRef("#Server-Info")).toEqual({ kind: "name", name: "server-info" });
    });

    test("short digit-only values are names, not ids", () => {
        expect(parseChannelRef("2026")).toEqual({ kind: "name", name: "2026" });
    });
});

describe("describeChannelRef", () => {
    test("renders ids and names distinctly", () => {
        expect(describeChannelRef({ kind: "id", id: ID })).toBe(`channel id ${ID}`);
        expect(describeChannelRef({ kind: "name", name: "server-info" })).toBe("#server-info");
    });
});

describe("matchesChannelRef", () => {
    const byId = { kind: "id", id: ID } as const;
    const byName = { kind: "name", name: "server-info" } as const;

    test("an id matches exactly one channel", () => {
        expect(matchesChannelRef(channel({}), byId)).toBeTrue();
        expect(matchesChannelRef(channel({ id: "999999999999999999" }), byId)).toBeFalse();
    });

    test("a name matches any channel with that name", () => {
        expect(matchesChannelRef(channel({ id: "1" }), byName)).toBeTrue();
        expect(matchesChannelRef(channel({ id: "2" }), byName)).toBeTrue();
        expect(matchesChannelRef(channel({ name: "general" }), byName)).toBeFalse();
    });

    test("only text and announcement channels are targets", () => {
        expect(matchesChannelRef(channel({ type: ChannelType.GuildAnnouncement }), byName)).toBeTrue();
        expect(matchesChannelRef(channel({ type: ChannelType.GuildVoice }), byName)).toBeFalse();
        expect(matchesChannelRef(channel({ type: ChannelType.PublicThread }), byId)).toBeFalse();
        expect(matchesChannelRef(channel({ type: ChannelType.GuildForum }), byName)).toBeFalse();
    });
});
