<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from "vue";

/** Subset of the server's `/status` response the widget renders. */
interface ServerStatus {
    isOnline: boolean;
    playerCount: number;
    maxPlayers: number;
    steamInviteCode: string | null;
    gogInviteCode: string | null;
    farmName: string;
    season: string;
    day: number;
    year: number;
    lastUpdated: string;
}

type WidgetState = "loading" | "live" | "offline" | "stale" | "unreachable";
type CodeKey = "steamInviteCode" | "gogInviteCode";

const props = defineProps<{
    /** HTTPS URL that returns the `/status` JSON. */
    apiUrl: string;
    /** Header text; the farm name is not a display name. */
    title?: string;
    /** Poll interval in ms; 0 disables polling. */
    refreshInterval?: number;
}>();

/** A successful fetch whose snapshot is older than this means the server stopped refreshing it. */
const STALE_THRESHOLD_MS = 120_000;

const status = ref<ServerStatus | null>(null);
const state = ref<WidgetState>("loading");
const copiedKey = ref<CodeKey | null>(null);
let refreshTimer: ReturnType<typeof setInterval> | null = null;

const inviteCodes: { key: CodeKey; label: string }[] = [
    { key: "steamInviteCode", label: "Steam invite code" },
    { key: "gogInviteCode", label: "GOG invite code" },
];

const isFull = computed(
    () => status.value !== null && status.value.maxPlayers > 0 && status.value.playerCount >= status.value.maxPlayers,
);

const playerPercentage = computed(() => {
    if (!status.value || status.value.maxPlayers <= 0) {
        return 0;
    }
    return Math.min(100, (status.value.playerCount / status.value.maxPlayers) * 100);
});

const statusColor = computed(() => {
    switch (state.value) {
        case "live":
            return isFull.value ? "var(--vp-c-warning-1)" : "var(--vp-c-success-1)";
        case "stale":
            return "var(--vp-c-warning-1)";
        default:
            return "var(--vp-c-danger-1)";
    }
});

const statusText = computed(() => {
    switch (state.value) {
        case "live":
            return isFull.value ? "Full" : "Online";
        case "stale":
            return "Stale";
        default:
            return "Offline";
    }
});

const farmDate = computed(() => {
    if (!status.value) {
        return "";
    }
    const season = status.value.season ? status.value.season[0].toUpperCase() + status.value.season.slice(1) : "";
    return `${status.value.farmName} Farm, ${season} ${status.value.day}, Year ${status.value.year}`;
});

function classify(data: ServerStatus): WidgetState {
    if (!data.isOnline) {
        return "offline";
    }
    const capturedAt = Date.parse(data.lastUpdated);
    if (Number.isNaN(capturedAt) || Date.now() - capturedAt > STALE_THRESHOLD_MS) {
        return "stale";
    }
    return "live";
}

// A hung request must not settle after a newer one and overwrite its result.
let latestRequest = 0;

async function fetchStatus() {
    const request = ++latestRequest;
    let data: ServerStatus | null = null;
    try {
        const response = await fetch(props.apiUrl, { cache: "no-store" });
        if (response.ok) {
            data = (await response.json()) as ServerStatus;
        }
    } catch {
        data = null;
    }
    if (request !== latestRequest) {
        return;
    }
    status.value = data;
    state.value = data ? classify(data) : "unreachable";
}

async function copyInviteCode(key: CodeKey) {
    const code = status.value?.[key];
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
    copiedKey.value = key;
    setTimeout(() => {
        if (copiedKey.value === key) {
            copiedKey.value = null;
        }
    }, 2000);
}

onMounted(() => {
    fetchStatus();
    const interval = props.refreshInterval ?? 30000;
    if (interval > 0) {
        refreshTimer = setInterval(fetchStatus, interval);
    }
});

onUnmounted(() => {
    if (refreshTimer) {
        clearInterval(refreshTimer);
    }
});
</script>

