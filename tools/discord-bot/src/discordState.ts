/** Discord presentation of a server state: emoji label, presence, and embed color. */

import { Colors, type PresenceStatusData } from "discord.js";
import {
    resolveServerState as resolveSharedState,
    type ServerState,
    type ServerStateKind,
    type StatusSignals,
} from "./serverState";

export type { ServerStateKind, ServerStatus, StatusSignals } from "./serverState";

export interface DiscordServerState extends ServerState {
    presence: PresenceStatusData;
    color: number;
}

const presentation: Record<ServerStateKind, { emoji: string; presence: PresenceStatusData; color: number }> = {
    online: { emoji: "🟢", presence: "online", color: Colors.Blue },
    busy: { emoji: "🟡", presence: "online", color: Colors.Yellow },
    loading: { emoji: "🟡", presence: "idle", color: Colors.Yellow },
    provisioning: { emoji: "🟠", presence: "idle", color: Colors.Orange },
    offline: { emoji: "🔴", presence: "dnd", color: Colors.Red },
};

export function resolveServerState(status: StatusSignals | null): DiscordServerState {
    const state = resolveSharedState(status);
    const { emoji, presence, color } = presentation[state.kind];
    return { ...state, label: `${emoji} ${state.label}`, presence, color };
}
