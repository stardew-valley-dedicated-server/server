import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import basicSsl from '@vitejs/plugin-basic-ssl'

export default defineConfig({
  plugins: [vue(), basicSsl()],

  build: {
    sourcemap: true,
    // reportCompressedSize: false,
    // minify: false,
  },
})
