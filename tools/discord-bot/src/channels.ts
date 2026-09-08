/**
 * Channel configuration: an operator names a channel either by snowflake id or by name.
 * An id targets exactly one channel. A name is resolved in every guild the bot is in,
 * so one configuration serves a production and a test Discord server alike.
 */

import { ChannelType } from "discord.js";

export type ChannelRef = { kind: "id"; id: string } | { kind: "name"; name: string };

/** Minimal channel shape shared by discord.js channels and test fixtures. */
export interface ChannelLike {
    id: string;
    name: string | null;
    type: ChannelType;
}

// Discord snowflakes are 17-20 digit integers; anything else is a channel name.
const SNOWFLAKE = /^\d{17,20}$/;

/** Parses an env value into a channel reference; empty or unset yields null. */
export function parseChannelRef(raw: string | undefined): ChannelRef | null {
    const value = raw?.trim();
    if (!value) {
        return null;
    }
    if (SNOWFLAKE.test(value)) {
        return { kind: "id", id: value };
    }
    // Discord stores text channel names lowercased; accept "#name" as written in chat.
    return { kind: "name", name: value.replace(/^#/, "").toLowerCase() };
}

/** Human-readable form for logs: `#name` or `channel id 123`. */
export function describeChannelRef(ref: ChannelRef): string {
    return ref.kind === "id" ? `channel id ${ref.id}` : `#${ref.name}`;
}

/** Channels the bot can post plain messages and embeds to; threads and voice chats are not targets. */
const POSTABLE_TYPES: ReadonlySet<ChannelType> = new Set([ChannelType.GuildText, ChannelType.GuildAnnouncement]);

/** True when the channel is the configured one: a postable channel with the id, or the name. */
export function matchesChannelRef(channel: ChannelLike, ref: ChannelRef): boolean {
    if (!POSTABLE_TYPES.has(channel.type)) {
        return false;
    }
    return ref.kind === "id" ? channel.id === ref.id : channel.name === ref.name;
}
