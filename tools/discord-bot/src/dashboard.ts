/**
 * Pure helpers for dashboard message ownership: footer stamping, owner-id parsing,
 * and classifying channel messages during the adoption scan.
 */

export const DASHBOARD_TITLE = "🧑‍🌾 Stardew Valley Server Status Dashboard";

const FOOTER_ID_SEPARATOR = " • id:";

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
 * Builds the dashboard footer text. When `ownerId` is null (degraded mode,
 * persistence unavailable) no ownership stamp is included.
 */
export function formatFooter(refreshRateFormatted: string, ownerId: string | null): string {
    const base = `Automatically updates every ${refreshRateFormatted}`;
    return ownerId ? `${base}${FOOTER_ID_SEPARATOR}${ownerId}` : base;
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
    return embed?.title === DASHBOARD_TITLE;
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
    return stamp === ownerId ? "mine" : "foreign";
}
