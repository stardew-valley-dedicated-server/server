<script setup lang="ts">
import { Icon } from "@iconify/vue";
import { onMounted, onUnmounted, ref, watch } from "vue";

const props = defineProps<{
    src: string;
    title: string;
    interactive: boolean;
    /** Base VNC URL without query params, used as stable iframe key to preserve connections. */
    baseUrl?: string;
    /** When true, immediately stop all retries, health checks, and show a disconnected state. */
    stopped?: boolean;
    /** Container disposed mid-run. Halt reconnects, show overlay, but remain non-terminal so runDone can still finalize. */
    retained?: boolean;
    /** Overlay message shown when retained. */
    retainedMessage?: string;
    /** True while the retained overlay should show a spinner (work still in progress, e.g. recording finalizing). */
    retainedBusy?: boolean;
}>();

type LoadState = "loading" | "connected" | "error";

const RETRY_DELAY_S = 5;

const state = ref<LoadState>("loading");
const errorMessage = ref("");
const retryCountdown = ref(0);
const iframeRef = ref<HTMLIFrameElement | null>(null);
let retryTimer: ReturnType<typeof setInterval> | null = null;
let healthTimer: ReturnType<typeof setInterval> | null = null;
let aborted = false;

function onIframeLoad() {
    if (aborted) {
        return;
    }
    // While retained, ignore any in-flight load events from the previous src.
    if (props.retained) {
        return;
    }
    // Ignore load events from about:blank
    const currentSrc = iframeRef.value?.src ?? "";
    if (!currentSrc || currentSrc === "about:blank") {
        return;
    }
    state.value = "connected";
    errorMessage.value = "";
    cancelRetry();
    startHealthCheck();
}

/** Periodically verify the VNC endpoint is still reachable while connected. */
function startHealthCheck() {
    stopHealthCheck();
    healthTimer = setInterval(async () => {
        if (aborted || state.value !== "connected") {
            stopHealthCheck();
            return;
        }
        try {
            await fetch(props.src, { mode: "no-cors", signal: AbortSignal.timeout(5000) });
        } catch {
            if (aborted) {
                return;
            }
            state.value = "error";
            errorMessage.value = "Connection lost";
            stopHealthCheck();
            scheduleRetry();
        }
    }, 5000);
}

function stopHealthCheck() {
    if (healthTimer) {
        clearInterval(healthTimer);
        healthTimer = null;
    }
}

function scheduleRetry() {
    cancelRetry();
    if (aborted) {
        return;
    }
    retryCountdown.value = RETRY_DELAY_S;
    retryTimer = setInterval(() => {
        retryCountdown.value--;
        if (retryCountdown.value <= 0) {
            cancelRetry();
            reload();
        }
    }, 1000);
}

function cancelRetry() {
    if (retryTimer) {
        clearInterval(retryTimer);
        retryTimer = null;
    }
    retryCountdown.value = 0;
}

function reload() {
    if (aborted) {
        return;
    }
    cancelRetry();
    state.value = "loading";
    errorMessage.value = "";
    if (iframeRef.value) {
        iframeRef.value.src = "about:blank";
        requestAnimationFrame(() => {
            if (iframeRef.value && !aborted) {
                iframeRef.value.src = props.src;
            }
        });
    }
}

/** Halt all retry/health-check activity and blank the iframe. Does NOT set aborted. */
function haltReconnects() {
    stopHealthCheck();
    clearLoadTimeout();
    cancelRetry();
    if (iframeRef.value) {
        iframeRef.value.src = "about:blank";
    }
}

/** Stop all activity: clear iframe, halt retries/health checks. Terminal. */
function stop() {
    aborted = true;
    haltReconnects();
    state.value = "error";
    errorMessage.value = "Disconnected";
}

/** Enter retained phase: halt reconnects, show retained overlay. Non-terminal. */
function enterRetained() {
    haltReconnects();
    state.value = "error";
    errorMessage.value = props.retainedMessage ?? "";
}

