/**
 * Registering the service worker, and noticing when a new one is waiting.
 *
 * Kept as a thin adapter over the browser API with no logic worth testing: the
 * decisions live in `standalone.ts`, which is pure. What is here is the awkward
 * part - the dev and production workers live at different URLs, and a waiting
 * worker has to be nudged rather than left to take over whenever the last tab
 * happens to close.
 */

const ProductionUrl = '/sw.js'
/** vite-plugin-pwa serves an unbundled worker in dev, so the URL differs. */
const DevelopmentUrl = '/dev-sw.js?dev-sw'

export type UpdateHandler = (activate: () => void) => void

export function registerServiceWorker(onUpdateReady: UpdateHandler): void {
  if (!('serviceWorker' in navigator)) return

  const url = import.meta.env.DEV ? DevelopmentUrl : ProductionUrl
  const type: WorkerType = import.meta.env.DEV ? 'module' : 'classic'

  void navigator.serviceWorker.register(url, { type, scope: '/' }).then((registration) => {
    // A worker already waiting means the page was opened against a build the
    // browser has since replaced. That is the case worth surfacing immediately.
    if (registration.waiting) offer(registration.waiting)

    registration.addEventListener('updatefound', () => {
      const installing = registration.installing
      if (!installing) return

      installing.addEventListener('statechange', () => {
        // `controller` is null on the very first install, which is not an update
        // and must not prompt: nothing is being replaced.
        if (installing.state === 'installed' && navigator.serviceWorker.controller) {
          offer(installing)
        }
      })
    })
  })

  function offer(worker: ServiceWorker): void {
    onUpdateReady(() => {
      // The reload is driven by the controller change rather than fired blind,
      // so the new page is served by the new worker rather than racing it.
      navigator.serviceWorker.addEventListener('controllerchange', () => window.location.reload(), {
        once: true,
      })
      worker.postMessage({ type: 'SKIP_WAITING' })
    })
  }
}
