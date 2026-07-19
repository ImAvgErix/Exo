import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

// Built assets land in Exo/wwwroot and are hosted via WebView2 virtual host.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: './',
  build: {
    outDir: path.resolve(__dirname, '../Exo/wwwroot'),
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    port: 5173,
    strictPort: true,
  },
})
