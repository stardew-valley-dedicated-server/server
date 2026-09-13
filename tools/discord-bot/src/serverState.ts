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

/** Galaxy lobby state; null when Steam auth isn't configured. */
export type GalaxyLobbyState = "connected" | "recovering" | "down";
/** Steam GameServer session state. */
export type SteamSessionState = "connected" | "lost";
/** Sidecar auth/token health; "unknown" until the first poll reaches the sidecar. */
export type AuthReadiness = "unknown" | "ok" | "expiring" | "unavailable";
/**
 * Whether the invite code is usable and by whom. Owner: `ConnectionStatusCode` in
 * mod/JunimoServer/Util/ConnectivitySignals.cs; the mod derives it from the raw signals below.
 */
export type ConnectionStatusCode =
    | "ready"
    | "steamRelayPending"
    | "reconnecting"
    | "starting"
    | "steamSessionDown"
    | "inviteUnavailable";

/**
 * The mod's /status response. Owner: `ServerStatus` in mod/JunimoServer/Services/Api/ApiService.cs
 * (camelCased by the JSON serializer); keep the two in sync.
 */
export interface ServerStatus extends StatusSignals {
    playerCount: number;
    maxPlayers: number;
    /** The one code to show players: the universal S-code. Null until a Galaxy lobby exists. */
    inviteCode: string | null;
    /** Whether Steam clients can join using the code yet (the Galaxy lobby carries the relay stamp). */
    steamRelayReady: boolean;
    /** Null when Steam auth isn't configured. */
    galaxyLobbyState: GalaxyLobbyState | null;
    steamSessionState: SteamSessionState;
    /** Null when Steam auth isn't configured. */
    authReadiness: AuthReadiness | null;
    connectionStatusCode: ConnectionStatusCode;
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
 * (surfaced separately — see `connectionStatusText`). The G-code is never exposed, since a
 * Steam player who used it would join over Galaxy and get a farmhand their Steam identity never sees.
 */
export function joinableInviteCode(status: Pick<ServerStatus, "inviteCode">): string | null {
    return status.inviteCode || null;
}

/**
 * Display text per connection status code. `ready` shows the code alone; the ellipsis marks
 * in-progress states, and the settled `inviteUnavailable` has none. Every code must map (type-checked).
 */
export const CONNECTION_STATUS_TEXT: Record<ConnectionStatusCode, string | null> = {
    ready: null,
    steamRelayPending: "GOG ready · Steam connecting…",
    reconnecting: "reconnecting…",
    starting: "starting up…",
    steamSessionDown: "connecting to Steam…",
    inviteUnavailable: "not used on this server",
};

/**
 * The text to show with the invite code, or in its place when there is none. Null when the code is
 * ready (shown alone). The display rule is the same on every surface: `code (text)` while a code is
 * present, else the text standalone. A server predating the field yields null.
 */
export function connectionStatusText(status: Pick<ServerStatus, "connectionStatusCode">): string | null {
    return CONNECTION_STATUS_TEXT[status.connectionStatusCode] ?? null;
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
