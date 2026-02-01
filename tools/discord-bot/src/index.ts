import { Client, Events, GatewayIntentBits, ActivityType } from "discord.js";

// Configuration from environment
const DISCORD_BOT_TOKEN = process.env.DISCORD_BOT_TOKEN;
const API_URL = process.env.API_URL || "http://server:8080";

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
}

const client = new Client({
  intents: [GatewayIntentBits.Guilds],
});

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

client.once(Events.ClientReady, () => {
  console.log(`[Discord Bot] Logged in as ${client.user?.tag}`);
  console.log(`[Discord Bot] API URL: ${API_URL}`);
  console.log(`[Discord Bot] Update interval: ${UPDATE_INTERVAL_MS}ms`);

  // Initial status update
  updatePresence();

  // Periodic updates
  setInterval(updatePresence, UPDATE_INTERVAL_MS);
});

client.on(Events.Error, (error) => {
  console.error(`[Discord Bot] Client error: ${error.message}`);
});

// Graceful shutdown
process.on("SIGINT", () => {
  console.log("[Discord Bot] Shutting down...");
  client.destroy();
  process.exit(0);
});

process.on("SIGTERM", () => {
  console.log("[Discord Bot] Shutting down...");
  client.destroy();
  process.exit(0);
});

// Start the bot
console.log("[Discord Bot] Starting...");
client.login(DISCORD_BOT_TOKEN);
