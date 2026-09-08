/**
 * Pure helpers for the dashboard message: building the embed, footer stamping, owner-id
 * parsing, and classifying channel messages during the adoption scan.
 */

import { type APIEmbedField, EmbedBuilder } from "discord.js";
import type { DiscordServerState } from "./discordState";
import {
    formatStardewDate,
    formatStardewTime,
    formatUptime,
    joinableInviteCode,
    type ServerStatus,
} from "./serverState";

export const DASHBOARD_TITLE = "Server Status";

/** Dashboards posted under this title are still adopted; the next edit gives them the current one. */
const LEGACY_DASHBOARD_TITLE = "🧑‍🌾 Stardew Valley Server Status Dashboard";

const OWNER_ID_SEPARATOR = " · Server ID: ";
// Footers written before the redesign used this; still recognized so the bot re-adopts and
// re-stamps its own older dashboards (and still detects foreign ones) instead of duplicating.
const LEGACY_OWNER_ID_SEPARATOR = " • id:";

/** Footer stamp length: a UUID prefix keeps the footer short and is unique enough among the handful of deployments sharing a channel. */
const STAMP_LENGTH = 8;

/** State line for the text `!status` reply: the label while online, otherwise with the reason. */
export function formatStateLine(state: DiscordServerState): string {
    return state.kind === "online" ? state.label : `${state.label}: ${state.detail}`;
}

/** Version strings as code chips; a version the mod has not reported yet shows a dash. */
function versionChip(version: string): string {
    return version ? `\`${version}\`` : "—";
}

/**
 * The fields of a running server: Players and in-game date share the top row (inline), while the
 * build version and the invite code each take a full-width row. A last-known snapshot passes
 * `includeInvite: false` (the invite only makes sense for a reachable server).
 */
export function buildStatusFields(status: ServerStatus, includeInvite = true): APIEmbedField[] {
    const players = `\`${status.playerCount} / ${status.maxPlayers}\`${status.isPaused ? " _(paused)_" : ""}`;
    const date = `\`${formatStardewDate(status)} · ${formatStardewTime(status.timeOfDay)}\``;
    const fields: APIEmbedField[] = [
        { name: "Players", value: players, inline: true },
        { name: "In-game date", value: date, inline: true },
        { name: "Build Version", value: versionChip(status.serverVersion), inline: false },
    ];
    if (includeInvite) {
        const inviteCode = joinableInviteCode(status);
        fields.push({
            name: "Invite code",
            value: inviteCode ? `\`${inviteCode}\`` : "_not yet available_",
            inline: false,
        });
    }
    return fields;
}

/**
 * The dashboard embed. Every state leads with a `dot label` headline; a live server appends its
 * uptime. A running server adds its live fields, an unreachable one falls back to the last-known
 * snapshot (`lastKnown`) once the bot has seen the server online, and a server with no game data
 * yet shows the state's hint in place of fields.
 */
export function buildDashboardEmbed(
    status: ServerStatus | null,
    state: DiscordServerState,
    footerText: string,
    lastKnown: ServerStatus | null = null,
    now: number = Date.now(),
): EmbedBuilder {
    const hasLastKnown = !status?.isOnline && lastKnown !== null;
    // Uptime rides the headline beside the label (a live server only), like the docs widget's badge.
    const uptime = status?.isOnline && status.startedAtUtc ? ` · up ${formatUptime(status.startedAtUtc, now)}` : "";
    const headline = `**${state.emoji} ${state.label}**${uptime}`;
    const embed = new EmbedBuilder()
        .setTitle(DASHBOARD_TITLE)
        .setColor(state.color)
        .setFooter({ text: footerText })
        .setDescription(headline);

    if (status?.isOnline) {
        return embed.addFields(buildStatusFields(status));
    }
    if (hasLastKnown && lastKnown) {
        return embed.addFields(buildStatusFields(lastKnown, false));
    }
    // No game data to show yet: the hint explains what the reader is waiting for.
    if (state.hint) {
        embed.setDescription(`${headline}\n\n${state.hint}`);
    }
    return embed;
}

/** Compact refresh cadence for the footer: "30s", "1m", "2m". */
export function formatRefreshRate(seconds: number): string {
    return seconds % 60 === 0 ? `${seconds / 60}m` : `${seconds}s`;
}

