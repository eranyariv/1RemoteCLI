import { afterEach, describe, expect, it, vi } from 'vitest'

import { installTouchScroll } from './touchScroll'

const cleanups: Array<() => void> = []

afterEach(() => {
  cleanups.splice(0).forEach((cleanup) => cleanup())
})

function setup(viewportY = 90, baseY = 100) {
  const element = document.createElement('div')
  const terminalElement = document.createElement('div')
  element.appendChild(terminalElement)
  Object.defineProperty(element, 'clientHeight', { value: 200 })
  const diagnose = vi.fn()
  const terminal = {
    rows: 10,
    cols: 40,
    element: terminalElement,
    buffer: { active: { viewportY, baseY } },
  }
  cleanups.push(installTouchScroll(element, terminal, diagnose))
  return { element, terminalElement, diagnose }
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
    const { element, terminalElement, diagnose } = setup()
    const wheels: WheelEvent[] = []
    terminalElement.addEventListener('wheel', (event) => {
      wheels.push(event)
      event.preventDefault()
    })

    touch(element, 'touchstart', 20, 20)
    const move = touch(element, 'touchmove', 20, 80)

    expect(move.defaultPrevented).toBe(true)
    expect(wheels).toHaveLength(3)
    expect(wheels.every((event) => event.deltaY === -20)).toBe(true)
    expect(diagnose).toHaveBeenCalledWith(
      'scroll-request',
      expect.objectContaining({ lines: -3, viewportY: 90, baseY: 100 }),
    )
    expect(diagnose).toHaveBeenCalledWith(
      'wheel-result',
      expect.objectContaining({ lines: -3, wheelEvents: 3, handled: 3 }),
    )
  })

  it('scrolls toward newer output when the finger moves up', () => {
    const { element, terminalElement } = setup()
    const deltas: number[] = []
    terminalElement.addEventListener('wheel', (event) => deltas.push(event.deltaY))

    touch(element, 'touchstart', 20, 80)
    touch(element, 'touchmove', 20, 20)

    expect(deltas).toEqual([20, 20, 20])
  })

  it('uses wheel input even when xterm has no local history', () => {
    const { element, terminalElement } = setup(0, 0)
    const deltas: number[] = []
    terminalElement.addEventListener('wheel', (event) => deltas.push(event.deltaY))

    touch(element, 'touchstart', 20, 80)
    touch(element, 'touchmove', 20, 40)

    expect(deltas).toEqual([20, 20])
  })

  it('accumulates movements smaller than one terminal row', () => {
    const { element, terminalElement } = setup()
    const wheel = vi.fn()
    terminalElement.addEventListener('wheel', wheel)

    touch(element, 'touchstart', 20, 60)
    touch(element, 'touchmove', 20, 50)
    expect(wheel).not.toHaveBeenCalled()

    touch(element, 'touchmove', 20, 35)
    expect(wheel).toHaveBeenCalledOnce()
  })

  it('leaves horizontal and multi-touch gestures alone', () => {
    const { element, terminalElement } = setup()
    const wheel = vi.fn()
    terminalElement.addEventListener('wheel', wheel)

    touch(element, 'touchstart', 20, 20)
    const horizontal = touch(element, 'touchmove', 80, 24)
    touch(element, 'touchend', 80, 24, 0)
    touch(element, 'touchstart', 20, 20, 2)
    const multiTouch = touch(element, 'touchmove', 20, 80, 2)

    expect(horizontal.defaultPrevented).toBe(false)
    expect(multiTouch.defaultPrevented).toBe(false)
    expect(wheel).not.toHaveBeenCalled()
  })

  it('removes its event handlers when disposed', () => {
    const { element, terminalElement } = setup()
    const wheel = vi.fn()
    terminalElement.addEventListener('wheel', wheel)
    cleanups.splice(0).forEach((cleanup) => cleanup())

    touch(element, 'touchstart', 20, 20)
    touch(element, 'touchmove', 20, 80)

    expect(wheel).not.toHaveBeenCalled()
  })
})
