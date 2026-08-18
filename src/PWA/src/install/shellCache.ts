/**
 * The caching rules the service worker applies, separated from the worker so
 * they can be tested. A service worker is one of the hardest things in a web app
 * to observe by hand - it survives reloads, updates on its own schedule, and
 * fails silently - so the parts with decisions in them are kept out here where a
 * test can reach them.
 */

export interface PrecacheEntry {
  url: string
  revision: string | null
}

export const CachePrefix = '1remotecli-shell-'
export const ShellUrl = '/index.html'

/**
 * Vite content-hashes every emitted file, so the precache manifest changes on
 * any build that changed anything. Fingerprinting it yields a cache name that
 * rolls exactly when the contents do: no version constant anybody has to
 * remember to bump, and no cache ever shared between two different builds.
 */
export function fingerprint(entries: readonly PrecacheEntry[]): string {
  let hash = 0x811c9dc5
  for (const entry of entries) {
    const key = `${entry.url}@${entry.revision ?? ''}`
    for (let index = 0; index < key.length; index += 1) {
      hash ^= key.charCodeAt(index)
      hash = Math.imul(hash, 0x01000193)
    }
  }
  return (hash >>> 0).toString(36)
}

export function cacheName(entries: readonly PrecacheEntry[]): string {
  return `${CachePrefix}${fingerprint(entries)}`
}

export function staleCacheNames(existing: readonly string[], current: string): string[] {
  return existing.filter((name) => name.startsWith(CachePrefix) && name !== current)
}

/**
 * Whether a navigation is the app shell rather than a page that exists as a file.
 *
 * The hub serves the shell for any path without a file extension and the real file
 * for any path with one, so the extension is the same signal on both sides. It
 * matters because the shell is cached under one fixed key: without this, browsing
 * to a static page such as `/readme.html` would store *that* page as the shell,
 * and the next offline launch would open the readme instead of the client.
 */
export function isShellNavigation(url: string): boolean {
  // A base, because a Request's url is absolute in a browser but relative in a test,
  // and URL refuses a relative one on its own.
  const last = new URL(url, 'http://shell.invalid').pathname.split('/').pop() ?? ''
  return !last.includes('.')
}

/**
 * Navigations are network-first.
 *
 * Offline-first would be the usual choice and is wrong here: this is a client for
 * a live connection, so a cached shell is never more useful than a fresh one, and
 * serving last week's client to a phone that has a working connection risks
 * talking a stale protocol to a hub that has moved on. The cache is the fallback,
 * not the default - it exists so that opening the app in a lift shows the app
 * saying it has no connection, rather than the browser's dinosaur.
 */
export async function navigationResponse(
  request: Request,
  cache: Cache,
  fetcher: typeof fetch,
): Promise<Response> {
  const shell = isShellNavigation(request.url)

  try {
    const response = await fetcher(request)
    // Only a successful navigation is worth keeping. Caching a 500 would turn one
    // bad deploy into a permanently broken app on that phone.
    if (response.ok) await cache.put(shell ? ShellUrl : request.url, response.clone())
    return response
  } catch (error) {
    // A static page falls back to itself, never to the shell: handing the client
    // to somebody who asked for the readme is worse than the browser's own
    // offline page, because it looks like the link was wrong.
    const cached = await cache.match(shell ? ShellUrl : request.url)
    if (cached) return cached
    throw error
  }
}

/**
 * Everything precached is content-hashed, so a hit is always correct and a miss
 * is always a genuine request for something the cache has never seen.
 */
export async function assetResponse(
  request: Request,
  cache: Cache,
  fetcher: typeof fetch,
): Promise<Response> {
  const cached = await cache.match(request)
  return cached ?? (await fetcher(request))
}

/**
 * Whether the worker should handle this request at all.
 *
 * The hub connection is a WebSocket to another origin and the identity provider
 * is a redirect to a third; both must pass straight through. A service worker
 * that intercepts an auth redirect is a very confusing thing to debug.
 */
export function shouldHandle(request: Request, origin: string): boolean {
  if (request.method !== 'GET') return false
  return new URL(request.url).origin === origin
}
