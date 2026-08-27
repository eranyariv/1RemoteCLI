import { useEffect } from 'react'
import type { RefObject } from 'react'

/**
 * Marks a subtree that is allowed to pan sideways. Put it on the element that
 * actually carries `overflow-x`, not on a wrapper.
 */
export const PAN_X = { 'data-pan-x': '' } as const

/**
 * Pins a session screen horizontally.
 *
 * `overflow-x: hidden` up the whole ancestor chain is already in place and still
 * is not enough: iOS rubber-bands the fixed session layer sideways even when its
 * scroll width is correct, which drags the header off the left edge.
 *
 * The obvious fix — `touch-action: pan-y` on the root — cannot be used here. The
 * effective touch-action of a gesture is intersected down the ancestor chain, so a
 * `pan-y` root also vetoes the two things inside a session that pan sideways on
 * purpose: the on-screen key bar and ACP code blocks. Cancelling the gesture only
 * when it starts outside those is the narrowest rule that keeps both.
 *
 * The listener must be non-passive; a passive one cannot preventDefault.
 */
export function useLockHorizontalPan(ref: RefObject<HTMLElement | null>) {
  useEffect(() => {
    const root = ref.current
    if (!root) return

    let startX = 0
    let startY = 0
    // Resolved on the first move that is unambiguous, then held for the rest of
    // the gesture: a drag that turns a corner must not change its mind halfway
    // and start fighting the finger.
    let decision: 'undecided' | 'allow' | 'block' = 'undecided'

    const onStart = (event: TouchEvent) => {
      // Leave pinch-zoom alone.
      if (event.touches.length !== 1) {
        decision = 'allow'
        return
      }

      const touch = event.touches[0]
      startX = touch.clientX
      startY = touch.clientY

      const target = event.target
      const inPannable =
        target instanceof Element && target.closest('[data-pan-x]') !== null

      decision = inPannable ? 'allow' : 'undecided'
    }

    const onMove = (event: TouchEvent) => {
      if (decision === 'allow') return
      if (event.touches.length !== 1) return

      const touch = event.touches[0]
      const dx = Math.abs(touch.clientX - startX)
      const dy = Math.abs(touch.clientY - startY)

      if (decision === 'undecided') {
        // Below this the direction is noise, not intent.
        if (dx < 8 && dy < 8) return
        decision = dx > dy ? 'block' : 'allow'
      }

      if (decision === 'block' && event.cancelable) event.preventDefault()
    }

    const reset = () => {
      decision = 'undecided'
    }

    root.addEventListener('touchstart', onStart, { passive: true })
    root.addEventListener('touchmove', onMove, { passive: false })
    root.addEventListener('touchend', reset, { passive: true })
    root.addEventListener('touchcancel', reset, { passive: true })

    return () => {
      root.removeEventListener('touchstart', onStart)
      root.removeEventListener('touchmove', onMove)
      root.removeEventListener('touchend', reset)
      root.removeEventListener('touchcancel', reset)
    }
  }, [ref])
}
