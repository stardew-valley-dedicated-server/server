import {
    ActivityType,
    Client,
    Colors,
    Events,
    GatewayIntentBits,
    type Message,
    PermissionFlagsBits,
    type TextChannel,
} from "discord.js";

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
let targetMessageId: string | null = null;
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
    console.log("[Discord Bot] DISCORD_BOT_TOKEN not set - bot disabled");
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

interface ServerStatus {
    playerCount: number;
    maxPlayers: number;
    steamInviteCode: string | null;
    gogInviteCode: string | null;
    serverVersion: string;
    isOnline: boolean;
    isReady: boolean;
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
            console.error(`[Discord Bot] API request failed: ${response.status} ${response.statusText}`);
            return null;
        }

        return await response.json();
    } catch (error) {
        if (error instanceof Error) {
            console.error(`[Discord Bot] Failed to fetch status: ${error.message}`);
        } else {
            console.error(`[Discord Bot] Failed to fetch status: ${error}`);
        }
        return null;
    }
}

/**
 * Updates the bot's presence/status based on server state.
 */
async function updatePresence(): Promise<void> {
    const status = await fetchServerStatus();

    let activityName: string;

    if (!status?.isOnline) {
        activityName = "Server Offline";
    } else {
        const playerInfo = `${status.playerCount}/${status.maxPlayers} players`;
        const inviteCode = status.steamInviteCode || status.gogInviteCode || "No code";
        activityName = `${playerInfo} | ${inviteCode}`;
    }

    client.user?.setPresence({
        activities: [
            {
                name: "Custom Status",
                type: ActivityType.Custom,
                state: activityName,
            },
        ],
        status: status?.isOnline ? "online" : "idle",
    });

    console.log(`[Discord Bot] Status updated: ${activityName}`);
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
                console.log(`[Discord Bot] Nickname set to "${nickname}" in ${guild.name}`);
            }
        } catch (error) {
            // May lack permissions in some guilds
            if (error instanceof Error) {
                console.error(`[Discord Bot] Failed to set nickname in ${guild.name}: ${error.message}`);
            }
        }
    }
}

// Track WebSocket authentication state
let wsAuthenticated = false;

/**
 * Connects to the game server's WebSocket for real-time chat relay.
 */
