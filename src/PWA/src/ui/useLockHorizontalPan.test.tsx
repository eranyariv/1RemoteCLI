import { cleanup, render } from '@testing-library/react'
import { useRef } from 'react'
import { afterEach, describe, expect, it } from 'vitest'

import { PAN_X, useLockHorizontalPan } from './useLockHorizontalPan'

/**
 * A session screen in miniature: a terminal area that must not move, and a key bar
 * that must still pan, inside the same locked root.
 */
function Screen() {
  const ref = useRef<HTMLDivElement | null>(null)
  useLockHorizontalPan(ref)

  return (
    <div ref={ref} data-testid="screen">
      <div data-testid="terminal">output</div>
      <div {...PAN_X} data-testid="keybar">
        <button type="button">Ctrl</button>
      </div>
    </div>
  )
}

function touch(target: Element, type: string, x: number, y: number) {
  const event = new Event(type, { bubbles: true, cancelable: true })
  Object.defineProperty(event, 'touches', {
    value: [{ clientX: x, clientY: y }],
  })
  target.dispatchEvent(event)
  return event
}

/** Drags from (0,0) to (dx,dy) and reports whether the pan was cancelled. */
function drag(target: Element, dx: number, dy: number) {
  touch(target, 'touchstart', 0, 0)
  const move = touch(target, 'touchmove', dx, dy)
  touch(target, 'touchend', dx, dy)
  return move.defaultPrevented
}

describe('useLockHorizontalPan', () => {
  afterEach(cleanup)

  it('cancels a sideways drag on the screen, which is what slides the header off the edge', () => {
    const { getByTestId } = render(<Screen />)

    expect(drag(getByTestId('terminal'), 60, 4)).toBe(true)
  })

  it('leaves vertical drags alone, so the terminal still scrolls', () => {
    const { getByTestId } = render(<Screen />)

    expect(drag(getByTestId('terminal'), 4, 60)).toBe(false)
  })

  it('lets the key bar pan sideways, because its keys run past the screen edge', () => {
    const { getByTestId } = render(<Screen />)

    expect(drag(getByTestId('keybar').querySelector('button')!, 60, 4)).toBe(false)
  })

  it('ignores a drag too small to be aimed, rather than fighting a tap that wobbled', () => {
    const { getByTestId } = render(<Screen />)

    expect(drag(getByTestId('terminal'), 3, 1)).toBe(false)
  })

  it('holds the verdict for the gesture, so a drag that turns a corner does not start fighting the finger', () => {
    const { getByTestId } = render(<Screen />)
    const terminal = getByTestId('terminal')

    touch(terminal, 'touchstart', 0, 0)
    // Committed to vertical first...
    expect(touch(terminal, 'touchmove', 0, 40).defaultPrevented).toBe(false)
    // ...so the sideways leg of the same drag stays allowed.
    expect(touch(terminal, 'touchmove', 80, 40).defaultPrevented).toBe(false)
  })
})
