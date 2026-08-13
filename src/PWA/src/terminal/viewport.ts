/**
 * What the browser is actually showing, once the software keyboard has taken its
 * half of the screen.
 *
 * A phone changes shape constantly and, on iOS in particular, it changes shape in a
 * way CSS does not see. When the keyboard opens, the *layout* viewport — what `100vh`
 * and `position: fixed` are measured against — does not change at all. The page is
 * simply scrolled up inside a smaller *visual* viewport, so a full-height fixed
 * element keeps its full height and the bottom of it, which is where the accessory
 * bar and the last lines of output live, ends up underneath the keyboard.
 *
 * The fix is to stop trusting the layout viewport: measure the visual one and size
 * the terminal to that, translating it down by however far the page has been scrolled
 * within it. This is why the terminal view sets an explicit pixel height rather than
 * `inset-0`, which would be the obvious thing and is wrong on exactly the device this
 * product is for.
 */

export interface ViewportBox {
  /** The height actually visible, in CSS pixels. */
  height: number
  /** How far the visual viewport has been scrolled inside the layout viewport. */
  offsetTop: number
}

/**
 * Reads the visual viewport, or null when there is nothing trustworthy to read.
 *
 * Null rather than a fallback to `innerHeight`: a browser without `visualViewport`
 * is one where the layout viewport is already correct, so the caller should leave
 * the CSS alone rather than pin a height that will then not respond to anything.
 * A zero height happens mid-rotation on iOS, and sizing to it collapses the
 * terminal to nothing.
 */
export function measureViewport(viewport: VisualViewport | null | undefined): ViewportBox | null {
  if (!viewport) return null
  if (!(viewport.height > 0)) return null

  return { height: viewport.height, offsetTop: viewport.offsetTop }
}

/**
 * The inline style that pins an element to the visible area.
 *
 * Empty when there is nothing to pin to, so the element keeps whatever the
 * stylesheet said. An empty object is deliberate: writing `height: undefined` into
 * React's style prop would clear a height the CSS had set.
 */
export function viewportStyle(box: ViewportBox | null): { height?: string; transform?: string } {
  if (!box) return {}

  return {
    height: `${box.height}px`,
    transform: `translateY(${box.offsetTop}px)`,
  }
}

/** True when the two boxes describe the same visible area, to the nearest pixel. */
export function sameViewport(a: ViewportBox | null, b: ViewportBox | null): boolean {
  if (a === null || b === null) return a === b

  // Rounded, because iOS reports fractional heights that drift by hundredths
  // during the keyboard animation. Treating those as changes would re-render the
  // terminal on every frame of it.
  return Math.round(a.height) === Math.round(b.height)
    && Math.round(a.offsetTop) === Math.round(b.offsetTop)
}
