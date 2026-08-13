import { describe, expect, it } from 'vitest'

import { DEFAULT_HUB, resolveHubUrl } from './endpoint'

describe('resolving the hub address', () => {
  it('falls back to the deployed hub when nothing is configured', () => {
    expect(resolveHubUrl('')).toBe(`${DEFAULT_HUB}/hub`)
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

  it('falls back rather than breaking on a mistyped override', () => {
    expect(resolveHubUrl('not a url')).toBe(`${DEFAULT_HUB}/hub`)
  })
})
