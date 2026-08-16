import { describe, expect, it } from 'vitest'

import { receive, startOfStream, type FrameKind, type StreamPosition } from './stream'

/** Feeds a run of frames through and reports what was drawn and what was flagged. */
function play(frames: readonly { seq: number; kind: FrameKind }[]): {
  applied: number[]
  missed: boolean
  position: StreamPosition
} {
  let position = startOfStream
  const applied: number[] = []
  let missed = false

  for (const frame of frames) {
    const step = receive(position, frame)
    if (step.apply) applied.push(frame.seq)
    if (step.missed) missed = true
    position = step.position
  }

  return { applied, missed, position }
}

const delta = (seq: number) => ({ seq, kind: 'Delta' as const })
const snapshot = (seq: number) => ({ seq, kind: 'Snapshot' as const })

describe('the output stream', () => {
  it('draws consecutive output once each', () => {
    const { applied, missed } = play([delta(1), delta(2), delta(3)])

    expect(applied).toEqual([1, 2, 3])
    expect(missed).toBe(false)
  })

  /**
   * The bug this exists for. Reattaching registers the phone for live output before
   * the agent decides what it missed, so a frame flushed in between arrives twice.
   */
  it('ignores a frame it has already drawn', () => {
    const { applied, missed } = play([delta(1), delta(2), delta(2), delta(3)])

    expect(applied).toEqual([1, 2, 3])
    expect(missed).toBe(false)
  })

  /**
   * The duplicate's nastier half: the watermark used to be assigned unconditionally,
   * so a replayed frame dragged it backwards and made the next live frame look like a
   * jump. The warning that says the screen cannot be trusted then fired over output
   * that was never lost.
   */
  it('does not claim output was missed when a frame is merely repeated', () => {
    const { applied, missed, position } = play([delta(7), delta(8), delta(7), delta(9)])

    expect(applied).toEqual([7, 8, 9])
    expect(missed).toBe(false)
    expect(position.applied).toBe(9)
  })

  it('still reports a genuine gap', () => {
    const { applied, missed } = play([delta(1), delta(5)])

    expect(applied).toEqual([1, 5])
    expect(missed).toBe(true)
  })

  /**
   * A snapshot bigger than one message is split into frames that all carry the same
   * sequence number, so dropping repeats blindly would leave half a screen painted.
   */
  it('draws every frame of a split snapshot', () => {
    const { applied, missed } = play([delta(4), snapshot(9), delta(9), delta(9)])

    expect(applied).toEqual([4, 9, 9, 9])
    expect(missed).toBe(true) // the jump from 4 to 9 is real
  })

  /** Resuming at a new size repaints without any new output having been produced. */
  it('draws a repaint that carries a sequence already seen', () => {
    const { applied } = play([delta(6), snapshot(6)])

    expect(applied).toEqual([6, 6])
  })

  it('goes back to discarding repeats once the snapshot is over', () => {
    const { applied } = play([snapshot(3), delta(3), delta(4), delta(4)])

    expect(applied).toEqual([3, 3, 4])
  })

  it('draws the first frame whatever its sequence', () => {
    const { applied, missed } = play([delta(1200)])

    expect(applied).toEqual([1200])
    expect(missed).toBe(false)
  })

  it('never moves the position backwards', () => {
    const { position } = play([delta(10), delta(4), delta(5)])

    expect(position.applied).toBe(10)
  })
})
