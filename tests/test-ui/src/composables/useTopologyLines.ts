import { type ComputedRef, nextTick, type Ref, ref, watch } from "vue";
import type { InstanceSnapshot } from "../types/state";

export interface TopologyGroup {
    server: InstanceSnapshot;
    clients: InstanceSnapshot[];
}

export function useTopologyLines(params: {
    topologyContainerRef: Ref<HTMLElement | null>;
    topologyGroups: ComputedRef<TopologyGroup[]>;
    unconnectedClients: ComputedRef<InstanceSnapshot[]>;
    layout: Ref<string>;
    viewportWidth: Ref<number>;
}) {
    const connectionLines = ref<
        { groupIdx: number; x1: number; y1: number; x2: number; y2: number; active: boolean }[]
    >([]);

    function updateConnectionLines() {
        if (
            params.layout.value !== "topology" ||
            !params.topologyContainerRef.value ||
            params.viewportWidth.value <= 600
        ) {
            connectionLines.value = [];
            return;
        }

        const lines: typeof connectionLines.value = [];
        const container = params.topologyContainerRef.value;
        const groups = container.querySelectorAll("[data-topology-group]");

        groups.forEach((groupEl, groupIdx) => {
            const serverCard = groupEl.querySelector("[data-topology-server]") as HTMLElement | null;
            const clientCards = groupEl.querySelectorAll("[data-topology-client]");
            if (!serverCard || clientCards.length === 0) {
                return;
            }

            const groupRect = groupEl.getBoundingClientRect();
            const serverRect = serverCard.getBoundingClientRect();

            clientCards.forEach((clientCard) => {
                const clientRect = clientCard.getBoundingClientRect();
                const isActive = clientCard.getAttribute("data-topology-active") === "true";

                // Always vertical: server bottom-center -> client top-center
                lines.push({
                    groupIdx,
                    x1: serverRect.left + serverRect.width / 2 - groupRect.left,
                    y1: serverRect.bottom - groupRect.top,
                    x2: clientRect.left + clientRect.width / 2 - groupRect.left,
                    y2: clientRect.top - groupRect.top,
                    active: isActive,
                });
            });
        });

        connectionLines.value = lines;
    }

    watch(
        [params.topologyGroups, params.unconnectedClients, params.layout, params.viewportWidth],
        () => {
            if (params.layout.value === "topology") {
                // Immediate recalc for DOM changes
                nextTick(updateConnectionLines);
                // Deferred recalc after card animations settle (cards animate to new positions
                // via animateTransition, so getBoundingClientRect reads stale mid-flight rects)
                nextTick(() => setTimeout(updateConnectionLines, 350));
            }
        },
        { deep: true },
    );

    function linesForGroup(groupIdx: number) {
        return connectionLines.value.filter((l) => l.groupIdx === groupIdx);
    }

    function linePath(line: { x1: number; y1: number; x2: number; y2: number }): string {
        const cy = (line.y1 + line.y2) / 2;
        return `M ${line.x1},${line.y1} C ${line.x1},${cy} ${line.x2},${cy} ${line.x2},${line.y2}`;
    }

    return { connectionLines, updateConnectionLines, linesForGroup, linePath };
}
