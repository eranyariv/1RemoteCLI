import { describe, expect, it } from 'vitest'

import { needsOnScreenKeys } from './device'

function browser(options: {
  touchFirst?: boolean
  userAgent?: string
  maxTouchPoints?: number
  withoutMatchMedia?: boolean
}): Window {
  return {
    navigator: {
      userAgent: options.userAgent ?? 'desktop',
      maxTouchPoints: options.maxTouchPoints ?? 0,
    },
    matchMedia: options.withoutMatchMedia
      ? undefined
      : () => ({ matches: options.touchFirst ?? false }),
  } as unknown as Window
}

describe('on-screen terminal keys', () => {
  it('shows them when the browser reports touch-first input without hover', () => {
    expect(needsOnScreenKeys(browser({ touchFirst: true }))).toBe(true)
  })

  it('hides them when the browser reports desktop-style input', () => {
    expect(needsOnScreenKeys(browser({ touchFirst: false }))).toBe(false)
  })

  it('falls back to the mobile platform on older browsers', () => {
    expect(
      needsOnScreenKeys(
        browser({
          withoutMatchMedia: true,
          userAgent: 'Mozilla/5.0 (Linux; Android 14; Mobile)',
          maxTouchPoints: 5,
        }),
      ),
    ).toBe(true)
  })

  it('does not show them on an older desktop browser', () => {
    expect(needsOnScreenKeys(browser({ withoutMatchMedia: true }))).toBe(false)
  })
})
