import { mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import {
    ActivityType,
    Client,
    DiscordAPIError,
    DiscordjsError,
    DiscordjsErrorCodes,
    EmbedBuilder,
    Events,
    GatewayCloseCodes,
    GatewayIntentBits,
    type Message,
    PermissionFlagsBits,
    RESTJSONErrorCodes,
    type TextChannel,
} from "discord.js";
import {
    classifyDashboardEmbed,
    DASHBOARD_TITLE,
    type DashboardMessageKind,
    formatFooter,
    parseOwnerId,
} from "./dashboard";
import { createLogger, log } from "./log";
import { resolveServerState, type StatusSignals } from "./serverState";

// Configuration from environment
const DISCORD_BOT_TOKEN = process.env.DISCORD_BOT_TOKEN;
const API_URL = process.env.API_URL || "http://server:8080";
const API_KEY = process.env.API_KEY || "";
const WS_URL = process.env.WS_URL || `${API_URL.replace("http://", "ws://").replace("https://", "wss://")}/ws`;
const DISCORD_CHAT_CHANNEL_ID = process.env.DISCORD_CHAT_CHANNEL_ID;
const STATUS_DASHBOARD_CHANNEL_ID = process.env.STATUS_DASHBOARD_CHANNEL_ID;
const STATUS_DASHBOARD_REFRESH_RATE = Number(process.env.STATUS_DASHBOARD_REFRESH_RATE) || 30;
const STATUS_DASHBOARD_REFRESH_RATE_FORMATTED =
    STATUS_DASHBOARD_REFRESH_RATE < 60
        ? `${STATUS_DASHBOARD_REFRESH_RATE} seconds`
        : `${Math.round(STATUS_DASHBOARD_REFRESH_RATE / 60)} minutes`;
const DISCORD_BOT_NICKNAME = process.env.DISCORD_BOT_NICKNAME;
const COOLDOWN_DURATION_MS = 30000;
const MAX_COMMANDS_PER_WINDOW = 10;
const commandHistory = new Map<string, number[]>();

// Discord rate limit for presence updates is ~5 per 20 seconds.
// 30 seconds is a safe default that won't trigger rate limits.
const MIN_UPDATE_INTERVAL_MS = 20_000;
const UPDATE_INTERVAL_MS = Math.max(
    Number.parseInt(process.env.UPDATE_INTERVAL_MS || "30000", 10),
    MIN_UPDATE_INTERVAL_MS,
);

if (!DISCORD_BOT_TOKEN) {
    log.info("DISCORD_BOT_TOKEN not set - bot disabled");
    process.exit(0);
}

/**
 * Returns headers for API requests, including Authorization if API_KEY is set.
 */
function getApiHeaders(): HeadersInit {
    const headers: HeadersInit = {};
    if (API_KEY) {
        headers.Authorization = `Bearer ${API_KEY}`;
    }
    return headers;
}

interface ServerStatus extends StatusSignals {
    playerCount: number;
    maxPlayers: number;
    steamInviteCode: string | null;
    gogInviteCode: string | null;
    serverVersion: string;
    lastUpdated: string;
    farmName: string;
    day: number;
    season: string;
    year: number;
    timeOfDay: number;
    farmTypeKey: string;
    isPaused: boolean;
}

interface PlayerInfo {
    id: number;
    name: string;
    isOnline: boolean;
}

interface PlayersResponse {
    players: PlayerInfo[];
}

interface StatsResponse {
    fps: number;
    tps: number;
    targetTps: number;
    avgTickMs: number;
    memoryMb: number;
    pendingActions: number;
}

interface SettingsResponse {
    game: {
        farmName: string;
        farmType: number;
        profitMargin: number;
        startingCabins: number;
        spawnMonstersAtNight: string;
    };
    server: {
        maxPlayers: number;
        cabinStrategy: string;
        separateWallets: boolean;
        existingCabinBehavior: string;
    };
}

interface CabinsResponse {
    strategy: string;
    totalCount: number;
    assignedCount: number;
    availableCount: number;
}
interface WebSocketMessage {
    type: string;
    payload?: {
        playerName?: string;
        message?: string;
        timestamp?: string;
    };
}

// Only request message intents if chat relay is enabled
const intents = [GatewayIntentBits.Guilds];
if (DISCORD_CHAT_CHANNEL_ID) {
    intents.push(GatewayIntentBits.GuildMessages, GatewayIntentBits.MessageContent);
}

const client = new Client({ intents });

// WebSocket connection state
let ws: WebSocket | null = null;
let wsReconnectTimer: ReturnType<typeof setTimeout> | null = null;
let wsHeartbeatTimer: ReturnType<typeof setInterval> | null = null;

/**
 * Starts the WebSocket heartbeat timer.
 */
function startHeartbeat(): void {
    if (wsHeartbeatTimer) {
        clearInterval(wsHeartbeatTimer);
    }
    wsHeartbeatTimer = setInterval(() => {
        if (ws?.readyState === WebSocket.OPEN) {
            ws.send(JSON.stringify({ type: "ping" }));
        }
    }, 30000);
}

// Polled by several callers, so only reachability changes are logged.
let statusFetchFailure: string | null | undefined;

/**
 * Fetches the server status from the HTTP API.
 */
async function fetchServerStatus(): Promise<ServerStatus | null> {
    try {
        const response = await fetch(`${API_URL}/status`, {
            headers: getApiHeaders(),
            signal: AbortSignal.timeout(5000),
        });

        if (!response.ok) {
            reportStatusFetch(`${response.status} ${response.statusText}`);
            return null;
        }

        const status = await response.json();
        reportStatusFetch(null);
        return status;
    } catch (error) {
        reportStatusFetch(error instanceof Error ? error.message : String(error));
        return null;
    }
}

function reportStatusFetch(failure: string | null): void {
    if (failure === statusFetchFailure) {
        return;
    }
    if (failure) {
        log.warn(
            `Cannot fetch ${API_URL}/status (${failure}); presence and dashboard will show the server as offline until it responds`,
        );
    } else if (statusFetchFailure) {
        log.info("Server API reachable again");
    }
    statusFetchFailure = failure;
}

let lastActivityName: string | null = null;

/**
 * Updates the bot's presence/status based on server state.
 */
async function updatePresence(): Promise<void> {
    const status = await fetchServerStatus();
    const state = resolveServerState(status);

    let activityName: string;

    if (state.kind === "online" && status) {
        const playerInfo = `${status.playerCount}/${status.maxPlayers} players`;
        const inviteCode = status.steamInviteCode || status.gogInviteCode || "No code";
        activityName = `${playerInfo} | ${inviteCode}`;
    } else {
        activityName = `${state.label} — ${state.detail}`;
    }

    client.user?.setPresence({
        activities: [
            {
                name: "Custom Status",
                type: ActivityType.Custom,
                state: activityName,
            },
        ],
        status: state.presence,
    });

    if (activityName !== lastActivityName) {
        log.info(`Status updated: ${activityName}`);
        lastActivityName = activityName;
    }
}

/**
 * Updates the bot's nickname in all guilds.
 * Uses DISCORD_BOT_NICKNAME env var if set, otherwise uses farm name from server.
 */
async function updateBotNickname(): Promise<void> {
    let nickname = DISCORD_BOT_NICKNAME;

    if (!nickname) {
        const status = await fetchServerStatus();
        if (status?.farmName) {
            nickname = status.farmName;
        }
    }

    if (!nickname) {
        return;
    }

    for (const guild of client.guilds.cache.values()) {
        try {
            const currentNickname = guild.members.me?.nickname;
            if (currentNickname !== nickname) {
                await guild.members.me?.setNickname(nickname);
                log.info(`Nickname set to "${nickname}" in ${guild.name}`);
            }
        } catch (error) {
            // May lack permissions in some guilds
            if (error instanceof Error) {
                log.error(`Failed to set nickname in ${guild.name}: ${error.message}`);
            }
        }
    }
}

// Track WebSocket authentication state
let wsAuthenticated = false;
let wsFailedAttempts = 0;
const WS_RECONNECT_DELAY_MS = 5000;
const WS_FAILURE_LOG_EVERY = 12; // one progress line per minute at the 5s retry cadence

/**
 * Connects to the game server's WebSocket for real-time chat relay.
 */
function connectWebSocket(): void {
    if (!DISCORD_CHAT_CHANNEL_ID) {
        log.info("DISCORD_CHAT_CHANNEL_ID not set - chat relay disabled");
        return;
    }

    if (ws) {
        try {
            ws.close();
        } catch {
            // Ignore close errors
        }
        ws = null;
    }

    wsAuthenticated = false;
    if (wsFailedAttempts === 0) {
        log.info(`Connecting to WebSocket: ${WS_URL}`);
    }

    try {
        ws = new WebSocket(WS_URL);
        let opened = false;

        ws.onopen = () => {
            opened = true;
            // Send auth message if API_KEY is set
            if (API_KEY) {
                if (wsFailedAttempts === 0) {
                    log.info("WebSocket connected, authenticating...");
                }
                ws?.send(JSON.stringify({ type: "auth", payload: { token: API_KEY } }));
            } else {
                // No auth required, start heartbeat immediately
                log.info("WebSocket connected");
                wsAuthenticated = true;
                wsFailedAttempts = 0;
                startHeartbeat();
            }
        };

        ws.onmessage = async (event) => {
            try {
                const msg: WebSocketMessage = JSON.parse(event.data.toString());

                // Handle auth response
                if (msg.type === "auth_success") {
                    log.info("WebSocket authenticated");
                    wsAuthenticated = true;
                    wsFailedAttempts = 0;
                    startHeartbeat();
                    return;
                }

                if (msg.type === "auth_failed") {
                    if (wsFailedAttempts === 0) {
                        log.error(`WebSocket authentication failed: ${(msg.payload as any)?.error || "unknown error"}`);
                    }
                    return;
                }

                // Ignore messages if not authenticated
                if (!wsAuthenticated) {
                    return;
                }

                if (msg.type === "chat" && msg.payload) {
                    // Game -> Discord
                    const channel = client.channels.cache.get(DISCORD_CHAT_CHANNEL_ID);
                    if (channel?.isTextBased()) {
                        const { playerName, message } = msg.payload;
                        if (playerName && message) {
                            await (channel as TextChannel).send(`**${playerName}**: ${message}`);
                        }
                    }
                }
            } catch (error) {
                if (error instanceof Error) {
                    log.error(`Failed to process WebSocket message: ${error.message}`);
                }
            }
        };

        ws.onclose = () => {
            if (wsAuthenticated) {
                log.info("WebSocket disconnected, reconnecting...");
            } else {
                wsFailedAttempts++;
                if (wsFailedAttempts === 1 && opened) {
                    log.warn(
                        `Server closed the WebSocket before authentication completed; check that API_KEY matches the server. Retrying every ${WS_RECONNECT_DELAY_MS / 1000}s`,
                    );
                } else if (wsFailedAttempts === 1) {
                    log.info(
                        `Cannot reach the server WebSocket at ${WS_URL} yet; retrying every ${WS_RECONNECT_DELAY_MS / 1000}s. ` +
                            "This is normal while the game server is still starting. If it persists after the server is up, " +
                            "check that API_ENABLED is true on the server and API_KEY matches.",
                    );
                } else if (wsFailedAttempts % WS_FAILURE_LOG_EVERY === 0) {
                    log.info(`WebSocket still not connected (${wsFailedAttempts} attempts)`);
                }
            }
            ws = null;
            if (wsHeartbeatTimer) {
                clearInterval(wsHeartbeatTimer);
                wsHeartbeatTimer = null;
            }
            wsReconnectTimer = setTimeout(connectWebSocket, WS_RECONNECT_DELAY_MS);
        };

        // Error events carry no details; the close event that always follows does the logging.
        ws.onerror = () => {};
    } catch (error) {
        if (error instanceof Error) {
            log.error(`Failed to create WebSocket: ${error.message}`);
        }
        wsReconnectTimer = setTimeout(connectWebSocket, WS_RECONNECT_DELAY_MS);
    }
}

/**
 * Sends a chat message from Discord to the game server via WebSocket.
 * Returns true if the message was sent, false otherwise.
 */
function sendChatToGame(author: string, message: string): boolean {
    if (!ws || ws.readyState !== WebSocket.OPEN || !wsAuthenticated) {
        log.info("WebSocket not ready, cannot send chat");
        return false;
    }

    try {
        ws.send(
            JSON.stringify({
                type: "chat_send",
                payload: { author, message },
            }),
        );
        return true;
    } catch (error) {
        if (error instanceof Error) {
            log.error(`Failed to send chat to game: ${error.message}`);
        }
        return false;
    }
}

// ============================================================================
// HELPER FUNCTIONS
// ============================================================================

/**
 * Checks if a user has exceeded their command rate limit.
 */
function isRateLimited(userId: string): boolean {
    const now = Date.now();

    const timestamps = commandHistory.get(userId) ?? [];
    const validTimestamps = timestamps.filter((time) => now - time < COOLDOWN_DURATION_MS);

    if (validTimestamps.length >= MAX_COMMANDS_PER_WINDOW) {
        commandHistory.set(userId, validTimestamps);
        return true;
    }

    validTimestamps.push(now);
    commandHistory.set(userId, validTimestamps);
    return false;
}

// ============================================================================
// STATUS DASHBOARD
// ============================================================================
// The dashboard message is owned by one deployment: a UUID generated on first
// boot, persisted to the bot's volume, and stamped into the embed footer. The
// stamp lets the bot re-adopt its own message across restarts and refuse to
// touch a dashboard owned by another deployment sharing the channel.

const dashboardLog = createLogger("Dashboard");
const DASHBOARD_STATE_FILE = "/data/dashboard-state.json";

interface DashboardState {
    // null only in degraded mode (persistence unavailable): no id is stamped
    // and adoption falls back to title-based matching.
    ownerId: string | null;
    messageId: string | null;
}

const dashboardState: DashboardState = { ownerId: null, messageId: null };
let dashboardStateWritable = false;
let dashboardUpdateInFlight = false;
const warnedForeignOwnerIds = new Set<string>();

/**
 * Loads (or initializes) the persisted dashboard state. A missing file is a
 * normal first boot — the id is never recovered from channel contents, since
 * that could steal a foreign deployment's dashboard.
 */
function loadDashboardState(): void {
    let raw: string | null = null;
    try {
        raw = readFileSync(DASHBOARD_STATE_FILE, "utf8");
    } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== "ENOENT") {
            dashboardLog.warn(`Could not read ${DASHBOARD_STATE_FILE} (${error}) - treating as first boot`);
        }
    }

    if (raw !== null) {
        try {
            const parsed = JSON.parse(raw);
            const ownerId = typeof parsed?.ownerId === "string" ? parsed.ownerId.trim() : "";
            if (ownerId.length > 0) {
                dashboardState.ownerId = ownerId;
                // A non-snowflake messageId would fail the tracked fetch with a 400,
                // which the transient-error branch retries forever - drop it instead.
                dashboardState.messageId =
                    typeof parsed.messageId === "string" && /^\d+$/.test(parsed.messageId) ? parsed.messageId : null;
                dashboardStateWritable = true;
                dashboardLog.info(`Ownership id: ${dashboardState.ownerId}`);
                return;
            }
            dashboardLog.warn(`${DASHBOARD_STATE_FILE} has no ownerId - treating as first boot`);
        } catch {
            dashboardLog.warn(`${DASHBOARD_STATE_FILE} is unparseable - treating as first boot`);
        }
    }

    dashboardState.ownerId = crypto.randomUUID();
    dashboardState.messageId = null;
    dashboardStateWritable = true;
    persistDashboardState();
    if (dashboardStateWritable) {
        dashboardLog.info(`Ownership id created: ${dashboardState.ownerId}`);
    }
}

