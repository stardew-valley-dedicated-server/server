<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";
import {
    formatStardewTime,
    joinableInviteCode,
    resolveServerState,
    type ServerState,
    type ServerStateKind,
    type ServerStatus,
} from "../../../tools/discord-bot/src/serverState";

const props = defineProps<{
    /** HTTPS base URL of the API; `/status` is fetched under it. */
    apiUrl: string;
    /** Poll interval in ms; 0 disables polling. */
    refreshInterval?: number;
}>();

const status = ref<ServerStatus | null>(null);
/** Header text: SERVER_NAME, else the farm name (the Discord bot's nickname rule). Kept across offline polls so the header does not flip. */
const serverName = ref("");
/** `null` until the first fetch settles. */
const state = ref<ServerState | null>(null);
const copied = ref(false);
const refreshInterval = props.refreshInterval ?? 30000;
const roundTripMs = ref<number | null>(null);
/** Epoch ms of the last settled poll; keys the refresh bar and anchors the next poll. */
const lastFetchedAt = ref(0);
/** Where the bar starts: 0 after a live poll, the elapsed time when resumed from cache. */
const cycleElapsedMs = ref(0);
let pollTimer: ReturnType<typeof setTimeout> | null = null;

/** Last successful poll, cached per API URL so a reload resumes the cycle instead of restarting it. */
interface CachedPoll {
    fetchedAt: number;
    status: ServerStatus;
    roundTripMs: number;
}
const cacheKey = `server-status:${props.apiUrl}`;

function readCache(): CachedPoll | null {
    try {
        const raw = localStorage.getItem(cacheKey);
        return raw ? (JSON.parse(raw) as CachedPoll) : null;
    } catch {
        return null;
    }
}

function writeCache(poll: CachedPoll | null) {
    try {
        if (poll) {
            localStorage.setItem(cacheKey, JSON.stringify(poll));
        } else {
            localStorage.removeItem(cacheKey);
        }
    } catch {
        // Storage unavailable (private mode, quota).
    }
}

/** The code players paste in-game; null until the lobby is published. */
const inviteCode = computed(() => (status.value ? joinableInviteCode(status.value) : null));

const stateColors: Record<ServerStateKind, string> = {
    online: "var(--vp-c-success-1)",
    busy: "var(--vp-c-warning-1)",
    loading: "var(--vp-c-warning-1)",
    provisioning: "var(--vp-c-warning-1)",
    offline: "var(--vp-c-danger-1)",
};

const playerSlots = computed(() => Math.max(0, status.value?.maxPlayers ?? 0));

/** Seats spread evenly over the fewest rows that fit this many per row. */
const MAX_SLOTS_PER_ROW = 16;
const playerSlotColumns = computed(() => {
    const rows = Math.ceil(playerSlots.value / MAX_SLOTS_PER_ROW);
    return rows > 0 ? Math.ceil(playerSlots.value / rows) : 0;
});

const farmDate = computed(() => {
    if (!status.value) {
        return "";
    }
    const season = status.value.season ? status.value.season[0].toUpperCase() + status.value.season.slice(1) : "";
    return `${season} ${status.value.day}, Year ${status.value.year}`;
});

const clock = computed(() => (status.value ? formatStardewTime(status.value.timeOfDay) : ""));

/** Coarse uptime ("3d 4h", "4h 12m", "12m"); precise seconds would only churn between polls. */
function formatUptime(startedAtUtc: string, now: number): string {
    const totalMinutes = Math.max(0, Math.floor((now - Date.parse(startedAtUtc)) / 60000));
    const days = Math.floor(totalMinutes / 1440);
    const hours = Math.floor((totalMinutes % 1440) / 60);
    const minutes = totalMinutes % 60;
    if (days > 0) {
        return `${days}d ${hours}h`;
    }
    if (hours > 0) {
        return `${hours}h ${minutes}m`;
    }
    return `${minutes}m`;
}

