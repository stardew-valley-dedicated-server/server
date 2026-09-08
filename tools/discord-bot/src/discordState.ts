/** Discord presentation of a server state: status emoji, presence, and embed color. */

import type { PresenceStatusData } from "discord.js";
import {
    resolveServerState as resolveSharedState,
    type ServerState,
    type ServerStateKind,
    type StatusSignals,
} from "./serverState";

export type { ServerStateKind, ServerStatus, StatusSignals } from "./serverState";

export interface DiscordServerState extends ServerState {
    /** Status dot on the embed headline; its color matches `color` so the dot and accent bar read as one. */
    emoji: string;
    presence: PresenceStatusData;
    color: number;
}

// Each color is the Twemoji hex of that state's headline dot (the emoji Discord renders), so the
// accent bar matches the dot exactly. Resync if the dot glyphs ever change.
const presentation: Record<ServerStateKind, Pick<DiscordServerState, "emoji" | "presence" | "color">> = {
    online: { emoji: "🟢", presence: "online", color: 0x78b159 },
    busy: { emoji: "🟠", presence: "online", color: 0xf4900c },
    loading: { emoji: "🟡", presence: "idle", color: 0xfdcb58 },
    provisioning: { emoji: "🟠", presence: "idle", color: 0xf4900c },
    offline: { emoji: "🔴", presence: "dnd", color: 0xdd2e44 },
};

export function resolveServerState(status: StatusSignals | null): DiscordServerState {
    const state = resolveSharedState(status);
    return { ...state, ...presentation[state.kind] };
}
