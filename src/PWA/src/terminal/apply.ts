import type { TerminalOutputKind } from '../protocol/wire'

/**
 * The part of xterm's `Terminal` that applying output needs.
 *
 * Narrowed to two methods so the rule below can be tested without constructing a
 * real terminal, which needs a DOM, a canvas and a font. The rule is the thing worth
 * testing; xterm's own writing is not ours to verify.
 */
export interface Writable {
  reset(): void
  write(data: Uint8Array): void
}

/**
 * Applies one output frame.
 *
 * A delta is a change to the screen and is written as-is. A snapshot is the *whole*
 * screen and must replace what is there, because it only draws the cells that differ
 * from a blank terminal — everything the agent's screen leaves empty is simply not in
 * the stream. Written on top of stale content, those gaps would show the previous
 * session's text, indistinguishable from real output and impossible for the user to
 * catch. Resetting first is what makes a snapshot mean what it says.
 */
export function applyOutput(term: Writable, data: Uint8Array, kind: TerminalOutputKind): void {
  if (kind === 'Snapshot') term.reset()

  term.write(data)
}
