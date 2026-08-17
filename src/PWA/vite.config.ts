import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'
import { fileURLToPath } from 'node:url'
import { readFileSync } from 'node:fs'

/**
 * The product version, read from the repository's VERSION file — the same file the
 * .NET build stamps the agent and the hub from. The PWA deliberately has no version
 * of its own: agent, hub and app ship together, and three numbers that can disagree
 * would only ever disagree at the moment somebody is trying to report a problem.
 *
 * package.json stays at 0.0.0. It is npm's number, not the product's.
 */
const productVersion = readFileSync(
  fileURLToPath(new URL('../../VERSION', import.meta.url)),
  'utf8',
).trim()

/**
 * Builds the app against a stand-in identity provider instead of Entra.
 *
 * Set only by `npm run build:e2e` and by the Playwright config's web server. An
 * alias rather than a runtime flag, so that the substitute is not merely unused in
 * a production build but absent from it — see `src/auth/adapter.ts`, and the test
 * in `src/auth/authBundle.test.ts` that checks the claim.
 */
const e2e = process.env.VITE_E2E === '1'

export default defineConfig({
  resolve: {
    alias: e2e
      ? [
          {
            find: /^.*\/auth\/impl(\.tsx)?$/,
            replacement: fileURLToPath(new URL('./src/auth/impl.e2e.tsx', import.meta.url)),
          },
        ]
      : [],
  },

  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      // The service worker is hand-written rather than generated: a terminal
      // client has an unusual caching story, and the rules are worth reading.
      strategies: 'injectManifest',
      srcDir: 'src',
      filename: 'sw.ts',
      registerType: 'prompt',
      // The registration is done by hand in main.tsx so the update prompt is the
      // app's own UI rather than an injected script.
      injectRegister: null,

      injectManifest: {
        // An IIFE, not a module: it emits sw.js rather than sw.mjs, and a classic
        // worker is the one shape every browser that matters registers without
        // argument.
        rollupFormat: 'iife',
        // xterm alone is a little over the default 2 MB ceiling, and it is
        // exactly the file a phone on a cellular link should not be fetching
        // twice.
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024,
      },

      manifest: {
        name: '1RemoteCLI',
        short_name: '1RemoteCLI',
        description: 'Attach to the terminal sessions already running on your machines.',
        // Standalone is not cosmetic on iOS: a tab can never receive a push
        // notification, so this is what makes the notification feature possible.
        display: 'standalone',
        orientation: 'portrait',
        start_url: '/',
        scope: '/',
        background_color: '#020617',
        theme_color: '#020617',
        icons: [
          { src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icon-512.png', sizes: '512x512', type: 'image/png' },
          // A maskable icon is cropped to whatever shape the launcher likes, so
          // it is a separate drawing with the artwork pulled inside the safe area.
          { src: '/icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },

      devOptions: {
        // Without this the install prompt cannot be exercised until a production
        // build is deployed, and the iOS behaviour is the whole risk here.
        enabled: true,
        type: 'module',
      },
    }),
  ],

  define: {
    __APP_VERSION__: JSON.stringify(productVersion),
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
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx', 'tests/**/*.test.ts'],
  },
})
