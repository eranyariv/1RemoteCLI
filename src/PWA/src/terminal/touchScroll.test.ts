import { afterEach, describe, expect, it, vi } from 'vitest'

import { installTouchScroll } from './touchScroll'

const cleanups: Array<() => void> = []

afterEach(() => {
  cleanups.splice(0).forEach((cleanup) => cleanup())
})

function setup() {
  const element = document.createElement('div')
  Object.defineProperty(element, 'clientHeight', { value: 200 })
  const scrollLines = vi.fn()
  cleanups.push(installTouchScroll(element, { rows: 10, scrollLines }))
  return { element, scrollLines }
}

function touch(
  element: HTMLElement,
  type: 'touchstart' | 'touchmove' | 'touchend',
  x: number,
  y: number,
  count = 1,
) {
  const event = new Event(type, { bubbles: true, cancelable: true })
  const touches = Array.from({ length: count }, () => ({ clientX: x, clientY: y }))
  Object.defineProperty(event, 'touches', { value: touches })
  element.dispatchEvent(event)
  return event
}

describe('installTouchScroll', () => {
  it('scrolls toward older output when the finger moves down', () => {
    const { element, scrollLines } = setup()

    touch(element, 'touchstart', 20, 20)
    const move = touch(element, 'touchmove', 20, 80)

    expect(move.defaultPrevented).toBe(true)
    expect(scrollLines).toHaveBeenCalledWith(-3)
  })

  it('scrolls toward newer output when the finger moves up', () => {
    const { element, scrollLines } = setup()

    touch(element, 'touchstart', 20, 80)
    touch(element, 'touchmove', 20, 20)

    expect(scrollLines).toHaveBeenCalledWith(3)
  })

  it('accumulates movements smaller than one terminal row', () => {
    const { element, scrollLines } = setup()

    touch(element, 'touchstart', 20, 60)
    touch(element, 'touchmove', 20, 50)
    expect(scrollLines).not.toHaveBeenCalled()

    touch(element, 'touchmove', 20, 35)
    expect(scrollLines).toHaveBeenCalledWith(1)
  })

  it('leaves horizontal and multi-touch gestures alone', () => {
    const { element, scrollLines } = setup()

    touch(element, 'touchstart', 20, 20)
    const horizontal = touch(element, 'touchmove', 80, 24)
    touch(element, 'touchend', 80, 24, 0)
    touch(element, 'touchstart', 20, 20, 2)
    const multiTouch = touch(element, 'touchmove', 20, 80, 2)

    expect(horizontal.defaultPrevented).toBe(false)
    expect(multiTouch.defaultPrevented).toBe(false)
    expect(scrollLines).not.toHaveBeenCalled()
  })

  it('removes its event handlers when disposed', () => {
    const { element, scrollLines } = setup()
    cleanups.splice(0).forEach((cleanup) => cleanup())

    touch(element, 'touchstart', 20, 20)
    touch(element, 'touchmove', 20, 80)

    expect(scrollLines).not.toHaveBeenCalled()
  })
})
