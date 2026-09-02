import { describe, expect, test } from "bun:test";
import { resolveServerState } from "./serverState";

describe("resolveServerState", () => {
    test("unreachable API is offline", () => {
        const state = resolveServerState(null);
        expect(state.kind).toBe("offline");
        expect(state.presence).toBe("dnd");
    });

    test("online and ready", () => {
        const state = resolveServerState({ isOnline: true, isReady: true });
        expect(state.kind).toBe("online");
        expect(state.presence).toBe("online");
    });

    test("online but not ready is busy, still online", () => {
        const state = resolveServerState({ isOnline: true, isReady: false });
        expect(state.kind).toBe("busy");
        expect(state.presence).toBe("online");
    });

    test("mod API up without a loaded save is loading", () => {
        const state = resolveServerState({ isOnline: false, isReady: false });
        expect(state.kind).toBe("loading");
        expect(state.presence).toBe("idle");
    });

    test("startup phases from the container are provisioning", () => {
        const downloading = resolveServerState({ isOnline: false, isReady: false, phase: "downloading" });
        const starting = resolveServerState({ isOnline: false, isReady: false, phase: "starting" });
        expect(downloading.kind).toBe("provisioning");
        expect(starting.kind).toBe("provisioning");
        expect(downloading.detail).not.toBe(starting.detail);
    });

    test("an unknown phase falls back to loading", () => {
        expect(resolveServerState({ isOnline: false, isReady: false, phase: "???" }).kind).toBe("loading");
    });

    test("phase never overrides an online server", () => {
        expect(resolveServerState({ isOnline: true, isReady: true, phase: "starting" }).kind).toBe("online");
    });
});