function connectWebSocket(): void {
    if (!DISCORD_CHAT_CHANNEL_ID) {
        console.log("[Discord Bot] DISCORD_CHAT_CHANNEL_ID not set - chat relay disabled");
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
    console.log(`[Discord Bot] Connecting to WebSocket: ${WS_URL}`);

    try {
        ws = new WebSocket(WS_URL);

        ws.onopen = () => {
            // Send auth message if API_KEY is set
            if (API_KEY) {
                console.log("[Discord Bot] WebSocket connected, authenticating...");
                ws?.send(JSON.stringify({ type: "auth", payload: { token: API_KEY } }));
            } else {
                // No auth required, start heartbeat immediately
                console.log("[Discord Bot] WebSocket connected");
                wsAuthenticated = true;
                startHeartbeat();
            }
        };

        ws.onmessage = async (event) => {
            try {
                const msg: WebSocketMessage = JSON.parse(event.data.toString());

                // Handle auth response
                if (msg.type === "auth_success") {
                    console.log("[Discord Bot] WebSocket authenticated");
                    wsAuthenticated = true;
                    startHeartbeat();
                    return;
                }

                if (msg.type === "auth_failed") {
                    console.error(
                        `[Discord Bot] WebSocket authentication failed: ${(msg.payload as any)?.error || "unknown error"}`,
                    );
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
                    console.error(`[Discord Bot] Failed to process WebSocket message: ${error.message}`);
                }
            }
        };

        ws.onclose = () => {
            console.log("[Discord Bot] WebSocket disconnected, reconnecting in 5s...");
            ws = null;
            if (wsHeartbeatTimer) {
                clearInterval(wsHeartbeatTimer);
                wsHeartbeatTimer = null;
            }
            wsReconnectTimer = setTimeout(connectWebSocket, 5000);
        };

        ws.onerror = (_event) => {
            // WebSocket error events don't contain useful error details in the browser API
            // The actual error will trigger onclose, so we just log that an error occurred
            console.error(`[Discord Bot] WebSocket connection error - will attempt reconnection`);
        };
    } catch (error) {
        if (error instanceof Error) {
            console.error(`[Discord Bot] Failed to create WebSocket: ${error.message}`);
        }
        wsReconnectTimer = setTimeout(connectWebSocket, 5000);
    }
}

/**
 * Sends a chat message from Discord to the game server via WebSocket.
 * Returns true if the message was sent, false otherwise.
 */
function sendChatToGame(author: string, message: string): boolean {
    if (!ws || ws.readyState !== WebSocket.OPEN || !wsAuthenticated) {
        console.log("[Discord Bot] WebSocket not ready, cannot send chat");
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
            console.error(`[Discord Bot] Failed to send chat to game: ${error.message}`);
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

// Auto-updating status dashboard
async function updateLiveDashboard() {
    if (!STATUS_DASHBOARD_CHANNEL_ID) {
        return;
    }

    try {
        const channel = (await client.channels.fetch(STATUS_DASHBOARD_CHANNEL_ID)) as TextChannel;
        if (!channel?.isTextBased()) {
            console.error("[Dashboard] Target channel not found or is not text-based.");
            return;
        }

        const status: ServerStatus | null = await fetchServerStatus();

        // Embed content factory
        const embed: any = {
            title: "🧑‍🌾 Stardew Valley Server Status Dashboard",
            timestamp: new Date().toISOString(),
            footer: { text: `Automatically updates every ${STATUS_DASHBOARD_REFRESH_RATE_FORMATTED}` },
        };

        if (!status?.isOnline) {
            embed.color = Colors.Red;
            embed.description =
                "🔴 **The server is currently offline.**\n\n_No game data can be pulled right now. Check back later!_";
        } else {
            const seasonEmojis: Record<string, string> = {
                spring: "🌸 Spring",
                summer: "☀️ Summer",
                fall: "🍂 Fall",
                winter: "❄️ Winter",
            };
            const formattedSeason = seasonEmojis[status.season?.toLowerCase()] || status.season;

            embed.color = Colors.Blue;
            embed.fields = [
                { name: "🏡 Farm Name", value: status.farmName || "Our Farm", inline: true },
                { name: "🗺️ Farm Layout", value: getFarmTypeName(status.farmTypeKey), inline: true },
                {
                    name: "📶 Server Status",
                    value: status.isReady ? "✅ Ready & Running" : "⏳ Saving / Loading",
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
            ];
        }

        // Dispatch/edit
        if (targetMessageId) {
            try {
                const existingMsg = await channel.messages.fetch(targetMessageId);
                await existingMsg.edit({ content: "", embeds: [embed] });
                console.log("[Dashboard] Live status display updated successfully.");
                return;
            } catch (_err) {
                console.log("[Dashboard] Saved message ID missing or deleted. Generating a replacement entry...");
            }
        }

        // Fallback
        const recentMessages = await channel.messages.fetch({ limit: 10 });
        const botOldMessage = recentMessages.find((m) => m.author.id === client.user?.id);

        if (botOldMessage) {
            targetMessageId = botOldMessage.id;
            await botOldMessage.edit({ content: "", embeds: [embed] });
            console.log("[Dashboard] Recovered old message reference and applied edits.");
        } else {
            const newMsg = await channel.send({ embeds: [embed] });
            targetMessageId = newMsg.id;
            console.log("[Dashboard] Fresh status message initialized.");
        }
    } catch (error) {
        console.error(`[Dashboard] Loop execution failed: ${error}`);
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
                console.error(`[Rate Limit] Failed to send cooldown warning: ${err}`);
            }
            return;
        }

        // COMMAND: !status
        if (input === "!status") {
            try {
                const status: ServerStatus | null = await fetchServerStatus();

                if (!status?.isOnline) {
                    await message.reply("❌ **Server Status:** The server is currently offline.");
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
                    `📡 **Server State:** ${status.isReady ? "Ready ✓" : "Busy (Saving/Transitioning) ⏳"}`,
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
                console.error(`[Discord Bot] Failed to add reaction: ${error.message}`);
            }
        }
    }
});

/**
 * Performs extensive permission and sanity checks on startup.
 * Logs warnings for any missing permissions or configuration issues.
 */
async function performStartupChecks(): Promise<void> {
    console.log("[Discord Bot] Performing startup checks...");

    const warnings: string[] = [];
    const errors: string[] = [];

    // Check guilds
    const guildCount = client.guilds.cache.size;
    console.log(`[Discord Bot] Connected to ${guildCount} guild(s)`);

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

                console.log(`[Discord Bot] Chat channel: #${channel.name} in ${channel.guild.name}`);
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

    // Check API connectivity
    try {
        const status = await fetchServerStatus();
        if (status) {
            console.log(`[Discord Bot] API connectivity: OK (server ${status.isOnline ? "online" : "offline"})`);
        } else {
            warnings.push("Could not fetch server status - API may be unavailable");
        }
    } catch {
        warnings.push("API connectivity check failed");
    }

    // Print warnings
    if (warnings.length > 0) {
        console.log("");
        console.log("[Discord Bot] ⚠️  Warnings:");
        for (const warning of warnings) {
            console.log(`  - ${warning}`);
        }
    }

    // Print errors
    if (errors.length > 0) {
        console.log("");
        console.log("[Discord Bot] ❌ Errors:");
        for (const error of errors) {
            console.log(`  - ${error}`);
        }
    }

    if (warnings.length === 0 && errors.length === 0) {
        console.log("[Discord Bot] All checks passed ✓");
    }

    console.log("");
}

client.once(Events.ClientReady, async () => {
    console.log(`[Discord Bot] Logged in as ${client.user?.tag}`);
    console.log(`[Discord Bot] API URL: ${API_URL}`);
    console.log(`[Discord Bot] API authentication: ${API_KEY ? "enabled" : "disabled"}`);
    console.log(`[Discord Bot] Update interval: ${UPDATE_INTERVAL_MS}ms`);

    if (DISCORD_CHAT_CHANNEL_ID) {
        console.log(`[Discord Bot] Chat relay channel: ${DISCORD_CHAT_CHANNEL_ID}`);
    }

    // Perform startup checks
    await performStartupChecks();

    // Initial updates
    updatePresence();
    updateBotNickname();

    // Connect WebSocket for chat relay
    connectWebSocket();

    if (STATUS_DASHBOARD_CHANNEL_ID) {
        await updateLiveDashboard();
        setInterval(updateLiveDashboard, STATUS_DASHBOARD_REFRESH_RATE * 1000);
    }

    // Periodic updates with error handling
    setInterval(() => {
        updatePresence().catch((error) => {
            console.error(`[Discord Bot] Presence update failed: ${error instanceof Error ? error.message : error}`);
        });
    }, UPDATE_INTERVAL_MS);

    setInterval(() => {
        updateBotNickname().catch((error) => {
            console.error(`[Discord Bot] Nickname update failed: ${error instanceof Error ? error.message : error}`);
        });
    }, UPDATE_INTERVAL_MS);
});

client.on(Events.Error, (error) => {
    console.error(`[Discord Bot] Client error: ${error.message}`);
});

// Graceful shutdown
function shutdown() {
    console.log("[Discord Bot] Shutting down...");

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
console.log("[Discord Bot] Starting...");
client.login(DISCORD_BOT_TOKEN);
