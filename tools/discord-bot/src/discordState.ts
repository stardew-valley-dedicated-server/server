/** Discord presentation of a server state: status emoji, presence, and embed color. */

import { Colors, type PresenceStatusData } from "discord.js";
import {
    resolveServerState as resolveSharedState,
    type ServerState,
    type ServerStateKind,
    type StatusSignals,
} from "./serverState";

export type { ServerStateKind, ServerStatus, StatusSignals } from "./serverState";

export interface DiscordServerState extends ServerState {
    /** Colored dot for the presence line; the dashboard embed shows `color` instead. */
    emoji: string;
    presence: PresenceStatusData;
    color: number;
}

const presentation: Record<ServerStateKind, Pick<DiscordServerState, "emoji" | "presence" | "color">> = {
    online: { emoji: "🟢", presence: "online", color: Colors.Blue },
    busy: { emoji: "🟡", presence: "online", color: Colors.Yellow },
    loading: { emoji: "🟡", presence: "idle", color: Colors.Yellow },
    provisioning: { emoji: "🟠", presence: "idle", color: Colors.Orange },
    offline: { emoji: "🔴", presence: "dnd", color: Colors.Red },
};

export function resolveServerState(status: StatusSignals | null): DiscordServerState {
    const state = resolveSharedState(status);
    return { ...state, ...presentation[state.kind] };
}
