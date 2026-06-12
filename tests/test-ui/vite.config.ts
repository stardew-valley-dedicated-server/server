import tailwindcss from "@tailwindcss/vite";
import basicSsl from "@vitejs/plugin-basic-ssl";
import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";

export default defineConfig({
    plugins: [vue(), tailwindcss(), basicSsl()],

    build: {
        sourcemap: true,
        // reportCompressedSize: false,
        // minify: false,
    },
});
