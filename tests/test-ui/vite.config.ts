import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import basicSsl from '@vitejs/plugin-basic-ssl'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [vue(), tailwindcss(), basicSsl()],

  build: {
    sourcemap: true,
    // reportCompressedSize: false,
    // minify: false,
  },
})
