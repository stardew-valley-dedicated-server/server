<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from "vue";

interface ServerStatus {
    serverName: string;
    currentPlayers: number;
    maxPlayers: number;
    inviteCode: string;
    isOnline: boolean;
}

const props = defineProps<{
    apiUrl: string;
    refreshInterval?: number;
}>();

const status = ref<ServerStatus | null>(null);
const isLoading = ref(true);
const error = ref<string | null>(null);
const copied = ref(false);
let refreshTimer: ReturnType<typeof setInterval> | null = null;

const playerPercentage = computed(() => {
    if (!status.value) return 0;
    return (status.value.currentPlayers / status.value.maxPlayers) * 100;
});

const statusColor = computed(() => {
    if (!status.value?.isOnline) return "var(--vp-c-danger-1)";
    if (status.value.currentPlayers >= status.value.maxPlayers) return "var(--vp-c-warning-1)";
    return "var(--vp-c-success-1)";
});

const statusText = computed(() => {
    if (!status.value?.isOnline) return "Offline";
    if (status.value.currentPlayers >= status.value.maxPlayers) return "Full";
    return "Online";
});


async function fetchStatus() {
    // TODO: Replace mock with real API call
    // const response = await fetch(props.apiUrl);
    // const data = await response.json();

    // Mock response for development
    await new Promise((resolve) => setTimeout(resolve, 500)); // Simulate network delay

    status.value = {
        serverName: "JunimoServer Public Test",
        currentPlayers: 3,
        maxPlayers: 8,
        inviteCode: "SG8ZJXDNMTK4",
        isOnline: true,
    };
    error.value = null;
    isLoading.value = false;
}

async function copyInviteCode() {
    if (!status.value?.inviteCode) return;
    try {
        await navigator.clipboard.writeText(status.value.inviteCode);
        copied.value = true;
        setTimeout(() => (copied.value = false), 2000);
    } catch {
        // Fallback for older browsers
        const textarea = document.createElement("textarea");
        textarea.value = status.value.inviteCode;
        document.body.appendChild(textarea);
        textarea.select();
        document.execCommand("copy");
        document.body.removeChild(textarea);
        copied.value = true;
        setTimeout(() => (copied.value = false), 2000);
    }
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
        <!-- Loading State -->
        <div v-if="isLoading" class="loading">
            <div class="spinner"></div>
            <span>Connecting to server...</span>
        </div>

        <!-- Error State -->
        <div v-else-if="error" class="error">
            <span class="error-icon">!</span>
            <span>{{ error }}</span>
        </div>

        <!-- Server Info -->
        <template v-else-if="status">
            <div class="header">
                <div class="server-info">
                    <h3 class="server-name">{{ status.serverName }}</h3>
                    <div class="status-badge" :style="{ '--status-color': statusColor }">
                        <span class="status-dot"></span>
                        <span class="status-text">{{ statusText }}</span>
                    </div>
                </div>
            </div>

            <div class="stats">
                <div class="stat">
                    <span class="stat-label">Players</span>
                    <div class="player-bar-container">
                        <div class="player-bar">
                            <div
                                class="player-bar-fill"
                                :style="{ width: `${playerPercentage}%` }"
                            ></div>
                        </div>
                        <span class="player-count">{{ status.currentPlayers }}/{{ status.maxPlayers }}</span>
                    </div>
                </div>

                <div class="stat">
                    <span class="stat-label">Invite Code</span>
                    <div class="invite-code-row">
                        <code class="invite-code">{{ status.inviteCode || "N/A" }}</code>
                        <button
                            v-if="status.inviteCode"
                            class="copy-btn"
                            :class="{ copied }"
                            @click="copyInviteCode"
                            :title="copied ? 'Copied!' : 'Copy invite code'"
                        >
                            <span v-if="copied">&#10003;</span>
                            <span v-else>&#128203;</span>
                        </button>
                    </div>
                </div>
            </div>

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

.stats {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 16px;
    margin-bottom: 20px;
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