/**
 * Atomically persists the dashboard state (tmp file + rename). A failed write
 * at any point drops the bot into degraded mode: the ownership guard is
 * disabled and the footer is written without an id, so a later healthy boot
 * can re-adopt the message via the legacy branch and re-stamp it.
 */
function persistDashboardState(): void {
    if (!dashboardStateWritable) {
        return;
    }
    try {
        mkdirSync(dirname(DASHBOARD_STATE_FILE), { recursive: true });
        const tmpFile = `${DASHBOARD_STATE_FILE}.tmp`;
        writeFileSync(tmpFile, JSON.stringify(dashboardState));
        renameSync(tmpFile, DASHBOARD_STATE_FILE);
    } catch (error) {
        dashboardStateWritable = false;
        dashboardState.ownerId = null;
        dashboardLog.warn(
            `Cannot persist ${DASHBOARD_STATE_FILE} (${error}) - ownership guard disabled. ` +
                "The dashboard still updates, but a second deployment sharing this channel is no longer detected. " +
                "Check that the bot's /data volume is mounted and writable.",
        );
    }
}

/** Forgets the tracked dashboard message so the next update rescans the channel. */
function clearTrackedMessage(): void {
    dashboardState.messageId = null;
    persistDashboardState();
}

/** Builds the dashboard embed from the current server status. */
async function buildDashboardEmbed(): Promise<EmbedBuilder> {
    const status: ServerStatus | null = await fetchServerStatus();
    const state = resolveServerState(status);

    const embed = new EmbedBuilder()
        .setTitle(DASHBOARD_TITLE)
        .setTimestamp()
        .setFooter({ text: formatFooter(STATUS_DASHBOARD_REFRESH_RATE_FORMATTED, dashboardState.ownerId) });

    if (!status?.isOnline) {
        const hint =
            state.kind === "offline"
                ? "No game data can be pulled right now. Check back later!"
                : "Game data appears once the save is loaded.";
        embed.setColor(state.color).setDescription(`${state.label} — **${state.detail}**\n\n_${hint}_`);
    } else {
        const seasonEmojis: Record<string, string> = {
            spring: "🌸 Spring",
            summer: "☀️ Summer",
            fall: "🍂 Fall",
            winter: "❄️ Winter",
        };
        const formattedSeason = seasonEmojis[status.season?.toLowerCase()] || status.season;

        embed.setColor(state.color).addFields(
            { name: "🏡 Farm Name", value: status.farmName || "Our Farm", inline: true },
            { name: "🗺️ Farm Layout", value: getFarmTypeName(status.farmTypeKey), inline: true },
            {
                name: "📶 Server Status",
                value: status.isReady ? "✅ Ready & Running" : `⏳ ${state.detail}`,
                inline: true,
            },
            {
                name: "👥 Active Players",
                value: `**${status.playerCount} / ${status.maxPlayers}** ${status.isPaused ? "_(Paused)_" : ""}`,
                inline: true,
            },
            {
                name: "📅 In-Game Date",
                value: `Year ${status.year}, ${formattedSeason}, Day ${status.day}`,
                inline: true,
            },
            { name: "⏰ Clock Time", value: formatStardewTime(status.timeOfDay), inline: true },
            {
                name: "🔑 Connection Code",
                value: `\`${status.steamInviteCode || status.gogInviteCode || "None Available"}\``,
                inline: false,
            },
        );
    }

    return embed;
}

