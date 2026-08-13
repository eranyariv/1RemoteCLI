import { describe, expect, it } from 'vitest'

import { applyOutput, type Writable } from './apply'

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

describe('applyOutput', () => {
  it('writes a delta straight through', () => {
    const term = recorder()

    applyOutput(term, bytes('hello'), 'Delta')

    expect(term.log).toEqual(['write:hello'])
  })

  it('clears the screen before drawing a snapshot', () => {
    const term = recorder()

    applyOutput(term, bytes('fresh'), 'Snapshot')

    // Order is the assertion. Resetting after the write would blank the very screen
    // the snapshot just drew.
    expect(term.log).toEqual(['reset', 'write:fresh'])
  })

  it('does not leave the previous screen underneath a snapshot', () => {
    const term = recorder()

    applyOutput(term, bytes('old session'), 'Delta')
    applyOutput(term, bytes('new session'), 'Snapshot')

    expect(term.log).toEqual(['write:old session', 'reset', 'write:new session'])
  })

  it('keeps applying deltas after a snapshot without resetting again', () => {
    const term = recorder()

    applyOutput(term, bytes('screen'), 'Snapshot')
    applyOutput(term, bytes('more'), 'Delta')

    // A reset here would discard the snapshot the client just rendered and leave the
    // user looking at a single line of follow-up output on an empty screen.
    expect(term.log).toEqual(['reset', 'write:screen', 'write:more'])
  })
})