/** Uptime shown inside the status badge while online; "" otherwise. */
const uptime = computed(() =>
    status.value?.isOnline && status.value.startedAtUtc
        ? formatUptime(status.value.startedAtUtc, lastFetchedAt.value)
        : "",
);

const vitals = computed(() => {
    if (!status.value) {
        return [];
    }
    const items = [
        {
            key: "tps",
            iconPath: "M12 20V10M18 20V4M6 20v-4",
            value: status.value.tps.toFixed(1),
            unit: "TPS",
            info: "Game ticks per second the server actually runs, averaged over the last 30 seconds.",
        },
    ];
    if (roundTripMs.value !== null) {
        items.push({
            key: "rtt",
            iconPath: "M3 12h4l3-8 4 16 3-8h4",
            value: String(roundTripMs.value),
            unit: "ms",
            info: "Round-trip from your browser to the server's API. A rough distance hint, not your in-game ping.",
        });
    }
    return items;
});

/** Applies a settled poll (live or cached; null when the server was unreachable) and schedules the next one. */
function applyPoll(poll: CachedPoll | null) {
    const fetchedAt = poll?.fetchedAt ?? Date.now();
    status.value = poll?.status ?? null;
    const name = poll?.status.serverName || poll?.status.farmName;
    if (name) {
        serverName.value = name;
    }
    state.value = resolveServerState(status.value);
    roundTripMs.value = poll?.roundTripMs ?? null;
    lastFetchedAt.value = fetchedAt;
    cycleElapsedMs.value = Date.now() - fetchedAt;
    writeCache(poll);
    if (refreshInterval > 0) {
        pollTimer = setTimeout(fetchStatus, Math.max(0, fetchedAt + refreshInterval - Date.now()));
    }
}

// A hung request must not settle after a newer one and overwrite its result.
let latestRequest = 0;

async function fetchStatus() {
    const request = ++latestRequest;
    let poll: CachedPoll | null = null;
    try {
        const startedAt = performance.now();
        const response = await fetch(`${props.apiUrl.replace(/\/+$/, "")}/status`, { cache: "no-store" });
        if (response.ok) {
            const data = (await response.json()) as ServerStatus;
            poll = { fetchedAt: Date.now(), status: data, roundTripMs: Math.round(performance.now() - startedAt) };
        }
    } catch {
        // Unreachable: applied as offline.
    }
    if (request === latestRequest) {
        applyPoll(poll);
    }
}

async function copyInviteCode() {
    const code = inviteCode.value;
    if (!code) {
        return;
    }
    try {
        await navigator.clipboard.writeText(code);
    } catch {
        // Fallback for browsers without the async clipboard API.
        const textarea = document.createElement("textarea");
        textarea.value = code;
        document.body.appendChild(textarea);
        textarea.select();
        document.execCommand("copy");
        document.body.removeChild(textarea);
    }
    copied.value = true;
    setTimeout(() => {
        copied.value = false;
    }, 2000);
}

onMounted(() => {
    const cached = readCache();
    if (cached && Date.now() - cached.fetchedAt < refreshInterval) {
        applyPoll(cached);
    } else {
        fetchStatus();
    }
});

onUnmounted(() => {
    latestRequest++; // a poll still in flight must not reschedule after disposal
    if (pollTimer) {
        clearTimeout(pollTimer);
    }
});
</script>

