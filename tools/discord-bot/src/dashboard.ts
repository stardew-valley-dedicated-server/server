/**
 * Pure helpers for the dashboard message: building the embed, footer stamping, owner-id
 * parsing, and classifying channel messages during the adoption scan.
 */

import { type APIEmbedField, EmbedBuilder } from "discord.js";
import type { DiscordServerState } from "./discordState";
import { formatStardewDate, formatStardewTime, joinableInviteCode, type ServerStatus } from "./serverState";

export const DASHBOARD_TITLE = "Server Status";

/** Dashboards posted under this title are still adopted; the next edit gives them the current one. */
const LEGACY_DASHBOARD_TITLE = "🧑‍🌾 Stardew Valley Server Status Dashboard";

const FOOTER_ID_SEPARATOR = " • id:";

/** Footer stamp length: a UUID prefix keeps the footer short and is unique enough among the handful of deployments sharing a channel. */
const STAMP_LENGTH = 8;

/** Headline for a state: the label alone while online, otherwise with the reason. */
export function formatStateLine(state: DiscordServerState): string {
    return state.kind === "online" ? state.label : `${state.label} — ${state.detail}`;
}

/** Version strings as code chips; a version the mod has not reported yet shows a dash. */
function versionChip(version: string): string {
    return version ? `\`${version}\`` : "—";
}

/**
 * The status fields of a running server, shared by the dashboard embed and the `!status`
 * reply. Laid out like the docs site's status widget: state, players, and in-game date on
 * one row, versions and tick rate on the next, and the invite code across the full width.
 */
export function buildStatusFields(status: ServerStatus, state: DiscordServerState): APIEmbedField[] {
    const players = `**${status.playerCount}** / ${status.maxPlayers}${status.isPaused ? " _(paused)_" : ""}`;
    const date = `${formatStardewDate(status)} · ${formatStardewTime(status.timeOfDay)}`;
    const inviteCode = joinableInviteCode(status);
    return [
        { name: "Status", value: formatStateLine(state), inline: true },
        { name: "Players", value: players, inline: true },
        { name: "In-game date", value: date, inline: true },
        { name: "Image", value: versionChip(status.serverVersion), inline: true },
        { name: "Stardew", value: versionChip(status.gameVersion), inline: true },
        { name: "Average TPS", value: status.tps.toFixed(1), inline: true },
        { name: "Invite code", value: inviteCode ? `\`${inviteCode}\`` : "_not yet available_", inline: false },
    ];
}

/** The dashboard embed for a /status response (null when unreachable). */
export function buildDashboardEmbed(
    status: ServerStatus | null,
    state: DiscordServerState,
    footerText: string,
): EmbedBuilder {
    const embed = new EmbedBuilder()
        .setTitle(DASHBOARD_TITLE)
        .setColor(state.color)
        .setTimestamp()
        .setFooter({ text: footerText });

    if (!status?.isOnline) {
        const lines = [`**${state.label}** — ${state.detail}`];
        if (state.hint) {
            lines.push(`_${state.hint}_`);
        }
        return embed.setDescription(lines.join("\n"));
    }
    return embed.addFields(buildStatusFields(status, state));
}

/** "30 seconds", "1 minute", "2 minutes"; a rate that is not a whole number of minutes stays in seconds. */
export function formatRefreshRate(seconds: number): string {
    if (seconds % 60 !== 0) {
        return `${seconds} seconds`;
    }
    const minutes = seconds / 60;
    return minutes === 1 ? "1 minute" : `${minutes} minutes`;
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
 * Builds the dashboard footer text, stamped with the owner id's prefix. When `ownerId` is
 * null (degraded mode, persistence unavailable) no ownership stamp is included.
 */
export function formatFooter(refreshSeconds: number, ownerId: string | null): string {
    const base = `Automatically updates every ${formatRefreshRate(refreshSeconds)}`;
    return ownerId ? `${base}${FOOTER_ID_SEPARATOR}${ownerId.slice(0, STAMP_LENGTH)}` : base;
}

/** Extracts the ownership stamp from a footer text, or null if none is present. */
export function parseOwnerId(footerText: string | null | undefined): string | null {
    if (!footerText) {
        return null;
    }
    const index = footerText.lastIndexOf(FOOTER_ID_SEPARATOR);
    if (index === -1) {
        return null;
    }
    const id = footerText.slice(index + FOOTER_ID_SEPARATOR.length).trim();
    return id.length > 0 ? id : null;
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
