/**
 * Maps the game server's /status response to the state the bot displays. Before the mod's API
 * is up, the game container itself answers /status with `isOnline: false` plus a `phase`, which
 * is what separates a provisioning server from a dead one.
 */

import { Colors, type PresenceStatusData } from "discord.js";

export interface StatusSignals {
    isOnline: boolean;
    isReady: boolean;
    /** "downloading" or "starting" while the container's startup script answers; absent once the mod does. */
    phase?: string;
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
    presence: PresenceStatusData;
    color: number;
}

/** `null` is an unreachable /status: the port is closed, so the server is offline. */
export function resolveServerState(status: StatusSignals | null): ServerState {
    if (status === null) {
        return {
            kind: "offline",
            label: "🔴 Offline",
            detail: "The server is offline.",
            presence: "dnd",
            color: Colors.Red,
        };
    }

    if (status.isOnline) {
        return status.isReady
            ? {
                  kind: "online",
                  label: "🟢 Online",
                  detail: "Ready & running.",
                  presence: "online",
                  color: Colors.Blue,
              }
            : {
                  kind: "busy",
                  label: "🟡 Busy",
                  detail: "Saving, changing day, or running an event.",
                  presence: "online",
                  color: Colors.Yellow,
              };
    }

    switch (status.phase) {
        case "downloading":
            return {
                kind: "provisioning",
                label: "🟠 Starting",
                detail: "Downloading game files.",
                presence: "idle",
                color: Colors.Orange,
            };
        case "starting":
            return {
                kind: "provisioning",
                label: "🟠 Starting",
                detail: "Launching the game.",
                presence: "idle",
                color: Colors.Orange,
            };
        default:
            return {
                kind: "loading",
                label: "🟡 Starting",
                detail: "Loading the save.",
                presence: "idle",
                color: Colors.Yellow,
            };
    }
}