<template>
    <div class="server-status-widget">
        <div
            :key="lastFetchedAt"
            class="refresh-bar"
            :class="{ filling: refreshInterval > 0 && lastFetchedAt > 0 }"
            :style="{ '--refresh-interval': `${refreshInterval}ms`, '--cycle-elapsed': `${cycleElapsedMs}ms` }"
        ></div>

        <div v-if="!state" class="loading">
            <div class="spinner"></div>
            <span>Connecting to server...</span>
        </div>

        <template v-else>
            <div class="header">
                <div class="server-info">
                    <div>
                        <h3 class="server-name">{{ serverName || "Server Status" }}</h3>
                        <p v-if="status?.serverVersion" class="version">
                            <span class="version-chip">
                                <span class="stat-label">Image</span>
                                <code>{{ status.serverVersion }}</code>
                            </span>
                            <span v-if="status.gameVersion" class="version-chip">
                                <span class="stat-label">Stardew</span>
                                <code>{{ status.gameVersion }}</code>
                            </span>
                        </p>
                    </div>
                    <div class="status">
                        <div class="status-badge" :style="{ '--status-color': stateColors[state.kind] }">
                            <span class="status-dot"></span>
                            <span class="status-text">{{ state.label }}</span>
                            <span v-if="uptime" class="status-uptime" title="Time since the server process last started">· {{ uptime }}</span>
                        </div>
                        <p v-if="status?.isOnline" class="vitals">
                            <span v-for="vital in vitals" :key="vital.key" class="vital">
                                <svg class="stat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                    <path :d="vital.iconPath" />
                                </svg>
                                <span class="value">{{ vital.value }} <span class="unit">{{ vital.unit }}</span></span>
                                <svg class="info-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" role="img">
                                    <title>{{ vital.info }}</title>
                                    <circle cx="12" cy="12" r="10" />
                                    <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3M12 17h.01" />
                                </svg>
                            </span>
                        </p>
                    </div>
                </div>
            </div>

            <p v-if="state.kind !== 'online'" class="note">{{ state.detail }}</p>

            <template v-if="status?.isOnline">
                <div class="stats">
                    <div class="stat">
                        <span class="stat-label">
                            <svg class="stat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                <circle cx="12" cy="8" r="4" />
                                <path d="M20 21a8 8 0 0 0-16 0" />
                            </svg>
                            Players
                        </span>
                        <div class="player-bar-container">
                            <div class="player-slots" :style="{ '--columns': playerSlotColumns }">
                                <span
                                    v-for="slot in playerSlots"
                                    :key="slot"
                                    class="player-slot"
                                    :class="{ filled: slot <= status.playerCount }"
                                ></span>
                            </div>
                            <span class="player-count">
                                {{ status.playerCount }}<span class="player-max">/{{ status.maxPlayers }}</span>
                            </span>
                        </div>
                    </div>

                    <div class="stat">
                        <span class="stat-label">
                            <svg class="stat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                <rect x="3" y="4" width="18" height="18" rx="2" />
                                <path d="M16 2v4M8 2v4M3 10h18" />
                            </svg>
                            In-game date
                        </span>
                        <span class="farm-date">{{ farmDate }} <span class="clock">· {{ clock }}</span></span>
                    </div>
                </div>

                <div class="stats">
                    <div
                        class="stat"
                        :class="{ copyable: inviteCode, copied }"
                        :role="inviteCode ? 'button' : undefined"
                        :tabindex="inviteCode ? 0 : undefined"
                        :title="inviteCode ? (copied ? 'Copied!' : 'Copy invite code') : undefined"
                        @click="copyInviteCode()"
                        @keydown.enter.prevent="copyInviteCode()"
                        @keydown.space.prevent="copyInviteCode()"
                    >
                        <span class="stat-label">
                            <svg class="stat-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                <path d="m21 2-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0 3 3L22 7l-3-3m-3.5 3.5L19 4" />
                            </svg>
                            Invite code
                        </span>
                        <div class="invite-code-row">
                            <code v-if="inviteCode" class="invite-code">{{ inviteCode }}</code>
                            <span v-else class="invite-code pending">not yet available</span>
                            <span v-if="inviteCode" class="copy-icon" aria-hidden="true">
                                <svg v-if="copied" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                    <path d="M20 6 9 17l-5-5" />
                                </svg>
                                <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                                    <rect x="9" y="9" width="13" height="13" rx="2" />
                                    <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
                                </svg>
                            </span>
                        </div>
                    </div>
                </div>
            </template>
            <p v-else class="hint">{{ state.hint }}</p>
        </template>
    </div>
</template>