<template>
    <div class="server-status-widget">
        <div v-if="state === 'loading'" class="loading">
            <div class="spinner"></div>
            <span>Connecting to server...</span>
        </div>

        <div v-else-if="state === 'unreachable'" class="error">
            <span class="error-icon">!</span>
            <span>Status unavailable. The server could not be reached.</span>
        </div>

        <template v-else-if="status">
            <div class="header">
                <div class="server-info">
                    <h3 class="server-name">{{ props.title ?? "Server Status" }}</h3>
                    <div class="status-badge" :style="{ '--status-color': statusColor }">
                        <span class="status-dot"></span>
                        <span class="status-text">{{ statusText }}</span>
                    </div>
                </div>
            </div>

            <p v-if="state === 'offline'" class="note">The server is running but no farm is loaded yet.</p>
            <p v-else-if="state === 'stale'" class="note">
                The server stopped refreshing its status; the numbers below may be outdated.
            </p>

            <template v-if="state !== 'offline'">
                <div class="stats">
                    <div class="stat">
                        <span class="stat-label">Players</span>
                        <div class="player-bar-container">
                            <div class="player-bar">
                                <div class="player-bar-fill" :style="{ width: `${playerPercentage}%` }"></div>
                            </div>
                            <span class="player-count">{{ status.playerCount }}/{{ status.maxPlayers }}</span>
                        </div>
                    </div>

                    <div class="stat">
                        <span class="stat-label">In-game date</span>
                        <span class="farm-date">{{ farmDate }}</span>
                    </div>
                </div>

                <div class="stats">
                    <div v-for="code in inviteCodes" :key="code.key" class="stat">
                        <span class="stat-label">{{ code.label }}</span>
                        <div class="invite-code-row">
                            <code v-if="status[code.key]" class="invite-code">{{ status[code.key] }}</code>
                            <span v-else class="invite-code pending">not yet available</span>
                            <button
                                v-if="status[code.key]"
                                class="copy-btn"
                                :class="{ copied: copiedKey === code.key }"
                                :title="copiedKey === code.key ? 'Copied!' : `Copy ${code.label}`"
                                @click="copyInviteCode(code.key)"
                            >
                                <span v-if="copiedKey === code.key">&#10003;</span>
                                <span v-else>&#128203;</span>
                            </button>
                        </div>
                    </div>
                </div>
            </template>
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
    border: 1px solid color-mix(in srgb, var(--vp-c-divider) 50%, var(--vp-c-brand-3) 50%);
    border-radius: 16px;
    padding: 24px;
    margin: 20px 0;
    overflow: hidden;
}

/* Subtle gradient accent line at top */
.server-status-widget::before {
    content: "";
    position: absolute;
    top: 0;
    left: 0;
    right: 0;
    height: 3px;
    background: linear-gradient(90deg, var(--vp-c-brand-1), var(--vp-c-brand-2), var(--vp-c-brand-3));
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

.error {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 10px;
    padding: 32px;
    color: var(--vp-c-danger-1);
}

.error-icon {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 28px;
    height: 28px;
    background: var(--vp-c-danger-soft);
    border-radius: 50%;
    font-weight: bold;
    font-size: 14px;
}

.header {
    margin-bottom: 20px;
}

.server-info {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    flex-wrap: wrap;
}

.server-name {
    margin: 0;
    font-size: 20px;
    font-weight: 700;
    color: var(--vp-c-text-1);
    letter-spacing: -0.02em;
}

.status-badge {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 14px;
    background: color-mix(in srgb, var(--status-color) 12%, transparent);
    border: 1px solid color-mix(in srgb, var(--status-color) 30%, transparent);
    border-radius: 24px;
    font-size: 13px;
    font-weight: 600;
    color: var(--status-color);
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

.note {
    margin: 0 0 16px;
    font-size: 14px;
    color: var(--vp-c-text-2);
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
    padding: 14px 16px;
    background: color-mix(in srgb, var(--vp-c-bg) 60%, transparent);
    border: 1px solid var(--vp-c-divider);
    border-radius: 12px;
}

.stat-label {
    font-size: 11px;
    font-weight: 600;
    color: var(--vp-c-text-3);
    text-transform: uppercase;
    letter-spacing: 0.08em;
}

.player-bar-container {
    display: flex;
    align-items: center;
    gap: 12px;
}

.player-bar {
    flex: 1;
    height: 6px;
    background: var(--vp-c-divider);
    border-radius: 3px;
    overflow: hidden;
}

.player-bar-fill {
    height: 100%;
    background: linear-gradient(90deg, var(--vp-c-brand-1), var(--vp-c-brand-2));
    border-radius: 3px;
    transition: width 0.5s cubic-bezier(0.4, 0, 0.2, 1);
}

.player-count {
    font-size: 15px;
    font-weight: 700;
    color: var(--vp-c-text-1);
    font-variant-numeric: tabular-nums;
}

.farm-date {
    font-size: 15px;
    font-weight: 600;
    color: var(--vp-c-text-1);
}

.invite-code-row {
    display: flex;
    align-items: center;
    gap: 8px;
}

.invite-code {
    flex: 1;
    padding: 8px 12px;
    background: var(--vp-c-bg);
    border: 1px solid var(--vp-c-divider);
    border-radius: 8px;
    font-family: var(--vp-font-family-mono);
    font-size: 13px;
    font-weight: 500;
    color: var(--vp-c-brand-1);
    letter-spacing: 0.05em;
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

.copy-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 36px;
    height: 36px;
    background: var(--vp-c-bg);
    border: 1px solid var(--vp-c-divider);
    border-radius: 8px;
    cursor: pointer;
    transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
    font-size: 15px;
    color: var(--vp-c-text-2);
}

.copy-btn:hover {
    border-color: var(--vp-c-brand-1);
    background: var(--vp-c-brand-soft);
    color: var(--vp-c-brand-1);
    transform: scale(1.05);
}

.copy-btn.copied {
    background: var(--vp-c-success-soft);
    border-color: var(--vp-c-success-1);
    color: var(--vp-c-success-1);
}
</style>
