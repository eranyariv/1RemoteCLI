import { afterEach, describe, expect, it, vi } from 'vitest'

import { DEFAULT_HUB, resolveHubUrl } from './endpoint'

describe('resolving the hub address', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  /**
   * The hub serves this app, so the hub is whoever served it. Compiling one hostname
   * in instead meant the app could not reach the hub from any other domain, and
   * failed as an opaque `TypeError: Load failed` well after sign-in had succeeded.
   */
  it('dials the origin it was served from when nothing is configured', () => {
    expect(resolveHubUrl('')).toBe(`${window.location.origin}/hub`)
  })

  it('appends the hub path to a base address', () => {
    expect(resolveHubUrl('http://localhost:5199')).toBe('http://localhost:5199/hub')
    expect(resolveHubUrl('http://localhost:5199/')).toBe('http://localhost:5199/hub')
  })

  it('leaves an address that already names the hub alone', () => {
    // Both forms are things a person reasonably types, and doubling the path
    // would produce a 404 that looks like the hub being down.
    expect(resolveHubUrl('http://localhost:5199/hub')).toBe('http://localhost:5199/hub')
  })

  it('keeps a base path that is not the hub', () => {
    expect(resolveHubUrl('https://relay.example.com/1remote')).toBe(
      'https://relay.example.com/1remote/hub',
    )
  })

  it('falls back to the serving origin rather than breaking on a mistyped override', () => {
    expect(resolveHubUrl('not a url')).toBe(`${window.location.origin}/hub`)
  })

  /** Unit tests and anything else without a page. A browser never gets here. */
  it('uses the compiled default only when there is no page to ask', () => {
    vi.stubGlobal('window', undefined)

    expect(resolveHubUrl('')).toBe(`${DEFAULT_HUB}/hub`)
  })
})
