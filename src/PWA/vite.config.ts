import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

import pkg from './package.json' with { type: 'json' }

export default defineConfig({
  plugins: [react(), tailwindcss()],

  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },

  server: {
    // Must match a redirect URI registered on the Entra app. See docs/azure-setup.md.
    port: 5173,
    strictPort: true,
    // Lets a real phone on the same network reach the dev server, which is the
    // only way to test the thing this product is actually for.
    host: true,
  },

  preview: {
    port: 4173,
    strictPort: true,
    host: true,
  },

  test: {
    environment: 'jsdom',
    globals: false,
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
  },
})