/** Interval entry point: runs one dashboard update, skipping ticks that overlap. */
async function updateLiveDashboard(): Promise<void> {
    if (!STATUS_DASHBOARD_CHANNEL_ID) {
        return;
    }
    if (dashboardUpdateInFlight) {
        return;
    }
    dashboardUpdateInFlight = true;
    try {
        await runDashboardUpdate(STATUS_DASHBOARD_CHANNEL_ID);
    } catch (error) {
        dashboardLog.error(`Loop execution failed: ${error}`);
    } finally {
        dashboardUpdateInFlight = false;
    }
}

/**
 * Updates the dashboard: edits the tracked message when it is still ours,
 * otherwise scans the channel to adopt our dashboard or posts a fresh one.
 */
async function runDashboardUpdate(channelId: string): Promise<void> {
    const channel = (await client.channels.fetch(channelId)) as TextChannel;
    if (!channel?.isTextBased()) {
        dashboardLog.error("Target channel not found or is not text-based");
        return;
    }

    const embed = await buildDashboardEmbed();

    // Primary path: edit the tracked message.
    if (dashboardState.messageId) {
        let existing: Message | null = null;
        try {
            // force: bypass the message cache (populated by our own edits), or a
            // deletion / foreign takeover of the tracked message is never seen.
            existing = await channel.messages.fetch({ message: dashboardState.messageId, force: true });
        } catch (error) {
            if (error instanceof DiscordAPIError && error.code === RESTJSONErrorCodes.UnknownMessage) {
                dashboardLog.info("Tracked message was deleted - scanning for a replacement");
                clearTrackedMessage();
            } else {
                // Transient failure (network, 5xx, rate limit, permissions): keep the id
                // and retry next tick — falling through to the scan here is what would
                // duplicate the dashboard.
                dashboardLog.error(`Could not fetch tracked message (${error}) - retrying next tick`);
                return;
            }
        }

        if (existing) {
            const kind = classifyDashboardEmbed(existing.embeds[0], dashboardState.ownerId);
            if (kind === "mine" || kind === "legacy") {
                await existing.edit({ content: "", embeds: [embed] });
                return;
            }
            dashboardLog.warn("Tracked message is not our dashboard anymore - scanning for a replacement");
            clearTrackedMessage();
        }
    }

    // Scan path: classify recent bot-authored messages and adopt our dashboard.
    let recentMessages: Awaited<ReturnType<typeof channel.messages.fetch>>;
    try {
        recentMessages = await channel.messages.fetch({ limit: 50 });
    } catch (error) {
        dashboardLog.error(`Message scan failed (${error}) - retrying next tick`);
        return;
    }

    const ownDashboards: { message: Message; kind: DashboardMessageKind }[] = [];
    for (const message of recentMessages.values()) {
        if (message.author.id !== client.user?.id) {
            continue;
        }
        const kind = classifyDashboardEmbed(message.embeds[0], dashboardState.ownerId);
        if (kind === "mine" || kind === "legacy") {
            ownDashboards.push({ message, kind });
        } else if (kind === "foreign") {
            const foreignId = parseOwnerId(message.embeds[0]?.footer?.text) ?? "unknown";
            if (!warnedForeignOwnerIds.has(foreignId)) {
                warnedForeignOwnerIds.add(foreignId);
                dashboardLog.warn(
                    `This channel has a dashboard owned by another deployment (id:${foreignId}) - ` +
                        "leaving it untouched. Each server needs its own bot application and channel setup.",
                );
            }
        }
    }

    // fetch() returns newest-first; prefer a stamped dashboard over a legacy one.
    const adopted = ownDashboards.find((d) => d.kind === "mine") ?? ownDashboards.find((d) => d.kind === "legacy");

    if (adopted) {
        dashboardState.messageId = adopted.message.id;
        persistDashboardState();
        // The edit re-stamps: legacy dashboards get the id on adoption.
        await adopted.message.edit({ content: "", embeds: [embed] });
        dashboardLog.info(`Adopted existing ${adopted.kind} dashboard message`);
    } else {
        const newMsg = await channel.send({ embeds: [embed] });
        dashboardState.messageId = newMsg.id;
        persistDashboardState();
        dashboardLog.info("Fresh status message initialized");
    }

    // Best-effort cleanup of our own surplus dashboards. Skipped in degraded mode,
    // where "mine" is title-based and could match a foreign deployment's dashboard.
    if (dashboardState.ownerId !== null) {
        for (const { message } of ownDashboards) {
            if (message.id === dashboardState.messageId) {
                continue;
            }
            try {
                await message.delete();
                dashboardLog.info("Deleted a surplus dashboard message of ours");
            } catch (error) {
                dashboardLog.warn(`Could not delete a surplus dashboard message: ${error}`);
            }
        }
    }
}