<style scoped>
.server-status-widget {
    position: relative;
    background: linear-gradient(
        135deg,
        color-mix(in srgb, var(--vp-c-bg-soft) 95%, var(--vp-c-brand-3) 5%),
        color-mix(in srgb, var(--vp-c-bg-soft) 90%, var(--vp-c-brand-1) 10%)
    );
    border-radius: 12px;
    padding: 24px;
    margin: 20px 0;
    overflow: hidden;
    --transition: 0.25s;
}

/* Fills over one poll interval; the negative delay resumes mid-cycle after a reload. */
.refresh-bar {
    position: absolute;
    bottom: 0;
    left: 0;
    width: 100%;
    height: 3px;
    /* Mirror-symmetric so the scrolling tiles meet without a seam. */
    background: linear-gradient(
        90deg,
        var(--vp-c-brand-3),
        var(--vp-c-brand-1),
        var(--vp-c-brand-2),
        #fff,
        var(--vp-c-brand-2),
        var(--vp-c-brand-1),
        var(--vp-c-brand-3)
    );
    background-size: 200% 100%;
    /* Prime-ish durations so the shimmer, breath, and drift never line up into a visible loop. */
    animation:
        refresh-shimmer 7s linear infinite,
        refresh-breathe 5.3s ease-in-out infinite;
}

/* Light spilling above the line, fading out on every edge with gradients (a blur or mask would clip hard at the box).
   Two overlapping bands drift along it at different speeds and interfere into slow waves. */
