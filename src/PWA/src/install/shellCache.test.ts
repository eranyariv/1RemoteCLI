import { describe, expect, it, vi } from 'vitest'

import {
  assetResponse,
  cacheName,
  fingerprint,
  navigationResponse,
  shouldHandle,
  staleCacheNames,
  ShellUrl,
} from './shellCache'

/** A cache small enough to reason about, with the same surface the worker uses. */
class FakeCache {
  entries = new Map<string, Response>()

  put = vi.fn(async (url: string, response: Response) => {
    this.entries.set(url, response)
  })

  match = vi.fn(async (key: string | Request) => {
    const url = typeof key === 'string' ? key : new URL(key.url).pathname
    return this.entries.get(url)
  })

  get cache(): Cache {
    return this as unknown as Cache
  }
}

function request(url: string, init: { mode?: RequestMode; method?: string } = {}): Request {
  return {
    url,
    method: init.method ?? 'GET',
    mode: init.mode ?? 'no-cors',
  } as unknown as Request
}

function response(body: string, ok = true): Response {
  return {
    ok,
    body,
    clone: () => response(body, ok),
  } as unknown as Response
}

describe('fingerprint', () => {
  it('changes when the build does', () => {
    const before = fingerprint([{ url: '/assets/index-aaa.js', revision: null }])
    const after = fingerprint([{ url: '/assets/index-bbb.js', revision: null }])

    // Vite content-hashes the filenames, so this is what makes the cache roll
    // without anyone having to remember to bump a version constant.
    expect(before).not.toBe(after)
  })

  it('does not change when the build does not', () => {
    const entries = [{ url: '/index.html', revision: 'abc' }]

    expect(fingerprint(entries)).toBe(fingerprint([...entries]))
  })

  it('notices a changed revision on an unchanged url', () => {
    // index.html is not content-hashed, so its revision is the only signal.
    expect(fingerprint([{ url: '/index.html', revision: 'one' }])).not.toBe(
      fingerprint([{ url: '/index.html', revision: 'two' }]),
    )
  })
})

describe('staleCacheNames', () => {
  it('collects every previous build and leaves the current one', () => {
    const current = cacheName([{ url: '/index.html', revision: 'now' }])
    const old = cacheName([{ url: '/index.html', revision: 'then' }])

    expect(staleCacheNames([old, current], current)).toEqual([old])
  })

  it('leaves caches belonging to something else alone', () => {
    // The app is not the only thing that may have opened a cache on this origin.
    const current = cacheName([])

    expect(staleCacheNames(['msal.cache', current], current)).toEqual([])
  })
})

describe('navigationResponse', () => {
  it('prefers the network, because a terminal client is not offline-first', async () => {
    const cache = new FakeCache()
    cache.entries.set(ShellUrl, response('stale'))
    const fetcher = vi.fn(async () => response('fresh'))

    const result = await navigationResponse(request('/', { mode: 'navigate' }), cache.cache, fetcher)

    expect((result as unknown as { body: string }).body).toBe('fresh')
  })

  it('falls back to the cached shell when the network is gone', async () => {
    // The point of caching anything at all: opening the app in a lift shows the
    // app saying it has no connection, not the browser's error page.
    const cache = new FakeCache()
    cache.entries.set(ShellUrl, response('shell'))
    const fetcher = vi.fn(async () => {
      throw new Error('offline')
    })

    const result = await navigationResponse(request('/', { mode: 'navigate' }), cache.cache, fetcher)

    expect((result as unknown as { body: string }).body).toBe('shell')
  })

  it('reports the failure when there is nothing cached to fall back to', async () => {
    const fetcher = vi.fn(async () => {
      throw new Error('offline')
    })

    await expect(
      navigationResponse(request('/', { mode: 'navigate' }), new FakeCache().cache, fetcher),
    ).rejects.toThrow('offline')
  })

  it('does not cache a failed navigation', async () => {
    // Caching a 500 would turn one bad deploy into a permanently broken app on
    // that phone, reachable only by clearing website data.
    const cache = new FakeCache()
    const fetcher = vi.fn(async () => response('error page', false))

    await navigationResponse(request('/', { mode: 'navigate' }), cache.cache, fetcher)

    expect(cache.put).not.toHaveBeenCalled()
  })

  it('stores a good navigation under the shell url, not the requested one', async () => {
    // Any route has to be able to serve the same single-page shell back.
    const cache = new FakeCache()
    const fetcher = vi.fn(async () => response('fresh'))

    await navigationResponse(request('/machines', { mode: 'navigate' }), cache.cache, fetcher)

    expect(cache.put).toHaveBeenCalledWith(ShellUrl, expect.anything())
  })
})

describe('assetResponse', () => {
  it('serves a precached asset without touching the network', async () => {
    const cache = new FakeCache()
    cache.entries.set('/assets/index-aaa.js', response('cached'))
    const fetcher = vi.fn(async () => response('network'))

    const result = await assetResponse(
      request('https://app.example/assets/index-aaa.js'),
      cache.cache,
      fetcher,
    )

    expect((result as unknown as { body: string }).body).toBe('cached')
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('fetches anything the cache has never seen', async () => {
    const fetcher = vi.fn(async () => response('network'))

    const result = await assetResponse(
      request('https://app.example/assets/new.js'),
      new FakeCache().cache,
      fetcher,
    )

    expect((result as unknown as { body: string }).body).toBe('network')
  })
})

describe('shouldHandle', () => {
  it('leaves other origins alone', () => {
    // The hub is a websocket to another origin and the identity provider is a
    // redirect to a third. A worker that intercepts an auth redirect is a very
    // confusing thing to debug.
    expect(shouldHandle(request('https://hub.example/hub'), 'https://app.example')).toBe(false)
  })

  it('leaves non-GET requests alone', () => {
    expect(
      shouldHandle(request('https://app.example/api', { method: 'POST' }), 'https://app.example'),
    ).toBe(false)
  })

  it('handles same-origin GETs', () => {
    expect(shouldHandle(request('https://app.example/index.html'), 'https://app.example')).toBe(true)
  })
})