// Helper to transform Stardew's 24h int format (e.g., 600, 1620) into standard time
function formatStardewTime(timeInt: number): string {
    if (timeInt === undefined || timeInt === null) {
        return "??:??";
    }
    const hours24 = Math.floor(timeInt / 100);
    const minutes = timeInt % 100;
    const ampm = hours24 >= 12 ? "PM" : "AM";
    let hours12 = hours24 % 12;
    if (hours12 === 0) {
        hours12 = 12;
    }
    const minutesStr = minutes < 10 ? `0${minutes}` : minutes;
    return `${hours12}:${minutesStr} ${ampm}`;
}

// Helper to match Stardew Farm types cleanly
function getFarmTypeName(key: string): string {
    const types: Record<string, string> = {
        Standard: "Standard 🌾",
        Riverland: "Riverland 🐟",
        Forest: "Forest 🌲",
        Hilltop: "Hilltop ⛏️",
        Wilderness: "Wilderness 🦁",
        FourCorners: "Four Corners 🗺️",
        Beach: "Beach 🏖️",
        MeadowlandsFarm: "Meadowlands 🐓",
    };
    return types[key] || key || "Unknown Type";
}

// ============================================================================
// MAIN MESSAGE EVENT ROUTER (Commands + Chat Relay)
// ============================================================================
client.on(Events.MessageCreate, async (message: Message) => {
    // Ignore bot messages
    if (message.author.bot) {
        return;
    }

    const input = message.content.trim().toLowerCase();

    // --------------------------------------------------------------------------
    // BOT COMMAND HANDLING
    // --------------------------------------------------------------------------

    const validCommands = ["!status", "!players", "!server", "!help"];
    const isCommand = validCommands.includes(input);
    if (isCommand) {
        if (isRateLimited(message.author.id)) {
            try {
                const reply = await message.reply(
                    "⏳ **Whoa, slow down!** You're sending commands too quickly. Please wait a bit before trying again.",
                );
                setTimeout(() => reply.delete().catch(() => {}), 5000);
            } catch (err) {
                log.error(`Failed to send cooldown warning: ${err}`);
            }
            return;
        }

        // COMMAND: !status
        if (input === "!status") {
            try {
                const status: ServerStatus | null = await fetchServerStatus();
                const state = resolveServerState(status);

                if (!status?.isOnline) {
                    await message.reply(`**Server Status:** ${state.label} — ${state.detail}`);
                    return;
                }

                const seasonEmojis: Record<string, string> = {
                    spring: "🌸 Spring",
                    summer: "☀️ Summer",
                    fall: "🍂 Fall",
                    winter: "❄️ Winter",
                };
                const formattedSeason = seasonEmojis[status.season?.toLowerCase()] || status.season;

                const lines = [
                    `🏡 **Farm Name:** ${status.farmName}`,
                    `🗺️ **Farm Type:** ${getFarmTypeName(status.farmTypeKey)}`,
                    `👥 **Players:** ${status.playerCount}/${status.maxPlayers} ${status.isPaused ? "(⏸️ Paused)" : "(▶️ Live)"}`,
                    `📅 **Date:** Day ${status.day} of ${formattedSeason}, Year ${status.year}`,
                    `⏰ **Time:** ${formatStardewTime(status.timeOfDay)}`,
                    `📡 **Server State:** ${status.isReady ? "Ready ✓" : `Busy (${state.detail}) ⏳`}`,
                    `🔑 **Invite Code:** \`${status.steamInviteCode || status.gogInviteCode || "None"}\``,
                ];

                await message.reply(lines.join("\n"));
            } catch (_e) {
                await message.reply("⚠️ Failed to load server status.");
            }
            return;
        }

        // COMMAND: !players
        if (input === "!players") {
            try {
                const sharedSignal = AbortSignal.timeout(5000);
                const [playersRes, cabinsRes] = await Promise.all([
                    fetch(`${API_URL}/players`, { headers: getApiHeaders(), signal: sharedSignal }).then(
                        (r) => r.json() as Promise<PlayersResponse>,
                    ),
                    fetch(`${API_URL}/cabins`, { headers: getApiHeaders(), signal: sharedSignal }).then(
                        (r) => r.json() as Promise<CabinsResponse>,
                    ),
                ]);

                const onlineList = playersRes.players.filter((p) => p.isOnline).map((p) => `• 🟢 **${p.name}**`);

                const lines = [
                    `📊 **Roster Information**`,
                    `━━━━━━━━━━━━━━━━━━━━━━━━`,
                    `🟢 **Online Now (${onlineList.length}):**`,
                    onlineList.length ? onlineList.join("\n") : "• Nobody online",
                    `\n🛖 **Cabin Strategy & Real Estate:**`,
                    `• Total Cabins Built: **${cabinsRes.totalCount}**`,
                    `• Assigned to Players: **${cabinsRes.assignedCount}**`,
                    `• Available Vacancies: **${cabinsRes.availableCount}**`,
                ];

                await message.reply(lines.join("\n"));
            } catch (_e) {
                await message.reply("⚠️ Failed to parse player lists and cabin layouts.");
            }
            return;
        }

        // COMMAND: !server
        if (input === "!server") {
            try {
                const sharedSignal = AbortSignal.timeout(5000);
                const [stats, settings]: [StatsResponse, SettingsResponse] = await Promise.all([
                    fetch(`${API_URL}/stats`, { headers: getApiHeaders(), signal: sharedSignal }).then((r) => r.json()),
                    fetch(`${API_URL}/settings`, { headers: getApiHeaders(), signal: sharedSignal }).then((r) =>
                        r.json(),
                    ),
                ]);

                const lines = [
                    `⚙️ **Server Configuration & Telemetry**`,
                    `━━━━━━━━━━━━━━━━━━━━━━━━`,
                    `🖥️ **Performance Metrics:**`,
                    `• Tick Speed: **${stats.tps.toFixed(1)} / ${stats.targetTps} TPS** (Ticks Per Sec)`,
                    `• (Web)VNC Frame Rate: **${stats.fps.toFixed(1)} FPS**`,
                    `• Average Tick Time: **${stats.avgTickMs.toFixed(2)} ms**`,
                    `• Ram Overhead: **${stats.memoryMb.toFixed(1)} MB**`,
                    `\n🛠️ **Gameplay Rules:**`,
                    `• Wallet-Type: **${settings.server.separateWallets ? "💰 Separate Wallets" : "🤝 Shared Wallet"}**`,
                    `• Profit Margin Multiplier: **${settings.game.profitMargin}x**`,
                    `• Night Monsters Spawn: **${settings.game.spawnMonstersAtNight}**`,
                    `• Cabin Strategy: \`${settings.server.cabinStrategy}\` ([Learn More](https://stardew-valley-dedicated-server.github.io/server/features/cabin-strategies.html#cabinstack-default))`,
                ];

                await message.reply(lines.join("\n"));
            } catch (_e) {
                await message.reply("⚠️ Failed to retrieve system performance diagnostics.");
            }
            return;
        }

        // COMMAND: !help
        if (input === "!help") {
            const helpLines = [
                `🤖 **Stardew Server Bot Commands:**`,
                `• \`!status\` - View current farm date, time, player counts, and invite codes.`,
                `• \`!players\` - List who is online, and cabin availability.`,
                `• \`!server\` - Check hardware telemetry (TPS/FPS/RAM) and farm settings.`,
                `• \`!help\` - Display this command map.`,
            ];
            await message.reply(helpLines.join("\n"));
            return;
        }
    }

    // --------------------------------------------------------------------------
    // PART 2: PASSIVE CHAT RELAY
    // --------------------------------------------------------------------------
    // If the message made it past the commands above, check if it's meant for the game chat
    if (message.channel.id !== DISCORD_CHAT_CHANNEL_ID) {
        return;
    }

    // Get display name (server nickname if set, otherwise global display name)
    const displayName = message.member?.displayName || message.author.displayName;

    const success = sendChatToGame(displayName, message.content);

    // Add reaction to indicate failure
    if (!success) {
        try {
            await message.react("❌");
        } catch (error) {
            if (error instanceof Error) {
                log.error(`Failed to add reaction: ${error.message}`);
            }
        }
    }
});

