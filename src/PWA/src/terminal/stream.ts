/**
 * What to do with each output frame as it arrives.
 *
 * Delivery is at-least-once, so the same frame can arrive twice. Reattaching
 * registers the phone for live output at the hub before the agent has worked out
 * which frames it missed, and anything flushed inside that window is broadcast live
 * and then replayed. Writing it to the emulator a second time duplicates a chunk of
 * the terminal, which reads as the shell having done something strange rather than as
 * a bug.
 *
 * Kept apart from the React hook because the interesting part is the decision, and a
 * decision is far easier to be sure of when it can be tested one frame at a time.
 */

export type FrameKind = 'Delta' | 'Snapshot'

/** How far through the stream we are. */
export interface StreamPosition {
  /** Highest sequence applied, and what a reattach asks to resume from. */
  readonly applied: number | null

  /**
   * Whether the frame just applied was a snapshot.
   *
   * A snapshot too large for one message is split across several frames that all
   * carry the *same* sequence number — deliberately, so the watchers who did not ask
   * for it are not left with a gap they would report as lost output. So while a
   * snapshot is being applied, a repeated sequence number is a continuation to be
   * drawn, not a duplicate to be dropped, and the two are indistinguishable without
   * remembering this.
   */
  readonly continuingSnapshot: boolean
}

/** Nothing received yet. */
export const startOfStream: StreamPosition = {
  applied: null,
  continuingSnapshot: false,
}

export interface StreamStep {
  /** Whether to draw this frame. */
  readonly apply: boolean

  /** Whether output was produced that will never arrive. */
  readonly missed: boolean

  readonly position: StreamPosition
}

/**
 * Decides whether a frame is new, a continuation, or one already seen.
 *
 * The position only ever moves forward. Assigning the sequence unconditionally is
 * what turned a duplicate into a false alarm: a replayed frame arriving behind a live
 * one dragged the watermark backwards, and the next live frame then looked like a
 * jump and raised the warning that says the screen cannot be trusted. Firing that
 * warning when nothing was lost is worse than the duplicate it came from, because the
 * only cure for a user who has learned to ignore it is not showing it wrongly.
 */
export function receive(
  position: StreamPosition,
  frame: { seq: number; kind: FrameKind },
): StreamStep {
  const { applied, continuingSnapshot } = position

  // A repaint is always drawn. It can legitimately carry a sequence the client has
  // already seen -- resuming at a new size repaints without any new output having
  // been produced -- so it must not be mistaken for something already applied.
  if (frame.kind === 'Snapshot') {
    return {
      apply: true,
      missed: applied !== null && frame.seq > applied + 1,
      position: { applied: frame.seq, continuingSnapshot: true },
    }
  }

  if (continuingSnapshot && applied !== null && frame.seq === applied) {
    return { apply: true, missed: false, position }
  }

  if (applied !== null && frame.seq <= applied) {
    return { apply: false, missed: false, position }
  }

  return {
    apply: true,
    missed: applied !== null && frame.seq > applied + 1,
    position: { applied: frame.seq, continuingSnapshot: false },
  }
}
