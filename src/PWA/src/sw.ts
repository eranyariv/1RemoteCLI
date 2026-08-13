/// <reference lib="webworker" />

/**
 * The service worker.
 *
 * It exists for two reasons, in this order of importance:
 *
 *   1. iOS will not deliver a web push notification to anything that is not an
 *      installed app, and will not treat anything without a service worker as
 *      installable. Notifications - the whole "tell me when the agent needs me"
 *      half of the product - are downstream of this file existing.
 *   2. Opening the app on a bad connection should show the app saying it has no
 *      connection, not the browser's error page. The shell is cached; the
 *      sessions behind it are live and cannot be.
 *
 * Deliberately hand-written rather than generated. A terminal client has an
 * unusual caching story - there is nothing offline-first about it - so the
 * handful of rules is worth being able to read. The rules themselves live in
 * `install/shellCache.ts`, where tests can reach them.
 */

import {
  assetResponse,
  cacheName,
  navigationResponse,
  shouldHandle,
  staleCacheNames,
  type PrecacheEntry,
} from './install/shellCache'

declare const self: ServiceWorkerGlobalScope & {
  /** Injected at build time by vite-plugin-pwa: the hashed app shell. */
  __WB_MANIFEST: PrecacheEntry[]
}

const manifest = self.__WB_MANIFEST
const CacheName = cacheName(manifest)

self.addEventListener('install', (event) => {
  event.waitUntil(
    (async () => {
      const cache = await caches.open(CacheName)
      await cache.addAll(manifest.map((entry) => entry.url))
      // Do not wait for every tab to close. A phone showing a stale build is the
      // failure this app cares about, and there is no unsaved work here to lose:
      // the session lives on the machine, not the phone.
      await self.skipWaiting()
    })(),
  )
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    (async () => {
      const names = await caches.keys()
      await Promise.all(staleCacheNames(names, CacheName).map((name) => caches.delete(name)))
      await self.clients.claim()
    })(),
  )
})

self.addEventListener('fetch', (event) => {
  const request = event.request
  if (!shouldHandle(request, self.location.origin)) return

  event.respondWith(
    (async () => {
      const cache = await caches.open(CacheName)
      return request.mode === 'navigate'
        ? navigationResponse(request, cache, fetch)
        : assetResponse(request, cache, fetch)
    })(),
  )
})

/**
 * Tapping a notification should land in the app that is already open rather than
 * stacking up copies of it. The deep link to a specific session arrives with the
 * push payload in 4.5; the focusing behaviour is the part worth having now.
 */
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const target = (event.notification.data as { url?: string } | undefined)?.url ?? '/'

  event.waitUntil(
    (async () => {
      const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
      for (const client of clients) {
        await client.focus()
        if (target !== '/') await client.navigate(target)
        return
      }
      await self.clients.openWindow(target)
    })(),
  )
})

self.addEventListener('message', (event) => {
  if ((event.data as { type?: string } | undefined)?.type === 'SKIP_WAITING') {
    void self.skipWaiting()
  }
})