/**
 * Performs extensive permission and sanity checks on startup.
 * Logs warnings for any missing permissions or configuration issues.
 */
async function performStartupChecks(): Promise<void> {
    log.info("Performing startup checks...");

    const warnings: string[] = [];
    const errors: string[] = [];

    // Check guilds
    const guildCount = client.guilds.cache.size;
    log.info(`Connected to ${guildCount} guild(s)`);

    if (guildCount === 0) {
        warnings.push("Bot is not in any guilds - invite it to a server first");
    }

    // Check chat channel if configured
    if (DISCORD_CHAT_CHANNEL_ID) {
        const chatChannel = client.channels.cache.get(DISCORD_CHAT_CHANNEL_ID);

        if (!chatChannel) {
            errors.push(`Chat channel ${DISCORD_CHAT_CHANNEL_ID} not found - check DISCORD_CHAT_CHANNEL_ID`);
        } else if (!chatChannel.isTextBased()) {
            errors.push(`Channel ${DISCORD_CHAT_CHANNEL_ID} is not a text channel`);
        } else {
            // Check permissions in the chat channel
            const channel = chatChannel as TextChannel;
            const botMember = channel.guild.members.me;

            if (botMember) {
                const permissions = channel.permissionsFor(botMember);

                if (!permissions?.has(PermissionFlagsBits.ViewChannel)) {
                    errors.push(`Missing VIEW_CHANNEL permission in chat channel "${channel.name}"`);
                }
                if (!permissions?.has(PermissionFlagsBits.SendMessages)) {
                    errors.push(`Missing SEND_MESSAGES permission in chat channel "${channel.name}"`);
                }
                if (!permissions?.has(PermissionFlagsBits.ReadMessageHistory)) {
                    warnings.push(
                        `Missing READ_MESSAGE_HISTORY permission in chat channel "${channel.name}" - may miss some messages`,
                    );
                }
                if (!permissions?.has(PermissionFlagsBits.AddReactions)) {
                    warnings.push(
                        `Missing ADD_REACTIONS permission in chat channel "${channel.name}" - cannot show failure indicators`,
                    );
                }

                log.info(`Chat channel: #${channel.name} in ${channel.guild.name}`);
            }
        }
    }

    // Check permissions in each guild
    for (const guild of client.guilds.cache.values()) {
        const botMember = guild.members.me;
        if (!botMember) {
            continue;
        }

        // Check nickname permission
        if (!botMember.permissions.has(PermissionFlagsBits.ChangeNickname)) {
            warnings.push(`Missing CHANGE_NICKNAME permission in "${guild.name}" - cannot update bot nickname`);
        }
    }

    // fetchServerStatus logs its own failure, so only success is reported here
    const status = await fetchServerStatus();
    if (status) {
        log.info(`API connectivity: OK (server ${resolveServerState(status).kind})`);
    }

    for (const warning of warnings) {
        log.warn(`Startup check: ${warning}`);
    }
    for (const error of errors) {
        log.error(`Startup check: ${error}`);
    }
    if (warnings.length === 0 && errors.length === 0) {
        log.info("All startup checks passed");
    }
}

