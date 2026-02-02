import { Client, Events, GatewayIntentBits, ActivityType, TextChannel } from "discord.js";

// Configuration from environment
const DISCORD_BOT_TOKEN = process.env.DISCORD_BOT_TOKEN;
const API_URL = process.env.API_URL || "http://server:8080";
const WS_URL = process.env.WS_URL || API_URL.replace("http://", "ws://").replace("https://", "wss://") + "/ws";
const DISCORD_CHAT_CHANNEL_ID = process.env.DISCORD_CHAT_CHANNEL_ID;
const DISCORD_BOT_NICKNAME = process.env.DISCORD_BOT_NICKNAME;

// Discord rate limit for presence updates is ~5 per 20 seconds.
// 30 seconds is a safe default that won't trigger rate limits.
const MIN_UPDATE_INTERVAL_MS = 20_000;
const UPDATE_INTERVAL_MS = Math.max(
  parseInt(process.env.UPDATE_INTERVAL_MS || "30000", 10),
  MIN_UPDATE_INTERVAL_MS
);

if (!DISCORD_BOT_TOKEN) {
  console.log("[Discord Bot] DISCORD_BOT_TOKEN not set - bot disabled");
  process.exit(0);
}

interface ServerStatus {
  playerCount: number;
  maxPlayers: number;
  steamInviteCode: string | null;
  gogInviteCode: string | null;
  serverVersion: string;
  isOnline: boolean;
  lastUpdated: string;
  farmName?: string;
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
 * Fetches the server status from the HTTP API.
 */
async function fetchServerStatus(): Promise<ServerStatus | null> {
  try {
    const response = await fetch(`${API_URL}/status`, {
      signal: AbortSignal.timeout(5000),
    });

    if (!response.ok) {
      console.error(
        `[Discord Bot] API request failed: ${response.status} ${response.statusText}`
      );
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

  if (!status || !status.isOnline) {
    activityName = "Server Offline";
  } else {
    const playerInfo = `${status.playerCount}/${status.maxPlayers} players`;
    const inviteCode = status.steamInviteCode || status.gogInviteCode || "No code";
    activityName = `${playerInfo} | ${inviteCode}`;
  }

  client.user?.setPresence({
    activities: [
      {
        name: activityName,
        type: ActivityType.Watching,
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

  if (!nickname) return;

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

  console.log(`[Discord Bot] Connecting to WebSocket: ${WS_URL}`);

  try {
    ws = new WebSocket(WS_URL);

    ws.onopen = () => {
      console.log("[Discord Bot] WebSocket connected");

      // Start heartbeat
      if (wsHeartbeatTimer) clearInterval(wsHeartbeatTimer);
      wsHeartbeatTimer = setInterval(() => {
        if (ws?.readyState === WebSocket.OPEN) {
          ws.send(JSON.stringify({ type: "ping" }));
        }
      }, 30000);
    };

    ws.onmessage = async (event) => {
      try {
        const msg: WebSocketMessage = JSON.parse(event.data.toString());

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

    ws.onerror = (error) => {
      console.error(`[Discord Bot] WebSocket error:`, error);
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
 */
function sendChatToGame(author: string, message: string): void {
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    console.log("[Discord Bot] WebSocket not connected, cannot send chat");
    return;
  }

  ws.send(JSON.stringify({
    type: "chat_send",
    payload: { author, message }
  }));
}

// Handle Discord messages for chat relay
client.on(Events.MessageCreate, async (message) => {
  // Ignore bot messages
  if (message.author.bot) return;

  // Only process messages from the configured chat channel
  if (message.channel.id !== DISCORD_CHAT_CHANNEL_ID) return;

  // Get display name (server nickname if set, otherwise global display name)
  const displayName = message.member?.displayName || message.author.displayName;

  sendChatToGame(displayName, message.content);
});

client.once(Events.ClientReady, () => {
  console.log(`[Discord Bot] Logged in as ${client.user?.tag}`);
  console.log(`[Discord Bot] API URL: ${API_URL}`);
  console.log(`[Discord Bot] Update interval: ${UPDATE_INTERVAL_MS}ms`);

  if (DISCORD_CHAT_CHANNEL_ID) {
    console.log(`[Discord Bot] Chat relay channel: ${DISCORD_CHAT_CHANNEL_ID}`);
  }

  // Initial updates
  updatePresence();
  updateBotNickname();

  // Connect WebSocket for chat relay
  connectWebSocket();

  // Periodic updates
  setInterval(updatePresence, UPDATE_INTERVAL_MS);
  setInterval(updateBotNickname, UPDATE_INTERVAL_MS);
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
