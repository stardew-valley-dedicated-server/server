/**
 * Generate an encrypted app ticket for Stardew Valley, without using a real Steam client.
 */

const SteamUser = require("steam-user");

/**
 * @see https://steamdb.info/app/413150/ Stardew Valley
 */
const appId = 413150;

/**
 * @see https://partner.steamgames.com/doc/features/auth?l=english "Tickets will expire 21 days after they are issued"
 */
const ticketMaxAgeInDays = 21;

// Get necessary credentials from environment variables or command line arguments
const username = process.env.STEAM_USERNAME || process.argv[2];
const password = process.env.STEAM_PASSWORD || process.argv[3];

// Usually we don't want to log anything and just print the token data to stdout,
// but allow to enable (verbose) logging for debugging
const isLoggingEnabled = process.env.STEAM_APPTICKET_GENERATOR_LOGGING === "1";
const isVerboseEnabled = process.env.STEAM_APPTICKET_GENERATOR_VERBOSE === "1";

function log(...args) {
    if (isLoggingEnabled) {
        console.log(...args);
    }
}

function logError(...args) {
    if (isLoggingEnabled) {
        console.error(...args);
    }
}

function getExpiry() {
    return Date.now() + ticketMaxAgeInDays * 24 * 60 * 60 * 1000;
}

if (!username || !password) {
    logError("Usage: node index.js <username> <password>");
    logError("Or set STEAM_USERNAME and STEAM_PASSWORD environment variables");
    process.exit(1);
}

const client = new SteamUser();

client.on("loggedOn", () => {
    log("Logged into Steam");
    log(`Requesting encrypted app ticket...`);
    client.getEncryptedAppTicket(appId, (err, ticket) => {
        if (err) {
            logError("Failed to get encrypted app ticket:", err.message);
            process.exit(1);
        }

        log("Requested encrypted app ticket");
        console.log(JSON.stringify({
            Ticket: ticket.toString("base64"),
            SteamId: client.steamID.getSteamID64(), // Probably not even needed, as we don't seem to need a real one
            Created: Date.now(),
            Expiry: getExpiry(),
        }));

        client.logOff();
        process.exit(0);
    });
});

if (isLoggingEnabled) {
    if (isVerboseEnabled) {
        client.on("debug", log);
    }

    client.on("error", (err) => {
        log("Steam error:", err.message);
        process.exit(1);
    });

    client.on("disconnected", () => {
        log("Disconnected from Steam");
        process.exit(0);
    });
}

log("Logging into Steam...");
client.logOn({
    accountName: username,
    password: password,
});