client.once(Events.ClientReady, async () => {
    log.info(`Logged in as ${client.user?.tag}`);
    log.info(`API URL: ${API_URL}`);
    log.info(`API authentication: ${API_KEY ? "enabled" : "disabled"}`);
    log.info(`Update interval: ${UPDATE_INTERVAL_MS}ms`);

    if (DISCORD_CHAT_CHANNEL_ID) {
        log.info(`Chat relay channel: ${DISCORD_CHAT_CHANNEL_ID}`);
    }

    // Perform startup checks
    await performStartupChecks();

    // Initial updates
    updatePresence();
    updateBotNickname();

    // Connect WebSocket for chat relay
    connectWebSocket();

    if (STATUS_DASHBOARD_CHANNEL_ID) {
        loadDashboardState();
        await updateLiveDashboard();
        setInterval(updateLiveDashboard, STATUS_DASHBOARD_REFRESH_RATE * 1000);
    }

    // Periodic updates with error handling
    setInterval(() => {
        updatePresence().catch((error) => {
            log.error(`Presence update failed: ${error instanceof Error ? error.message : error}`);
        });
    }, UPDATE_INTERVAL_MS);

    setInterval(() => {
        updateBotNickname().catch((error) => {
            log.error(`Nickname update failed: ${error instanceof Error ? error.message : error}`);
        });
    }, UPDATE_INTERVAL_MS);
});

