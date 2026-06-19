import tailwindcss from "@tailwindcss/vite";
import basicSsl from "@vitejs/plugin-basic-ssl";
import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";

export default defineConfig({
    // Relative base so built asset URLs are ./assets/… — the report bundle is
    // served from an unknown-at-build-time subpath (and over file:// offline).
    // The index.html <base href="./"> resolves them against the served dir.
    //
    // Do NOT "fix" this back to the standard base:"/": that bakes the deploy path
    // in at build time, which only works when the path is fixed. One build here
    // ships to N per-run URLs (e2e/<branch>/<run>/), so no single base is correct
    // — resolution must defer to serve time, which the relative base does. (Images
    // and fonts are imported from src/assets so Vite hashes them into ./assets/;
    // public/ is reserved for must-stay-static files like mock-artifacts/.)
    base: "./",

    plugins: [vue(), tailwindcss(), basicSsl()],

    build: {
        sourcemap: true,
        // reportCompressedSize: false,
        // minify: false,
    },
});
