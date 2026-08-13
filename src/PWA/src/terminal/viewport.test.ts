import { describe, expect, it } from 'vitest'

import { measureViewport, sameViewport, viewportStyle } from './viewport'

/**
 * The bug these guard against does not reproduce on a desktop browser, which is
 * precisely why they are here. On iOS the layout viewport does not shrink when the
 * keyboard opens — the page is scrolled inside a smaller visual viewport — so a
 * full-height fixed element keeps its height and hides its own bottom behind the
 * keyboard. That bottom is the accessory bar and the last lines of output: the two
 * things somebody opened the app to reach.
 */
describe('measuring the visible area', () => {
  it('reads the visual viewport', () => {
    const box = measureViewport({ height: 420, offsetTop: 96 } as VisualViewport)
    expect(box).toEqual({ height: 420, offsetTop: 96 })
  })

  it('reads nothing when the browser has no visual viewport', () => {
    // Null rather than a fallback to innerHeight. A browser without this API is one
    // whose layout viewport is already correct, so the right move is to leave the
    // CSS alone rather than pin a height that will then respond to nothing.
    expect(measureViewport(undefined)).toBeNull()
    expect(measureViewport(null)).toBeNull()
  })

  it('reads nothing while the viewport has no height', () => {
    // Happens mid-rotation on iOS. Sizing to it collapses the terminal to nothing.
    expect(measureViewport({ height: 0, offsetTop: 0 } as VisualViewport)).toBeNull()
  })
})

describe('pinning to the visible area', () => {
  it('sets an explicit height and slides down by the scroll', () => {
    expect(viewportStyle({ height: 420, offsetTop: 96 })).toEqual({
      height: '420px',
      transform: 'translateY(96px)',
    })
  })

  it('sets nothing at all when there is nothing to pin to', () => {
    // Not `{ height: undefined }`: React would write that over a height the
    // stylesheet had set, which is how you get a zero-height terminal on a
    // browser that was working fine.
    expect(viewportStyle(null)).toEqual({})
  })
})

describe('noticing that the visible area changed', () => {
  it('ignores sub-pixel drift', () => {
    // iOS reports fractional heights that wobble by hundredths through the whole
    // keyboard animation. Treating those as changes re-renders the terminal on
    // every frame of it.
    expect(sameViewport({ height: 420.02, offsetTop: 0 }, { height: 419.98, offsetTop: 0 })).toBe(
      true,
    )
  })

  it('notices a real change', () => {
    expect(sameViewport({ height: 420, offsetTop: 0 }, { height: 300, offsetTop: 0 })).toBe(false)
    expect(sameViewport({ height: 420, offsetTop: 0 }, { height: 420, offsetTop: 96 })).toBe(false)
  })

  it('treats absent and present as different', () => {
    expect(sameViewport(null, null)).toBe(true)
    expect(sameViewport(null, { height: 420, offsetTop: 0 })).toBe(false)
  })
})
