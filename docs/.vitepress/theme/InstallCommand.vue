<script setup lang="ts">
import { ref, useId } from "vue";

// Install/update command block whose URL always matches the deploy it renders in.
// import.meta.env.BASE_URL is VitePress's base ("/" on the latest site, "/preview/" on the preview
// half), so the same source yields docs.junimoserver.com/install.sh vs /preview/install.sh with no
// per-channel hardcoding. Pass `update` on the in-place upgrade pages to drop the `cd` line.
const props = defineProps<{ update?: boolean }>();

const origin = "https://docs.junimoserver.com";
const base = import.meta.env.BASE_URL;
const cd = props.update ? "" : "\ncd junimoserver";

const commands = [
    { lang: "sh", label: "Linux / macOS", code: `curl -fsSL ${origin}${base}install.sh | bash${cd}` },
    { lang: "powershell", label: "Windows", code: `irm ${origin}${base}install.ps1 | iex${cd}` },
];

// Radio ids must be unique per instance so several blocks on one page don't share a tab group.
// useId is stable across SSR and client hydration (Math.random would mismatch and warn).
const uid = useId();
const active = ref(0);
</script>

<template>
    <!-- Reuses VitePress's own .vp-code-group / .language-* styling and its global copy-button
         handler; only the tab toggle is ours (static markup can't switch the active block). -->
    <div class="vp-code-group vp-adaptive-theme">
        <div class="tabs">
            <template v-for="(c, i) in commands" :key="c.lang">
                <input
                    :id="`install-${uid}-${i}`"
                    type="radio"
                    :name="`install-${uid}`"
                    :checked="active === i"
                    @change="active = i"
                />
                <label :for="`install-${uid}-${i}`">{{ c.label }}</label>
            </template>
        </div>
        <div class="blocks">
            <div
                v-for="(c, i) in commands"
                :key="c.lang"
                :class="[`language-${c.lang}`, 'vp-adaptive-theme', { active: active === i }]"
            >
                <button title="Copy Code" class="copy"></button>
                <span class="lang">{{ c.lang }}</span>
                <pre class="vp-code"><code>{{ c.code }}</code></pre>
            </div>
        </div>
    </div>
</template>
