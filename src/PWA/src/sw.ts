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
import { readPushPayload } from './push/notification'

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
 * A session wants something. This is the product's reason for existing on a
 * phone at all: without it, the app only helps if you happen to be looking.
 *
 * A notification is shown for every push, unconditionally. iOS revokes push
 * permission from apps that receive pushes without showing anything, so
 * "nothing worth saying" is not an available option — and having the OS quietly
 * withdraw permission would cost every future notification, not just this one.
 */
self.addEventListener('push', (event) => {
  const plan = readPushPayload(event.data?.text())

  event.waitUntil(
    self.registration.showNotification(plan.title, {
      body: plan.body,
      icon: '/icon-192.png',
      badge: '/icon-192.png',
      tag: plan.tag,
      data: { url: plan.url },
    }),
  )
})

/**
 * Tapping a notification should land in the session it is about, in the app
 * that is already open rather than a second copy of it.
 *
 * Two taps from a locked phone — tap the notification, tap `y` — is the whole
 * target, and every branch here exists to keep the machine list out of the way.
 */
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const target = (event.notification.data as { url?: string } | undefined)?.url ?? '/'

  event.waitUntil(
    (async () => {
      const clients = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
      for (const client of clients) {
        await client.focus()

        // A message rather than a navigation. The app is already running with a
        // live socket and possibly an attached terminal; navigating would throw
        // that away and make the user wait through a reconnect to answer a
        // question that is sitting there now.
        client.postMessage({ type: 'OPEN_SESSION', url: target })
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