// React to the stopped prop. Shut everything down immediately.
watch(
    () => props.stopped,
    (stopped) => {
        if (stopped) {
            stop();
        }
    },
);

// React to the retained prop. Halt reconnects without aborting.
watch(
    () => props.retained,
    (retained) => {
        if (retained && !props.stopped) {
            enterRetained();
        }
    },
);

// Keep overlay message in sync while retained (e.g. transitioning from "Finalizing..." to "Recording captured").
watch(
    () => props.retainedMessage,
    (msg) => {
        if (props.retained && !props.stopped && state.value === "error") {
            errorMessage.value = msg ?? "";
        }
    },
);

// Re-load when src changes (e.g. interactive toggle flips query params on a stable key)
watch(
    () => props.src,
    (newSrc) => {
        stopHealthCheck();
        cancelRetry();
        state.value = "loading";
        if (iframeRef.value) {
            iframeRef.value.src = newSrc;
        }
    },
);

// If iframe hasn't fired onload within 10s, assume connection failure
let loadTimeout: ReturnType<typeof setTimeout> | null = null;

function startLoadTimeout() {
    clearLoadTimeout();
    loadTimeout = setTimeout(() => {
        if (state.value === "loading" && !aborted) {
            state.value = "error";
            errorMessage.value = "Connection timed out";
            scheduleRetry();
        }
    }, 10000);
}

function clearLoadTimeout() {
    if (loadTimeout) {
        clearTimeout(loadTimeout);
        loadTimeout = null;
    }
}

watch(
    () => state.value,
    (newState) => {
        if (newState === "loading") {
            startLoadTimeout();
        } else {
            clearLoadTimeout();
        }
    },
);

onMounted(() => {
    if (props.stopped) {
        stop();
    } else if (props.retained) {
        enterRetained();
    } else {
        startLoadTimeout();
    }
});

onUnmounted(() => {
    aborted = true;
    stopHealthCheck();
    clearLoadTimeout();
    cancelRetry();
});
</script>

<template>
  <div class="relative w-full h-full bg-black">
    <!-- Hide iframe when not connected so browser error pages aren't visible -->
    <iframe ref="iframeRef"
            :key="baseUrl ?? src"
            :src="src"
            :title="title"
            class="w-full h-full border-0"
            :class="{ 'invisible': state !== 'connected', 'pointer-events-none': !interactive }"
            loading="eager"
            allow="clipboard-read 'none'; clipboard-write 'none'"
            :tabindex="interactive ? 0 : -1"
            @load="onIframeLoad" />
    <!-- Scroll guard: transparent overlay prevents iframe from swallowing wheel events.
         Only shown in view-only mode; interactive mode needs direct iframe access. -->
    <div v-if="!interactive" class="absolute inset-0" />

    <!-- Loading overlay (opaque so browser error pages can't bleed through) -->
    <div v-if="state === 'loading'"
         class="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-black pointer-events-none">
      <span class="loading loading-spinner loading-sm text-base-content/30" />
      <span class="text-[10px] text-base-content/25">Connecting...</span>
    </div>

    <!-- Error overlay with countdown -->
    <div v-if="state === 'error'"
         class="absolute inset-0 flex flex-col items-center justify-center gap-2 px-4 bg-black">
      <Icon icon="lucide:monitor-off" class="w-8 h-8 text-base-content/20" />
      <div class="flex items-center gap-1.5">
        <span v-if="retained && !stopped && retainedBusy"
              class="loading loading-spinner loading-xs text-base-content/30" />
        <span class="text-[11px] text-base-content/30 text-center">{{ errorMessage }}</span>
      </div>
      <button v-if="!stopped && !retained"
              class="btn btn-ghost btn-xs text-[10px] text-base-content/40 hover:text-base-content/70"
              @click="reload">
        {{ retryCountdown > 0 ? `Retry in ${retryCountdown}s...` : 'Retry' }}
      </button>
    </div>
  </div>
</template>
