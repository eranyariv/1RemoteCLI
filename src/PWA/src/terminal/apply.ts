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
 * How long a session can be out of contact before its scrollback stops being worth
 * keeping.
 *
 * A day is chosen so that the answer is never "it depends": everything within one
 * working day of use keeps its history, and anything older is a session the user is
 * returning to rather than continuing. The exact figure matters less than it being
 * far longer than the disconnections that actually happen, which are measured in
 * seconds — a lift, a tunnel, a locked screen.
 */
export const HISTORY_LIFETIME_MS = 24 * 60 * 60 * 1000

/**
 * Applies one output frame.
 *
 * A delta is a change to the screen and is written as-is. A snapshot is the *whole*
 * screen and must replace what is there, because it only draws the cells that differ
 * from a blank terminal — everything the agent's screen leaves empty is simply not in
 * the stream. Written on top of stale content, those gaps would show the previous
 * session's text, indistinguishable from real output and impossible for the user to
 * catch.
 *
 * Clearing it is the snapshot's own job. It opens by putting the terminal into
 * power-on state — see `VtSnapshotWriter` — and does that by erasing the display
 * rather than by resetting the terminal, so the rows above the screen survive. That
 * matters more than it sounds: the agent keeps no scrollback at all, so lines that
 * have scrolled off exist only in the emulator the user is looking at, and a reset
 * here was throwing away every one of them on a reconnection that lasted a second.
 *
 * `awayMs` is how long the client was out of contact before this frame. Past a day
 * the scrollback is no longer history the user is still reading, and keeping it would
 * silently join yesterday's screen onto today's; that is the one case worth the full
 * reset.
 */
export function applyOutput(
  term: Writable,
  data: Uint8Array,
  kind: TerminalOutputKind,
  awayMs = 0,
): void {
  if (kind === 'Snapshot' && awayMs >= HISTORY_LIFETIME_MS) term.reset()

  term.write(data)
}
