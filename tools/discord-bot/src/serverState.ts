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
    steamInviteCode: string | null;
    gogInviteCode: string | null;
    serverVersion: string;
    gameVersion: string;
    dayTransitionComplete: boolean;
    lastUpdated: string;
    farmName: string;
    day: number;
    season: string;
    year: number;
    timeOfDay: number;
    farmTypeKey: string;
    isPaused: boolean;
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

/** `null` is an unreachable /status: the port is closed, so the server is offline. */
export function resolveServerState(status: StatusSignals | null): ServerState {
    if (status === null) {
        return {
            kind: "offline",
            label: "Offline",
            detail: "The server is offline.",
            hint: "No game data can be pulled right now. Check back later!",
        };
    }

    if (status.isOnline) {
        return status.isReady
            ? { kind: "online", label: "Online", detail: "Ready & running." }
            : { kind: "busy", label: "Busy", detail: "Saving, changing day, or running an event." };
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
