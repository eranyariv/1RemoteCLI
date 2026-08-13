/// <reference types="vite/client" />

/** Injected by Vite from package.json, so the hub sees a real client version. */
declare const __APP_VERSION__: string

interface ImportMetaEnv {
  /**
   * Overrides the compiled-in hub address. Set it in `.env.local` to point a dev
   * build at a hub running on your own machine.
   */
  readonly VITE_HUB_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
