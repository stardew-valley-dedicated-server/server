import { Client, Events, GatewayIntentBits, ActivityType } from "discord.js";

// Configuration from environment
const DISCORD_BOT_TOKEN = process.env.DISCORD_BOT_TOKEN;
const STATUS_FILE_PATH = process.env.STATUS_FILE_PATH || "/tmp/server-status.json";

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
  inviteCode: string;
  serverVersion: string;
  isOnline: boolean;
  lastUpdated: string;
}

const client = new Client({
  intents: [GatewayIntentBits.Guilds],
});

/**
 * Reads the server status from the JSON file written by the game mod.
 */
async function readServerStatus(): Promise<ServerStatus | null> {
  try {
    const file = Bun.file(STATUS_FILE_PATH);
    if (!(await file.exists())) {
      return null;
    }
    return await file.json();
  } catch (error) {
    console.error(`[Discord Bot] Failed to read status file: ${error}`);
    return null;
  }
}

/**
 * Updates the bot's presence/status based on server state.
 */
async function updatePresence(): Promise<void> {
  const status = await readServerStatus();

  let activityName: string;

  if (!status || !status.isOnline) {
    activityName = "Server Offline";
  } else {
    const playerInfo = `${status.playerCount}/${status.maxPlayers} players`;
    const inviteCode = status.inviteCode || "No code";
    activityName = `${playerInfo} | ${inviteCode}`;
  }

  client.user?.setPresence({
    activities: [
      {
        name: activityName,
        type: ActivityType.Custom,
      },
    ],
    status: "online",
  });

  console.log(`[Discord Bot] Status updated: ${activityName}`);
}

client.once(Events.ClientReady, () => {
  console.log(`[Discord Bot] Logged in as ${client.user?.tag}`);
  console.log(`[Discord Bot] Monitoring status file: ${STATUS_FILE_PATH}`);
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
