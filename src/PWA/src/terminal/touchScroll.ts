const DIRECTION_THRESHOLD_PX = 8

interface TouchScrollTerminal {
  readonly cols: number
  readonly rows: number
  readonly element: HTMLElement | undefined
  readonly buffer: {
    readonly active: {
      readonly viewportY: number
      readonly baseY: number
    }
  }
}

type DiagnosticValue = string | number | boolean | null
export type TouchScrollDiagnostic = (
  event: string,
  details: Record<string, DiagnosticValue>,
) => void

function targetName(target: EventTarget | null): string {
  if (!(target instanceof Element)) return 'unknown'
  const classes = [...target.classList].slice(0, 4).join('.')
  return `${target.tagName.toLowerCase()}${classes ? `.${classes}` : ''}`
}

/**
 * Restores one-finger terminal scrolling removed by xterm 6.0.0.
 *
 * Feeding xterm wheel events rather than calling `scrollLines` is load-bearing:
 * xterm scrolls its local buffer when one exists, but when a full-screen CLI has
 * no local history it translates wheel input into the Up/Down or mouse sequences
 * that the program uses for its own history. That is also what the desktop wheel
 * does. This adapter can go away once xtermjs/xterm.js#5489 ships.
 */
export function installTouchScroll(
  element: HTMLElement,
  terminal: TouchScrollTerminal,
  diagnose: TouchScrollDiagnostic = () => {},
): () => void {
  let startX = 0
  let startY = 0
  let lastY = 0
  let remainder = 0
  let direction: 'undecided' | 'horizontal' | 'vertical' | null = null

  const position = () => ({
    viewportY: terminal.buffer.active.viewportY,
    baseY: terminal.buffer.active.baseY,
  })

  const reset = (event?: Event) => {
    if (event) {
      diagnose(event.type, {
        direction,
        defaultPrevented: event.defaultPrevented,
        ...position(),
      })
    }
    direction = null
    remainder = 0
  }

  const onStart = (event: TouchEvent) => {
    if (event.touches.length !== 1) {
      reset()
      return
    }

    const touch = event.touches[0]
    startX = touch.clientX
    startY = touch.clientY
    lastY = touch.clientY
    remainder = 0
    direction = 'undecided'

    const rect = element.getBoundingClientRect()
    const style = getComputedStyle(element)
    diagnose('touchstart', {
      x: touch.clientX,
      y: touch.clientY,
      touches: event.touches.length,
      target: targetName(event.target),
      cancelable: event.cancelable,
      defaultPrevented: event.defaultPrevented,
      clientHeight: element.clientHeight,
      rectHeight: Math.round(rect.height),
      rectWidth: Math.round(rect.width),
      touchAction: style.touchAction || '',
      overflowY: style.overflowY || '',
      rows: terminal.rows,
      cols: terminal.cols,
      ...position(),
    })
  }

  const onMove = (event: TouchEvent) => {
    if (direction === null || event.touches.length !== 1) return

    const touch = event.touches[0]
    const dx = Math.abs(touch.clientX - startX)
    const dy = Math.abs(touch.clientY - startY)
    if (direction === 'undecided') {
      if (dx < DIRECTION_THRESHOLD_PX && dy < DIRECTION_THRESHOLD_PX) {
        diagnose('touchmove-below-threshold', {
          dx,
          dy,
          defaultPrevented: event.defaultPrevented,
          ...position(),
        })
        return
      }
      direction = dy > dx ? 'vertical' : 'horizontal'
      diagnose('direction-decided', { direction, dx, dy, ...position() })
    }

    if (direction !== 'vertical') {
      diagnose('touchmove-ignored', {
        direction,
        dx,
        dy,
        defaultPrevented: event.defaultPrevented,
        ...position(),
      })
      return
    }

    const defaultPreventedBefore = event.defaultPrevented
    if (event.cancelable) event.preventDefault()

    const rowHeight = element.clientHeight / terminal.rows
    if (!(rowHeight > 0)) {
      diagnose('invalid-row-height', {
        clientHeight: element.clientHeight,
        rows: terminal.rows,
        rowHeight,
        ...position(),
      })
      lastY = touch.clientY
      return
    }

    remainder += lastY - touch.clientY
    lastY = touch.clientY

    const lines = remainder > 0
      ? Math.floor(remainder / rowHeight)
      : Math.ceil(remainder / rowHeight)
    const before = position()

    diagnose('scroll-request', {
      x: touch.clientX,
      y: touch.clientY,
      dx,
      dy,
      rowHeight: Math.round(rowHeight * 100) / 100,
      remainder: Math.round(remainder * 100) / 100,
      lines,
      cancelable: event.cancelable,
      defaultPreventedBefore,
      defaultPreventedAfter: event.defaultPrevented,
      ...before,
    })
    if (lines === 0) return

    const wheelTarget = terminal.element
    if (!wheelTarget) {
      diagnose('wheel-target-missing', {
        lines,
        ...position(),
      })
      return
    }

    let handled = 0
    const wheelDelta = Math.sign(lines) * rowHeight
    for (let i = 0; i < Math.abs(lines); i += 1) {
      const wheel = new WheelEvent('wheel', {
        bubbles: true,
        cancelable: true,
        clientX: touch.clientX,
        clientY: touch.clientY,
        deltaMode: 0,
        deltaY: wheelDelta,
      })
      wheelTarget.dispatchEvent(wheel)
      if (wheel.defaultPrevented) handled += 1
    }
    remainder -= lines * rowHeight

    diagnose('wheel-result', {
      lines,
      wheelEvents: Math.abs(lines),
      wheelDelta: Math.round(wheelDelta * 100) / 100,
      handled,
      remainder: Math.round(remainder * 100) / 100,
      viewportYBefore: before.viewportY,
      baseYBefore: before.baseY,
      ...position(),
    })
    queueMicrotask(() => {
      diagnose('wheel-settled', {
        lines,
        ...position(),
      })
    })
  }

  const onPointer = (event: PointerEvent) => {
    if (event.pointerType !== 'touch') return
    diagnose(event.type, {
      x: event.clientX,
      y: event.clientY,
      cancelable: event.cancelable,
      defaultPrevented: event.defaultPrevented,
      target: targetName(event.target),
      ...position(),
    })
  }

  element.addEventListener('touchstart', onStart, { capture: true, passive: true })
  element.addEventListener('touchmove', onMove, { capture: true, passive: false })
  element.addEventListener('touchend', reset, { capture: true, passive: true })
  element.addEventListener('touchcancel', reset, { capture: true, passive: true })
  element.addEventListener('pointerdown', onPointer, { capture: true, passive: true })
  element.addEventListener('pointermove', onPointer, { capture: true, passive: true })
  element.addEventListener('pointerup', onPointer, { capture: true, passive: true })
  element.addEventListener('pointercancel', onPointer, { capture: true, passive: true })

  return () => {
    element.removeEventListener('touchstart', onStart, true)
    element.removeEventListener('touchmove', onMove, true)
    element.removeEventListener('touchend', reset, true)
    element.removeEventListener('touchcancel', reset, true)
    element.removeEventListener('pointerdown', onPointer, true)
    element.removeEventListener('pointermove', onPointer, true)
    element.removeEventListener('pointerup', onPointer, true)
    element.removeEventListener('pointercancel', onPointer, true)
  }
}
