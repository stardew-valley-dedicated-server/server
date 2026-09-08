import { mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import {
    ActivityType,
    type Channel,
    Client,
    DiscordAPIError,
    DiscordjsError,
    DiscordjsErrorCodes,
    type EmbedBuilder,
    Events,
    GatewayCloseCodes,
    GatewayIntentBits,
    type Guild,
    type Message,
    type NewsChannel,
    PermissionFlagsBits,
    type PermissionResolvable,
    RESTJSONErrorCodes,
    type TextChannel,
} from "discord.js";
import { type ChannelRef, describeChannelRef, matchesChannelRef, parseChannelRef } from "./channels";
import {
    buildDashboardEmbed,
    buildStatusFields,
    classifyDashboardEmbed,
    type DashboardMessageKind,
    formatFooter,
    formatStateLine,
    parseDashboardState,
    parseOwnerId,
} from "./dashboard";
import { resolveServerState, type ServerStatus } from "./discordState";
import { createLogger, log } from "./log";
import { joinableInviteCode } from "./serverState";

// Configuration from environment
const DISCORD_BOT_TOKEN = process.env.DISCORD_BOT_TOKEN;
const API_URL = process.env.API_URL || "http://server:8080";
const API_KEY = process.env.API_KEY || "";
const WS_URL = process.env.WS_URL || `${API_URL.replace("http://", "ws://").replace("https://", "wss://")}/ws`;
const CHAT_CHANNEL = parseChannelRef(process.env.DISCORD_CHAT_CHANNEL);
const DASHBOARD_CHANNEL = parseChannelRef(process.env.STATUS_DASHBOARD_CHANNEL);
// Every update edits one message per channel; the same floor as the presence keeps Discord's rate limits at bay.
const MIN_STATUS_DASHBOARD_REFRESH_RATE = 20;
const STATUS_DASHBOARD_REFRESH_RATE = Math.max(
    Number(process.env.STATUS_DASHBOARD_REFRESH_RATE) || 30,
    MIN_STATUS_DASHBOARD_REFRESH_RATE,
);
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

// Commands are addressed by mentioning the bot, and Discord delivers the content of such
// messages without the privileged Message Content intent. That intent is needed only to
// read every message in the chat relay channel.
const intents = [GatewayIntentBits.Guilds, GatewayIntentBits.GuildMessages];
if (CHAT_CHANNEL) {
    intents.push(GatewayIntentBits.MessageContent);
}

// Relayed chat, player names and the farm name come from the game unchanged, so nothing the
// bot posts may ping users, roles or everyone; replies still notify the person replied to.
const client = new Client({ intents, allowedMentions: { parse: [], repliedUser: true } });

/** A guild channel the bot posts to; the two types matched by `matchesChannelRef`. */
type PostableChannel = TextChannel | NewsChannel;

/** Channels in one guild matching a configured reference. */
function resolveChannelsIn(guild: Guild, ref: ChannelRef): PostableChannel[] {
    const matches: PostableChannel[] = [];
    for (const channel of guild.channels.cache.values()) {
        if (matchesChannelRef(channel, ref)) {
            matches.push(channel as PostableChannel);
        }
    }
    return matches;
}

/**
 * Channels matching a configured reference across every guild: one for an id, one per
 * guild for a name. Resolved from the gateway cache on each use, so channel renames and
 * newly joined guilds take effect without a restart.
 */
function resolveChannels(ref: ChannelRef): PostableChannel[] {
    return [...client.guilds.cache.values()].flatMap((guild) => resolveChannelsIn(guild, ref));
}

/** True when an incoming message's channel is the configured one. */
function isConfiguredChannel(channel: Channel, ref: ChannelRef): boolean {
    return !channel.isDMBased() && matchesChannelRef(channel, ref);
}

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

let lastPresenceSummary: string | null = null;
let presenceShowsVersion = false;

/**
 * Updates the bot's presence/status based on server state. The line has no room for both the
 * player count and the server version, so it alternates between them each refresh.
 */
async function updatePresence(): Promise<void> {
    const status = await fetchServerStatus();
    const state = resolveServerState(status);

    let activityName: string;
    // Logged instead of activityName, so the alternation itself is not logged as a change.
    let presenceSummary: string;

    if (state.kind === "online" && status) {
        const playerInfo = `${status.playerCount}/${status.maxPlayers} players`;
        const version = `v${status.serverVersion}`;
        const inviteCode = joinableInviteCode(status) ?? "No code";
        presenceShowsVersion = !presenceShowsVersion;
        activityName = `${presenceShowsVersion ? version : playerInfo} | ${inviteCode}`;
        presenceSummary = `${playerInfo} | ${version} | ${inviteCode}`;
    } else {
        activityName = `${state.emoji} ${state.label} — ${state.detail}`;
        presenceSummary = activityName;
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

    if (presenceSummary !== lastPresenceSummary) {
        log.info(`Status updated: ${presenceSummary}`);
        lastPresenceSummary = presenceSummary;
    }
}

/**
 * Updates the bot's nickname in all guilds: the server's display name (SERVER_NAME), else the farm name.
 */
async function updateBotNickname(): Promise<void> {
    const status = await fetchServerStatus();
    // Discord rejects nicknames over 32 characters; the full name stays on /status.
    const nickname = (status?.serverName || status?.farmName)?.slice(0, 32);

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
    if (!CHAT_CHANNEL) {
        log.info("DISCORD_CHAT_CHANNEL not set - chat relay disabled");
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

                if (msg.type === "chat" && msg.payload && CHAT_CHANNEL) {
                    // Game -> Discord, to the chat channel of every guild that has one
                    const { playerName, message } = msg.payload;
                    if (playerName && message) {
                        for (const channel of resolveChannels(CHAT_CHANNEL)) {
                            try {
                                await channel.send(`**${playerName}**: ${message}`);
                            } catch (error) {
                                log.error(
                                    `Failed to relay chat to #${channel.name} in ${channel.guild.name}: ${error}`,
                                );
                            }
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
    // Tracked dashboard message per channel id.
    messageIds: Record<string, string>;
}

const dashboardState: DashboardState = { ownerId: null, messageIds: {} };
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
        const persisted = parseDashboardState(raw);
        if (persisted) {
            dashboardState.ownerId = persisted.ownerId;
            dashboardState.messageIds = persisted.messageIds;
            dashboardStateWritable = true;
            dashboardLog.info(`Ownership id: ${dashboardState.ownerId}`);
            return;
        }
        dashboardLog.warn(`${DASHBOARD_STATE_FILE} has no usable ownerId - treating as first boot`);
    }

    dashboardState.ownerId = crypto.randomUUID();
    dashboardState.messageIds = {};
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

/** Remembers the dashboard message of a channel. */
function trackMessage(channelId: string, messageId: string): void {
    dashboardState.messageIds[channelId] = messageId;
    persistDashboardState();
}

/** Forgets a channel's tracked dashboard message so the next update rescans that channel. */
function clearTrackedMessage(channelId: string): void {
    delete dashboardState.messageIds[channelId];
    persistDashboardState();
}

/** Builds the dashboard embed from the current server status. */
async function fetchDashboardEmbed(): Promise<EmbedBuilder> {
    const status = await fetchServerStatus();
    const footer = formatFooter(STATUS_DASHBOARD_REFRESH_RATE, dashboardState.ownerId);
    return buildDashboardEmbed(status, resolveServerState(status), footer);
}

/** Interval entry point: updates the dashboard in every matching channel, skipping ticks that overlap. */
async function updateLiveDashboard(): Promise<void> {
    if (!DASHBOARD_CHANNEL || dashboardUpdateInFlight) {
        return;
    }
    dashboardUpdateInFlight = true;
    try {
        const embed = await fetchDashboardEmbed();
        for (const channel of resolveChannels(DASHBOARD_CHANNEL)) {
            try {
                await runDashboardUpdate(channel, embed);
            } catch (error) {
                dashboardLog.error(`Update failed in #${channel.name} (${channel.guild.name}): ${error}`);
            }
        }
    } catch (error) {
        dashboardLog.error(`Loop execution failed: ${error}`);
    } finally {
        dashboardUpdateInFlight = false;
    }
}

/**
 * Updates one channel's dashboard: edits the tracked message when it is still ours,
 * otherwise scans the channel to adopt our dashboard or posts a fresh one.
 */
async function runDashboardUpdate(channel: PostableChannel, embed: EmbedBuilder): Promise<void> {
    const where = `#${channel.name} (${channel.guild.name})`;

    // Primary path: edit the tracked message.
    const trackedId = dashboardState.messageIds[channel.id];
    if (trackedId) {
        let existing: Message | null = null;
        try {
            // force: bypass the message cache (populated by our own edits), or a
            // deletion / foreign takeover of the tracked message is never seen.
            existing = await channel.messages.fetch({ message: trackedId, force: true });
        } catch (error) {
            if (error instanceof DiscordAPIError && error.code === RESTJSONErrorCodes.UnknownMessage) {
                dashboardLog.info(`Tracked message in ${where} was deleted - scanning for a replacement`);
                clearTrackedMessage(channel.id);
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
            dashboardLog.warn(`Tracked message in ${where} is not our dashboard anymore - scanning for a replacement`);
            clearTrackedMessage(channel.id);
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
            const warningKey = `${channel.id}:${foreignId}`;
            if (!warnedForeignOwnerIds.has(warningKey)) {
                warnedForeignOwnerIds.add(warningKey);
                dashboardLog.warn(
                    `${where} has a dashboard owned by another deployment (id:${foreignId}) - ` +
                        "leaving it untouched. Each server needs its own bot application and channel setup.",
                );
            }
        }
    }

    // fetch() returns newest-first; prefer a stamped dashboard over a legacy one.
    const adopted = ownDashboards.find((d) => d.kind === "mine") ?? ownDashboards.find((d) => d.kind === "legacy");

    if (adopted) {
        trackMessage(channel.id, adopted.message.id);
        // The edit re-stamps: legacy dashboards get the id on adoption.
        await adopted.message.edit({ content: "", embeds: [embed] });
        dashboardLog.info(`Adopted existing ${adopted.kind} dashboard message in ${where}`);
    } else {
        const newMsg = await channel.send({ embeds: [embed] });
        trackMessage(channel.id, newMsg.id);
        dashboardLog.info(`Fresh status message posted in ${where}`);
    }

    // Best-effort cleanup of our own surplus dashboards. Skipped in degraded mode,
    // where "mine" is title-based and could match a foreign deployment's dashboard.
    if (dashboardState.ownerId !== null) {
        for (const { message } of ownDashboards) {
            if (message.id === dashboardState.messageIds[channel.id]) {
                continue;
            }
            try {
                await message.delete();
                dashboardLog.info(`Deleted a surplus dashboard message of ours in ${where}`);
            } catch (error) {
                dashboardLog.warn(`Could not delete a surplus dashboard message in ${where}: ${error}`);
            }
        }
    }
}

// ============================================================================
// MAIN MESSAGE EVENT ROUTER (Commands + Chat Relay)
// ============================================================================

const COMMANDS = ["!status", "!players", "!server", "!help"];

/**
 * The command in a message addressed to this bot (`@Bot !status`), or null. The mention
 * is what tells several bots in one Discord server apart and keeps these commands from
 * colliding with the game's own `!` commands typed into the chat relay.
 */
function parseCommand(message: Message): string | null {
    const botId = client.user?.id;
    if (!botId || !message.mentions.has(botId)) {
        return null;
    }
    const input = message.content
        .replace(new RegExp(`<@!?${botId}>`, "g"), "")
        .trim()
        .toLowerCase();
    return COMMANDS.includes(input) ? input : null;
}

/** Replies to a message, logging instead of throwing when the bot may not post in that channel. */
async function reply(message: Message, content: string): Promise<Message | null> {
    try {
        return await message.reply(content);
    } catch (error) {
        const channelName = message.channel.isDMBased() ? "a DM" : `#${message.channel.name}`;
        log.error(`Could not reply in ${channelName} (${message.guild?.name ?? "no guild"}): ${error}`);
        return null;
    }
}

client.on(Events.MessageCreate, async (message: Message) => {
    // Ignore bot messages
    if (message.author.bot) {
        return;
    }

    // --------------------------------------------------------------------------
    // BOT COMMAND HANDLING
    // --------------------------------------------------------------------------

    // The relay and dashboard channels keep their own purpose; commands there are
    // treated like any other message (relayed into the game, or ignored).
    const inOwnChannel =
        (CHAT_CHANNEL !== null && isConfiguredChannel(message.channel, CHAT_CHANNEL)) ||
        (DASHBOARD_CHANNEL !== null && isConfiguredChannel(message.channel, DASHBOARD_CHANNEL));
    const input = inOwnChannel ? null : parseCommand(message);
    if (input) {
        if (isRateLimited(message.author.id)) {
            const warning = await reply(message, "Too many commands; please wait a moment before trying again.");
            if (warning) {
                setTimeout(() => warning.delete().catch(() => {}), 5000);
            }
            return;
        }

        // COMMAND: !status
        if (input === "!status") {
            try {
                const status: ServerStatus | null = await fetchServerStatus();
                const state = resolveServerState(status);

                if (!status?.isOnline) {
                    await reply(message, `**Status:** ${formatStateLine(state)}`);
                    return;
                }

                const lines = buildStatusFields(status, state).map((field) => `**${field.name}:** ${field.value}`);
                await reply(message, lines.join("\n"));
            } catch (_e) {
                await reply(message, "Could not load the server status.");
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

                const online = playersRes.players.filter((p) => p.isOnline).map((p) => p.name);

                const lines = [
                    `**Online (${online.length}):** ${online.length ? online.join(", ") : "nobody"}`,
                    `**Cabins:** ${cabinsRes.totalCount} built, ${cabinsRes.assignedCount} assigned, ${cabinsRes.availableCount} available`,
                ];

                await reply(message, lines.join("\n"));
            } catch (_e) {
                await reply(message, "Could not load the player list.");
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
                    `**Tick rate:** ${stats.tps.toFixed(1)} / ${stats.targetTps} TPS, ${stats.avgTickMs.toFixed(2)} ms per tick`,
                    `**Frame rate:** ${stats.fps.toFixed(1)} FPS`,
                    `**Memory:** ${stats.memoryMb.toFixed(1)} MB`,
                    `**Wallets:** ${settings.server.separateWallets ? "separate" : "shared"}`,
                    `**Profit margin:** ${settings.game.profitMargin}x`,
                    `**Monsters at night:** ${settings.game.spawnMonstersAtNight}`,
                    `**Cabin strategy:** \`${settings.server.cabinStrategy}\` (<https://docs.junimoserver.com/features/cabin-strategies>)`,
                ];

                await reply(message, lines.join("\n"));
            } catch (_e) {
                await reply(message, "Could not load the server settings.");
            }
            return;
        }

        // COMMAND: !help
        if (input === "!help") {
            const helpLines = [
                `**Commands** (mention me, e.g. <@${client.user?.id}> !status):`,
                `\`!status\` - server state, players, in-game date, versions, tick rate, and the invite code`,
                `\`!players\` - who is online, and cabin availability`,
                `\`!server\` - tick rate, memory, and gameplay settings`,
                `\`!help\` - this list`,
            ];
            await reply(message, helpLines.join("\n"));
            return;
        }
    }

    // --------------------------------------------------------------------------
    // PART 2: PASSIVE CHAT RELAY
    // --------------------------------------------------------------------------
    // If the message made it past the commands above, check if it's meant for the game chat
    if (!CHAT_CHANNEL || !isConfiguredChannel(message.channel, CHAT_CHANNEL)) {
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

// ============================================================================
// GUILD CHECKS
// ============================================================================

interface ChannelRequirements {
    setting: string;
    required: [PermissionResolvable, string][];
    optional: [PermissionResolvable, string, string][];
}

const CHAT_CHANNEL_REQUIREMENTS: ChannelRequirements = {
    setting: "DISCORD_CHAT_CHANNEL",
    required: [
        [PermissionFlagsBits.ViewChannel, "VIEW_CHANNEL"],
        [PermissionFlagsBits.SendMessages, "SEND_MESSAGES"],
    ],
    optional: [
        [PermissionFlagsBits.ReadMessageHistory, "READ_MESSAGE_HISTORY", "may miss some messages"],
        [PermissionFlagsBits.AddReactions, "ADD_REACTIONS", "cannot show failure indicators"],
    ],
};

const DASHBOARD_CHANNEL_REQUIREMENTS: ChannelRequirements = {
    setting: "STATUS_DASHBOARD_CHANNEL",
    required: [
        [PermissionFlagsBits.ViewChannel, "VIEW_CHANNEL"],
        [PermissionFlagsBits.SendMessages, "SEND_MESSAGES"],
        [PermissionFlagsBits.EmbedLinks, "EMBED_LINKS"],
    ],
    optional: [
        [
            PermissionFlagsBits.ReadMessageHistory,
            "READ_MESSAGE_HISTORY",
            "cannot recover the dashboard message after a restart",
        ],
    ],
};

/** The configured channels with what the bot needs in them; empty when neither feature is set. */
const CONFIGURED_CHANNELS: [ChannelRef, string, ChannelRequirements][] = [];
if (CHAT_CHANNEL) {
    CONFIGURED_CHANNELS.push([CHAT_CHANNEL, CHAT_CHANNEL_REQUIREMENTS.setting, CHAT_CHANNEL_REQUIREMENTS]);
}
if (DASHBOARD_CHANNEL) {
    CONFIGURED_CHANNELS.push([
        DASHBOARD_CHANNEL,
        DASHBOARD_CHANNEL_REQUIREMENTS.setting,
        DASHBOARD_CHANNEL_REQUIREMENTS,
    ]);
}

interface GuildFindings {
    errors: string[];
    warnings: string[];
}

/**
 * Resolves each configured channel in one guild and checks the bot's permissions there.
 * A name is expected in every guild, so a guild without it gets a warning; a missing id
 * is reported once, across guilds, by the startup checks.
 */
function checkGuild(guild: Guild): GuildFindings {
    const findings: GuildFindings = { errors: [], warnings: [] };

    if (!guild.members.me?.permissions.has(PermissionFlagsBits.ChangeNickname)) {
        findings.warnings.push(`Missing CHANGE_NICKNAME permission in "${guild.name}" - cannot update bot nickname`);
    }

    for (const [ref, setting, requirements] of CONFIGURED_CHANNELS) {
        const channels = resolveChannelsIn(guild, ref);
        if (channels.length === 0) {
            if (ref.kind === "name") {
                findings.warnings.push(`${describeChannelRef(ref)} not found in "${guild.name}" - check ${setting}`);
            }
            continue;
        }
        if (channels.length > 1) {
            findings.warnings.push(
                `${describeChannelRef(ref)} matches ${channels.length} channels in "${guild.name}" - all of them are used`,
            );
        }
        for (const channel of channels) {
            const permissions = guild.members.me ? channel.permissionsFor(guild.members.me) : null;
            const at = `#${channel.name} in "${guild.name}"`;
            for (const [permission, name] of requirements.required) {
                if (!permissions?.has(permission)) {
                    findings.errors.push(`Missing ${name} permission in ${at}`);
                }
            }
            for (const [permission, name, consequence] of requirements.optional) {
                if (!permissions?.has(permission)) {
                    findings.warnings.push(`Missing ${name} permission in ${at} - ${consequence}`);
                }
            }
            log.info(`${setting}: ${at}`);
        }
    }
    return findings;
}

// A guild joined while running is served on the next tick; check it now so a missing
// channel or permission shows up in the log right away instead of as a failed update.
client.on(Events.GuildCreate, (guild) => {
    log.info(`Joined "${guild.name}"`);
    const findings = checkGuild(guild);
    for (const warning of findings.warnings) {
        log.warn(`Guild check: ${warning}`);
    }
    for (const error of findings.errors) {
        log.error(`Guild check: ${error}`);
    }
    updateBotNickname().catch((error) => {
        log.error(`Nickname update failed: ${error instanceof Error ? error.message : error}`);
    });
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

    // An id targets one channel, so it is an error only when no guild has it. A name is
    // checked per guild below.
    for (const [ref, setting] of CONFIGURED_CHANNELS) {
        if (ref.kind === "id" && resolveChannels(ref).length === 0) {
            errors.push(`${describeChannelRef(ref)} not found in any server the bot is in - check ${setting}`);
        }
    }

    for (const guild of client.guilds.cache.values()) {
        const findings = checkGuild(guild);
        errors.push(...findings.errors);
        warnings.push(...findings.warnings);
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

    if (CHAT_CHANNEL) {
        log.info(`Chat relay channel: ${describeChannelRef(CHAT_CHANNEL)}`);
    }
    if (DASHBOARD_CHANNEL) {
        log.info(
            `Status dashboard channel: ${describeChannelRef(DASHBOARD_CHANNEL)}, updating every ${STATUS_DASHBOARD_REFRESH_RATE}s`,
        );
    }

    // Perform startup checks
    await performStartupChecks();

    // Initial updates
    updatePresence();
    updateBotNickname();

    // Connect WebSocket for chat relay
    connectWebSocket();

    if (DASHBOARD_CHANNEL) {
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
                "Discord refused the bot's gateway intents: DISCORD_CHAT_CHANNEL is set, so the bot requests the",
                "Message Content intent, but it is not enabled for this application.",
                "Fix: Developer Portal -> your app -> Bot -> Privileged Gateway Intents -> enable Message Content Intent,",
                "or unset DISCORD_CHAT_CHANNEL to run without chat relay.",
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
