const DIRECTION_THRESHOLD_PX = 8

type TouchScrollTerminal = Pick<
  import('@xterm/xterm').Terminal,
  'rows' | 'scrollLines'
>

/**
 * Restores one-finger scrollback navigation removed by xterm 6.0.0.
 *
 * This can go away once xterm's fix for xtermjs/xterm.js#5489 is released.
 */
export function installTouchScroll(
  element: HTMLElement,
  terminal: TouchScrollTerminal,
): () => void {
  let startX = 0
  let startY = 0
  let lastY = 0
  let remainder = 0
  let direction: 'undecided' | 'horizontal' | 'vertical' | null = null

  const reset = () => {
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
  }

  const onMove = (event: TouchEvent) => {
    if (direction === null || event.touches.length !== 1) return

    const touch = event.touches[0]
    if (direction === 'undecided') {
      const dx = Math.abs(touch.clientX - startX)
      const dy = Math.abs(touch.clientY - startY)
      if (dx < DIRECTION_THRESHOLD_PX && dy < DIRECTION_THRESHOLD_PX) return
      direction = dy > dx ? 'vertical' : 'horizontal'
    }

    if (direction !== 'vertical') return
    if (event.cancelable) event.preventDefault()

    const rowHeight = element.clientHeight / terminal.rows
    if (!(rowHeight > 0)) {
      lastY = touch.clientY
      return
    }

    remainder += lastY - touch.clientY
    lastY = touch.clientY

    const lines = remainder > 0
      ? Math.floor(remainder / rowHeight)
      : Math.ceil(remainder / rowHeight)
    if (lines === 0) return

    terminal.scrollLines(lines)
    remainder -= lines * rowHeight
  }

  element.addEventListener('touchstart', onStart, { passive: true })
  element.addEventListener('touchmove', onMove, { passive: false })
  element.addEventListener('touchend', reset, { passive: true })
  element.addEventListener('touchcancel', reset, { passive: true })

  return () => {
    element.removeEventListener('touchstart', onStart)
    element.removeEventListener('touchmove', onMove)
    element.removeEventListener('touchend', reset)
    element.removeEventListener('touchcancel', reset)
  }
}
