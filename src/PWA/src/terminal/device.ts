import { detectPlatform } from '../install/standalone'

const TOUCH_FIRST_INPUT = '(hover: none) and (pointer: coarse)'

/**
 * Browsers do not expose whether a physical keyboard is connected. A coarse primary
 * pointer with no hover is their closest report of a touch-only device; platform
 * detection covers older mobile browsers that do not implement input media queries.
 */
export function needsOnScreenKeys(window: Window): boolean {
  if (typeof window.matchMedia === 'function') {
    return window.matchMedia(TOUCH_FIRST_INPUT).matches
  }

  const navigator = window.navigator
  return detectPlatform(navigator.userAgent ?? '', navigator.maxTouchPoints ?? 0) !== 'desktop'
}
