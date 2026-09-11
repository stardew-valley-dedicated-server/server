/**
 * Maps the game server's /status response to a displayed state. Shared by the Discord bot
 * (see discordState.ts) and the docs site's status widget, so both report the same state
 * for the same response. Before the mod's API is up, the game container itself answers
 * /status with `isOnline: false` plus a `phase`, which is what separates a provisioning
 * server from a dead one.
 */

export interface StatusSignals {
    isOnline: boolean;
    isReady: boolean;
    /** "downloading" or "starting" while the container's startup script answers; absent once the mod does. */
    phase?: string;
}

/**
 * The mod's /status response. Owner: `ServerStatus` in mod/JunimoServer/Services/Api/ApiService.cs
 * (camelCased by the JSON serializer); keep the two in sync.
 */
export interface ServerStatus extends StatusSignals {
    playerCount: number;
    maxPlayers: number;
    /** The one code to show players: the universal S-code. Null until a Galaxy lobby exists. */
    steamInviteCode: string | null;
    /** Whether Steam clients can join the code right now (the Galaxy lobby carries the relay stamp). */
    steamRelayReady: boolean;
    /** Galaxy lobby state: "connected" | "recovering" | "down". Null in LAN-only mode. */
    galaxyLobby: string | null;
    /** Steam GameServer session state: "connected" | "lost". */
    steamSession: string;
    /** Sidecar auth/token health: "ok" | "expiring" | "unavailable". Null in LAN-only mode. */
    authReadiness: string | null;
    serverVersion: string;
    gameVersion: string;
    dayTransitionComplete: boolean;
    lastUpdated: string;
    farmName: string;
    /** Deployment display name from SERVER_NAME; empty when unset. */
    serverName: string;
    /** ISO 8601 UTC time the server process started; null until known. */
    startedAtUtc: string | null;
    day: number;
    season: string;
    year: number;
    timeOfDay: number;
    farmTypeKey: string;
    isPaused: boolean;
    /** Measured game ticks per second, averaged over the last 30 seconds. */
    tps: number;
    version: number;
}

export type ServerStateKind =
    | "online" // game loaded and accepting players
    | "busy" // game loaded, mid save / day transition / festival / wedding
    | "loading" // mod API up, save not loaded yet
    | "provisioning" // container up, game files downloading or the game process booting
    | "offline"; // /status unreachable

export interface ServerState {
    kind: ServerStateKind;
    label: string;
    detail: string;
    /** Shown in place of game data while there is none; absent once the game is loaded. */
    hint?: string;
}

const NO_GAME_DATA_HINT = "Game data appears once the save is loaded.";

/**
 * The one invite code to show players: the universal S-code, exposed whenever a Galaxy lobby
 * exists. GOG players can join it immediately; Steam players join once `steamRelayReady` is true
 * (surfaced separately — see `describeInviteAvailability`). The G-code is never exposed, since a
 * Steam player who used it would join over Galaxy and get a farmhand their Steam identity never sees.
 */
export function joinableInviteCode(status: Pick<ServerStatus, "steamInviteCode">): string | null {
    return status.steamInviteCode || null;
}

/**
 * A short note to show alongside the code (or in its place). When a code exists but the Steam relay
 * is not yet ready, Steam players must wait; when there is no code, the connectivity state explains why.
 */
export function describeInviteAvailability(
    status: Pick<ServerStatus, "steamInviteCode" | "steamRelayReady" | "galaxyLobby" | "steamSession">,
): string | null {
    if (joinableInviteCode(status)) {
        // steamRelayReady is false both at normal startup (the stamp lands ~1s after the code) and
        // during a relay recovery, so "connecting" fits both; GOG players can already join either way.
        return status.steamRelayReady ? null : "Steam relay connecting — GOG players can join now";
    }
    if (status.galaxyLobby === null) {
        // LAN-only server (no Steam/Galaxy configured): there will never be a code.
        return "invite codes are disabled in LAN-only mode";
    }
    // Steam first: the Steam session is upstream of the Galaxy lobby, so when both are down it is
    // the root cause. Same order as the mod's InviteCodes.UnavailableReason.
    if (status.steamSession === "lost") {
        return "Steam session reconnecting";
    }
    if (status.galaxyLobby === "recovering") {
        return "Galaxy lobby reconnecting";
    }
    return "connecting…";
}

/** The in-game calendar as "Spring 14, Year 1". */
export function formatStardewDate(status: Pick<ServerStatus, "day" | "season" | "year">): string {
    const season = status.season ? `${status.season[0].toUpperCase()}${status.season.slice(1)} ` : "";
    return `${season}${status.day}, Year ${status.year}`;
}

/** The game's HHMM clock integer (600, 1330, 2550) as a 12-hour time; hours past 24 are after midnight. */
export function formatStardewTime(timeOfDay: number): string {
    const hours24 = Math.floor(timeOfDay / 100) % 24;
    const minutes = timeOfDay % 100;
    const hours12 = hours24 % 12 || 12;
    return `${hours12}:${String(minutes).padStart(2, "0")} ${hours24 < 12 ? "AM" : "PM"}`;
}

/** Coarse uptime for display ("3d 4h", "4h 12m", "12m"); precise seconds would only churn between polls. */
export function formatUptime(startedAtUtc: string, now: number): string {
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

/** `null` is an unreachable /status: the port is closed, so the server is offline. */
export function resolveServerState(status: StatusSignals | null): ServerState {
    if (status === null) {
        return {
            kind: "offline",
            label: "Offline",
            detail: "Currently unreachable.",
            hint: "No game data can be pulled right now. Check back later!",
        };
    }

    if (status.isOnline) {
        return status.isReady
            ? { kind: "online", label: "Online", detail: "Ready to join" }
            : { kind: "busy", label: "Busy", detail: "Saving or running an event." };
    }

    switch (status.phase) {
        case "downloading":
            return {
                kind: "provisioning",
                label: "Starting",
                detail: "Downloading game files.",
                hint: NO_GAME_DATA_HINT,
            };
        case "starting":
            return { kind: "provisioning", label: "Starting", detail: "Launching the game.", hint: NO_GAME_DATA_HINT };
        default:
            return { kind: "loading", label: "Starting", detail: "Loading the save.", hint: NO_GAME_DATA_HINT };
    }
}
