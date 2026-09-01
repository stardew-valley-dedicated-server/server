/**
 * Pure helpers for dashboard message ownership: footer stamping, owner-id parsing,
 * and classifying channel messages during the adoption scan.
 */

export const DASHBOARD_TITLE = "🧑‍🌾 Stardew Valley Server Status Dashboard";

const FOOTER_ID_SEPARATOR = " • id:";

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