/**
 * Persisted dashboard state. `ownerId` is this deployment's identity, stamped into every
 * dashboard footer; `messageIds` caches the tracked message per channel id. The cache is
 * only a shortcut: a missing entry makes the next update rescan the channel and re-adopt
 * the message by its stamp.
 */
export interface PersistedDashboardState {
    ownerId: string;
    messageIds: Record<string, string>;
}

/**
 * Parses the state file. Returns null when no usable owner id is stored, which the caller
 * treats as a first boot. Message ids that are not snowflakes are dropped: fetching one
 * fails with a 400 that the transient-error path would retry forever.
 */
export function parseDashboardState(raw: string): PersistedDashboardState | null {
    let parsed: unknown;
    try {
        parsed = JSON.parse(raw);
    } catch {
        return null;
    }
    if (typeof parsed !== "object" || parsed === null) {
        return null;
    }
    const record = parsed as Record<string, unknown>;
    const ownerId = typeof record.ownerId === "string" ? record.ownerId.trim() : "";
    if (ownerId.length === 0) {
        return null;
    }

    const messageIds: Record<string, string> = {};
    if (typeof record.messageIds === "object" && record.messageIds !== null) {
        for (const [channelId, messageId] of Object.entries(record.messageIds as Record<string, unknown>)) {
            if (/^\d+$/.test(channelId) && typeof messageId === "string" && /^\d+$/.test(messageId)) {
                messageIds[channelId] = messageId;
            }
        }
    }
    return { ownerId, messageIds };
}

/** Minimal embed shape shared by discord.js `Embed` and test fixtures. */
export interface EmbedLike {
    title?: string | null;
    footer?: { text?: string | null } | null;
}

/**
 * The dashboard footer: refresh cadence, when the status was last checked, and the deployment's
 * ownership stamp. When `ownerId` is null (persistence unavailable) the stamp is omitted.
 */
export function formatFooter(refreshSeconds: number, ownerId: string | null, lastChecked: string): string {
    const parts = [`↻ Updates every ${formatRefreshRate(refreshSeconds)}`, `Last checked ${lastChecked}`];
    if (ownerId) {
        parts.push(`Server ID: ${ownerId.slice(0, STAMP_LENGTH)}`);
    }
    return parts.join(" · ");
}

/** Extracts the ownership stamp from a footer, trying the current format then the legacy one. */
export function parseOwnerId(footerText: string | null | undefined): string | null {
    if (!footerText) {
        return null;
    }
    for (const separator of [OWNER_ID_SEPARATOR, LEGACY_OWNER_ID_SEPARATOR]) {
        const index = footerText.lastIndexOf(separator);
        if (index !== -1) {
            const id = footerText.slice(index + separator.length).trim();
            if (id.length > 0) {
                return id;
            }
        }
    }
    return null;
}

/** A message is a dashboard iff its first embed carries the dashboard title. */
export function isDashboardEmbed(embed: EmbedLike | null | undefined): boolean {
    return embed?.title === DASHBOARD_TITLE || embed?.title === LEGACY_DASHBOARD_TITLE;
}

/**
 * - `mine` — dashboard embed stamped with our owner id
 * - `legacy` — dashboard embed with no ownership stamp (adopt and stamp)
 * - `foreign` — dashboard embed stamped by another deployment (never touch)
 * - `unrelated` — not a dashboard embed (chat relay line, command reply, ...)
 */
export type DashboardMessageKind = "mine" | "legacy" | "foreign" | "unrelated";

/**
 * Classifies a bot-authored message's first embed against our owner id.
 * With `ownerId` null (degraded mode) adoption is title-based: every dashboard
 * embed classifies as `mine`, stamped or not — a restart without persistence
 * must still re-adopt its own message.
 */
export function classifyDashboardEmbed(
    embed: EmbedLike | null | undefined,
    ownerId: string | null,
): DashboardMessageKind {
    if (!isDashboardEmbed(embed)) {
        return "unrelated";
    }
    if (ownerId === null) {
        return "mine";
    }
    const stamp = parseOwnerId(embed?.footer?.text);
    if (stamp === null) {
        return "legacy";
    }
    // Prefix match: footers written before the stamp was shortened carry the full id.
    return ownerId.startsWith(stamp) ? "mine" : "foreign";
}
