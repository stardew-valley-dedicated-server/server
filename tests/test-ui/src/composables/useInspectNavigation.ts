import { type ComputedRef, computed, ref } from "vue";
import type { InstanceSnapshot } from "../types/state";

export function useInspectNavigation(params: {
    instances: ComputedRef<InstanceSnapshot[]>;
    stoppedInstances: InstanceSnapshot[]; // reactive array, NOT Ref
}) {
    const inspectStack = ref<string[]>([]);
    const inspectId = computed(() =>
        inspectStack.value.length > 0 ? inspectStack.value[inspectStack.value.length - 1] : null,
    );
    const canGoBack = computed(() => inspectStack.value.length > 1);

    // Flat ordered list: servers then clients (live then stopped)
    const allOrderedInstances = computed(() => {
        const live = params.instances.value;
        const stopped = params.stoppedInstances;
        const liveServers = live.filter((i) => i.instanceType === "server");
        const liveClients = live.filter((i) => i.instanceType === "client");
        const stoppedServers = stopped.filter((i) => i.instanceType === "server");
        const stoppedClients = stopped.filter((i) => i.instanceType === "client");
        return [...liveServers, ...stoppedServers, ...liveClients, ...stoppedClients];
    });

    const inspectIndex = computed(() => {
        if (!inspectId.value) return -1;
        return allOrderedInstances.value.findIndex((i) => i.instanceId === inspectId.value);
    });

    const hasPrevInspect = computed(() => inspectIndex.value > 0);
    const hasNextInspect = computed(
        () => inspectIndex.value >= 0 && inspectIndex.value < allOrderedInstances.value.length - 1,
    );

    function prevInspect() {
        if (hasPrevInspect.value) {
            inspectStack.value = [allOrderedInstances.value[inspectIndex.value - 1].instanceId];
        }
    }

    function nextInspect() {
        if (hasNextInspect.value) {
            inspectStack.value = [allOrderedInstances.value[inspectIndex.value + 1].instanceId];
        }
    }

    function openInspect(id: string) {
        inspectStack.value = [id];
    }

    function pushInspect(id: string) {
        inspectStack.value.push(id);
    }

    function closeInspect() {
        inspectStack.value = [];
    }

    function goBackInspect() {
        inspectStack.value.pop();
    }

    const inspectInstance = computed(() => {
        if (!inspectId.value) return null;
        return (
            params.instances.value.find((i) => i.instanceId === inspectId.value) ??
            params.stoppedInstances.find((i) => i.instanceId === inspectId.value) ??
            null
        );
    });

    const inspectPeers = computed(() => {
        if (!inspectInstance.value) return [];
        const id = inspectId.value!;
        const all = [...params.instances.value, ...params.stoppedInstances];
        if (inspectInstance.value.instanceType === "server") {
            const clients = all.filter(
                (i) =>
                    i.instanceType === "client" &&
                    i.history.some((h) => h.event === "leased" && h.serverInstanceId === id),
            );
            return clients.map((c) => ({ instanceId: c.instanceId, label: c.label, instanceType: c.instanceType }));
        } else {
            const serverIds = new Set(
                inspectInstance.value.history
                    .filter((h) => h.event === "leased" && h.serverInstanceId)
                    .map((h) => h.serverInstanceId!),
            );
            return [...serverIds].map((sid) => {
                const srv = all.find((i) => i.instanceId === sid);
                return { instanceId: sid, label: srv?.label ?? sid, instanceType: "server" as const };
            });
        }
    });

    return {
        inspectStack,
        inspectId,
        canGoBack,
        inspectIndex,
        hasPrevInspect,
        hasNextInspect,
        allOrderedInstances,
        openInspect,
        pushInspect,
        closeInspect,
        goBackInspect,
        prevInspect,
        nextInspect,
        inspectInstance,
        inspectPeers,
    };
}
