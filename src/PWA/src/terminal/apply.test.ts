import { describe, expect, it } from 'vitest'

import { applyOutput, HISTORY_LIFETIME_MS, type Writable } from './apply'

/** Records what was done to it, in order, so ordering can be asserted and not just occurrence. */
function recorder(): Writable & { log: string[] } {
  const log: string[] = []

  return {
    log,
    reset: () => log.push('reset'),
    write: (data) => log.push(`write:${new TextDecoder().decode(data)}`),
  }
}

const bytes = (text: string) => new TextEncoder().encode(text)

const A_MINUTE = 60_000
const A_WEEK = 7 * HISTORY_LIFETIME_MS

describe('applyOutput', () => {
  it('writes a delta straight through', () => {
    const term = recorder()

    applyOutput(term, bytes('hello'), 'Delta')

    expect(term.log).toEqual(['write:hello'])
  })

  it('draws a snapshot without discarding the scrollback above it', () => {
    const term = recorder()

    applyOutput(term, bytes('fresh'), 'Snapshot')

    // The snapshot clears the screen itself, by erasing the display rather than by
    // resetting the terminal. Resetting here as well would take the scrollback with
    // it, which on a phone is the only copy of everything already scrolled off.
    expect(term.log).toEqual(['write:fresh'])
  })

  it('keeps the scrollback across the reconnections that actually happen', () => {
    const term = recorder()

    // A lift, a tunnel, a locked screen. Seconds, and the overwhelming majority of
    // the snapshots this client will ever apply.
    applyOutput(term, bytes('screen'), 'Snapshot', 4_000)
    applyOutput(term, bytes('screen'), 'Snapshot', A_MINUTE)

    expect(term.log).toEqual(['write:screen', 'write:screen'])
  })

  it('discards the scrollback once it is a day old', () => {
    const term = recorder()

    applyOutput(term, bytes('today'), 'Snapshot', HISTORY_LIFETIME_MS)

    // Past a day the history is not what the user is still reading, and keeping it
    // would silently join yesterday's screen onto today's.
    expect(term.log).toEqual(['reset', 'write:today'])
  })

  it('holds the line just under a day', () => {
    const term = recorder()

    applyOutput(term, bytes('still today'), 'Snapshot', HISTORY_LIFETIME_MS - 1)

    expect(term.log).toEqual(['write:still today'])
  })

  it('resets for a session picked up a week later', () => {
    const term = recorder()

    applyOutput(term, bytes('last week'), 'Snapshot', A_WEEK)

    expect(term.log).toEqual(['reset', 'write:last week'])
  })

  it('never resets for a delta, however long the gap', () => {
    const term = recorder()

    // A delta is a change to what is already there. Clearing first would delete the
    // screen it is describing a change to.
    applyOutput(term, bytes('more'), 'Delta', A_WEEK)

    expect(term.log).toEqual(['write:more'])
  })

  it('does not reset again for the continuation frames of one snapshot', () => {
    const term = recorder()

    // A snapshot too large for one message arrives as several frames. Only the first
    // carries the time away; a reset on the rest would wipe what it had just drawn.
    applyOutput(term, bytes('first half'), 'Snapshot', A_WEEK)
    applyOutput(term, bytes('second half'), 'Snapshot')

    expect(term.log).toEqual(['reset', 'write:first half', 'write:second half'])
  })

  it('keeps applying deltas after a snapshot without resetting again', () => {
    const term = recorder()

    applyOutput(term, bytes('screen'), 'Snapshot', A_WEEK)
    applyOutput(term, bytes('more'), 'Delta')

    expect(term.log).toEqual(['reset', 'write:screen', 'write:more'])
  })
})