.refresh-bar::before {
    content: "";
    position: absolute;
    inset: -18px 0 0;
    background:
        radial-gradient(190px 22px at 50% 100%, color-mix(in srgb, var(--vp-c-brand-2) 32%, transparent), transparent 70%) 0 0 / 260px 100% repeat-x,
        radial-gradient(290px 19px at 50% 100%, color-mix(in srgb, #fff 22%, transparent), transparent 70%) 0 0 / 410px 100% repeat-x,
        linear-gradient(
            0deg,
            color-mix(in srgb, var(--vp-c-brand-1) 38%, transparent),
            color-mix(in srgb, var(--vp-c-brand-1) 16%, transparent) 30%,
            color-mix(in srgb, var(--vp-c-brand-2) 5%, transparent) 65%,
            transparent
        );
    /* Percentage stops only: fixed-length stops fall out of order while the fill is still narrow and collapse the fades into hard edges.
       The spill ends short of the tip with a tight fade, so it trails the tip instead of wrapping around it. */
    mask-image: linear-gradient(
        90deg,
        transparent,
        rgba(0, 0, 0, 0.45) 45%,
        #000 68%,
        rgba(0, 0, 0, 0.85) 78%,
        rgba(0, 0, 0, 0.55) 85%,
        rgba(0, 0, 0, 0.25) 90%,
        rgba(0, 0, 0, 0.08) 94%,
        transparent 97%
    );
    animation: refresh-drift 11s linear infinite;
    pointer-events: none;
}

/* Fades the spill in over the first stretch of each poll cycle, on the fill's own clock, so a
   few-pixel fill does not show a blob of spill in the corner. Breathing comes from the parent's opacity. */
.refresh-bar.filling::before {
    animation:
        refresh-drift 11s linear infinite,
        refresh-spill-in var(--refresh-interval) linear calc(-1 * var(--cycle-elapsed)) forwards;
}

@keyframes refresh-shimmer {
    from { background-position: 200% 0; }
    to { background-position: 0% 0; }
}

@keyframes refresh-breathe {
    0%, 100% {
        box-shadow:
            0 0 3px color-mix(in srgb, var(--vp-c-brand-1) 90%, #fff),
            0 0 8px color-mix(in srgb, var(--vp-c-brand-1) 60%, transparent),
            0 0 18px color-mix(in srgb, var(--vp-c-brand-2) 35%, transparent),
            0 0 36px color-mix(in srgb, var(--vp-c-brand-3) 18%, transparent);
        opacity: 0.9;
    }
    50% {
        box-shadow:
            0 0 4px color-mix(in srgb, var(--vp-c-brand-1) 95%, #fff),
            0 0 12px color-mix(in srgb, var(--vp-c-brand-1) 80%, transparent),
            0 0 26px color-mix(in srgb, var(--vp-c-brand-2) 55%, transparent),
            0 0 48px color-mix(in srgb, var(--vp-c-brand-3) 32%, transparent);
        opacity: 1;
    }
}

/* Faint while the line is still short, ramping up once it has some length behind the tip. */
@keyframes refresh-spill-in {
    from { opacity: 0.15; }
    15% { opacity: 0.15; }
    40% { opacity: 1; }
    to { opacity: 1; }
}

/* Moves the two pocket layers at different rates; the base fade stays put. */
@keyframes refresh-drift {
    from { background-position: 0 0, 0 0, 0 0; }
    to { background-position: 260px 0, -410px 0, 0 0; }
}

.refresh-bar.filling {
    animation:
        refresh-fill var(--refresh-interval) linear calc(-1 * var(--cycle-elapsed)) forwards,
        refresh-shimmer 7s linear infinite,
        refresh-breathe 5.3s ease-in-out infinite;
}

@keyframes refresh-fill {
    from { width: 0; }
    to { width: 100%; }
}

@media (prefers-reduced-motion: reduce) {
    .refresh-bar,
    .refresh-bar.filling,
    .refresh-bar::before,
    .refresh-bar.filling::before,
    .status-dot {
        animation: none;
    }
}

.loading {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 12px;
    padding: 32px;
    color: var(--vp-c-text-2);
}

.spinner {
    width: 24px;
    height: 24px;
    border: 3px solid var(--vp-c-divider);
    border-top-color: var(--vp-c-brand-1);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
}

@keyframes spin {
    to { transform: rotate(360deg); }
}

.header {
    margin-bottom: 16px;
}

.server-info {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 16px;
    flex-wrap: wrap;
}

.server-name {
    margin: 0;
    font-size: 20px;
    line-height: 24px;
    font-weight: 700;
    color: var(--vp-c-text-1);
    letter-spacing: -0.02em;
}

.status {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
}

/* Title line height, so the badge sits on the title row. */
.status-badge {
    display: flex;
    align-items: center;
    gap: 6px;
    height: 24px;
    padding: 0 10px;
    background: color-mix(in srgb, var(--status-color) 12%, transparent);
    border: 1px solid color-mix(in srgb, var(--status-color) 30%, transparent);
    border-radius: 12px;
    font-size: 12px;
    font-weight: 600;
    color: var(--status-color);
}

.status-uptime {
    font-weight: 500;
    opacity: 0.8;
}

.status-dot {
    width: 8px;
    height: 8px;
    background: var(--status-color);
    border-radius: 50%;
    box-shadow: 0 0 8px var(--status-color);
    animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
    0%, 100% { opacity: 1; transform: scale(1); }
    50% { opacity: 0.6; transform: scale(0.9); }
}

.note,
.hint {
    margin: 0 0 16px;
    font-size: 14px;
    color: var(--vp-c-text-2);
}

.hint {
    margin-bottom: 0;
    font-style: italic;
}

.version,
.vitals {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
    margin: 16px 0 0;
    line-height: 1;
}

.vitals {
    gap: 12px;
    height: 20px;
}

.vital {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    color: var(--vp-c-text-2);
}

.vital .value {
    font-size: 12px;
}

.vital .stat-icon {
    width: 12px;
    height: 12px;
}

.info-icon {
    width: 13px;
    height: 13px;
    margin-left: 1px;
    color: var(--vp-c-text-3);
    cursor: help;
    transition: color var(--transition);
}

.info-icon:hover {
    color: var(--vp-c-brand-1);
}

/* Two-tone pill: darker label half, lighter value half. */
.version-chip {
    display: inline-flex;
    align-items: stretch;
    border-radius: 4px;
    overflow: hidden;
}

.version-chip .stat-label {
    padding: 3px 8px;
    background: var(--vp-c-bg);
}

.version-chip code {
    padding: 3px 8px;
    border-radius: 0; /* the theme rounds every <code> */
    background: color-mix(in srgb, var(--vp-c-bg-soft) 75%, var(--vp-c-text-3));
    font-family: var(--vp-font-family-mono);
    font-size: 12px;
    line-height: 14px;
    font-weight: 600;
    color: var(--vp-c-text-1);
}

.stats {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 16px;
    margin-bottom: 16px;
}

.stats:last-child {
    margin-bottom: 0;
}

@media (max-width: 480px) {
    .stats {
        grid-template-columns: 1fr;
    }
}

.stat {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-width: 0; /* equal grid columns regardless of content */
    padding: 14px 16px;
    background: color-mix(in srgb, var(--vp-c-bg) 60%, transparent);
    border: 1px solid var(--vp-c-divider);
    border-radius: 8px;
}

/* The whole invite-code card copies; its icons light up on hover or focus, and the check turns green once copied. */
.stat.copyable {
    cursor: pointer;
    user-select: none;
    transition: background var(--transition);
}

/* Hover deepens the panel's own color a step, so it stays readable while the copied check is green without adding a hue. */
.stat.copyable:hover,
.stat.copyable:focus-visible {
    background: color-mix(in srgb, var(--vp-c-bg) 80%, transparent);
}

.stat.copyable:hover .stat-icon,
.stat.copyable:hover .copy-icon,
.stat.copyable:focus-visible .stat-icon,
.stat.copyable:focus-visible .copy-icon {
    color: var(--vp-c-brand-1);
}

.stat.copyable:focus-visible {
    outline: 2px solid var(--vp-c-brand-1);
    outline-offset: 2px;
}

/* Copied wins over the hover and focus highlight. */
.stat.copied .copy-icon,
.stat.copied:hover .copy-icon,
.stat.copied:focus-visible .copy-icon {
    color: var(--vp-c-success-1);
}

.stat-label {
    display: flex;
    align-items: center;
    gap: 6px;
    font-size: 11px;
    font-weight: 600;
    color: var(--vp-c-text-2);
    text-transform: uppercase;
    letter-spacing: 0.08em;
}

.stat-icon {
    width: 14px;
    height: 14px;
    flex-shrink: 0;
    transition: color var(--transition);
}

.player-bar-container {
    display: flex;
    align-items: center;
    gap: 12px;
}

.player-slots {
    flex: 1;
    min-width: 0;
    display: grid;
    grid-template-columns: repeat(var(--columns), 1fr);
    gap: 4px;
}

.player-slot {
    height: 6px;
    background: var(--vp-c-divider);
    border-radius: 3px;
    transition: background var(--transition);
}

.player-slot.filled {
    background: linear-gradient(90deg, var(--vp-c-brand-1), var(--vp-c-brand-2));
}

.player-count {
    font-size: 14px;
    font-weight: 700;
    line-height: 1;
    color: var(--vp-c-text-1);
    font-variant-numeric: tabular-nums;
}

.player-max {
    font-weight: 500;
    color: var(--vp-c-text-2);
}

.farm-date,
.value {
    font-size: 14px;
    font-weight: 600;
    color: var(--vp-c-text-1);
}

.value {
    font-variant-numeric: tabular-nums;
}

.clock,
.unit {
    font-weight: 500;
    color: var(--vp-c-text-2);
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
}

.invite-code-row {
    display: flex;
    align-items: center;
    gap: 8px;
}

.invite-code {
    flex: 1;
    padding: 0;
    background: none;
    font-family: var(--vp-font-family-mono);
    font-size: 14px;
    font-weight: 600;
    color: var(--vp-c-text-1);
    letter-spacing: 0.04em;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.invite-code.pending {
    color: var(--vp-c-text-3);
    font-family: inherit;
    font-style: italic;
    letter-spacing: normal;
}

.copy-icon {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 32px;
    height: 32px;
    color: var(--vp-c-text-2);
    transition: color var(--transition);
}

.copy-icon svg {
    width: 18px;
    height: 18px;
}
</style>