client.on(Events.Error, (error) => {
    log.error(`Client error: ${error.message}`);
});

client.on(Events.ShardError, (error) => {
    log.error(`Gateway error: ${error.message}`);
});

const TOKEN_REJECTED_HINT =
    "Discord rejected DISCORD_BOT_TOKEN. Copy a fresh token from the Developer Portal (Bot -> Reset Token).";
const RESTART_HINT = "Bot stopped. After fixing the configuration, run: docker compose up -d discord-bot";

/**
 * Explains a configuration error and exits 0, so `restart: on-failure` does not
 * loop against Discord's daily identify limit.
 */
function stopForConfigError(lines: string[]): never {
    for (const line of lines) {
        log.error(line);
    }
    log.error(RESTART_HINT);
    process.exit(0);
}

// discord.js gives up on these close codes and then rejects an internal promise,
// which Bun reports as a crash with a library stack trace. Exit here first.
client.on(Events.ShardDisconnect, ({ code }) => {
    switch (code) {
        case GatewayCloseCodes.AuthenticationFailed:
            stopForConfigError([TOKEN_REJECTED_HINT]);
            break;
        case GatewayCloseCodes.DisallowedIntents:
            stopForConfigError([
                "Discord refused the bot's gateway intents: DISCORD_CHAT_CHANNEL_ID is set, so the bot requests the",
                "Message Content intent, but it is not enabled for this application.",
                "Fix: Developer Portal -> your app -> Bot -> Privileged Gateway Intents -> enable Message Content Intent,",
                "or unset DISCORD_CHAT_CHANNEL_ID to run without chat relay.",
            ]);
            break;
        default:
            log.error(`Gateway closed with unrecoverable code ${code} (${GatewayCloseCodes[code] ?? "unknown"})`);
            process.exit(1);
    }
});

// Graceful shutdown
function shutdown() {
    log.info("Shutting down...");

    if (wsReconnectTimer) {
        clearTimeout(wsReconnectTimer);
    }
    if (wsHeartbeatTimer) {
        clearInterval(wsHeartbeatTimer);
    }
    if (ws) {
        try {
            ws.close();
        } catch {
            // Ignore close errors
        }
    }

    client.destroy();
    process.exit(0);
}

process.on("SIGINT", shutdown);
process.on("SIGTERM", shutdown);

// Start the bot
log.info("Starting...");
client.login(DISCORD_BOT_TOKEN).catch((error: unknown) => {
    if (error instanceof DiscordjsError && error.code === DiscordjsErrorCodes.TokenInvalid) {
        stopForConfigError([TOKEN_REJECTED_HINT]);
    }
    log.error(`Login failed: ${error instanceof Error ? error.message : error}`);
    process.exit(1);
});
