import { describe, expect, it } from 'vitest'

import { EMPTY_STATS, Sampler, verdict } from './latency'

/**
 * A fake clock, because the thing under test is a measurement of time. Using the
 * real one would make these tests both slow and flaky, and would test the machine's
 * scheduler rather than the sampler's arithmetic.
 */
function clock() {
  let t = 1_000
  return {
    now: () => t,
    advance(ms: number) {
      t += ms
    },
  }
}

describe('Sampler', () => {
  it('reports nothing before any keystroke has been answered', () => {
    expect(new Sampler().stats()).toEqual(EMPTY_STATS)
  })

  it('measures the gap between a keystroke and the output that follows it', () => {
    const c = clock()
    const sampler = new Sampler(c.now)

    sampler.keystroke()
    c.advance(45)
    sampler.output()

    expect(sampler.stats()).toMatchObject({ count: 1, p50: 45, worst: 45 })
  })

  it('resolves only the first keystroke of a burst', () => {
    // A fast typist produces several keystrokes before the first echo returns.
    // Resolving all of them against that one echo would report the later ones as
    // near-zero and quietly halve the median.
    const c = clock()
    const sampler = new Sampler(c.now)

    sampler.keystroke()
    c.advance(10)
    sampler.keystroke()
    c.advance(10)
    sampler.keystroke()
    c.advance(30)
    sampler.output()

    expect(sampler.stats().count).toBe(1)
    expect(sampler.stats().p50).toBe(50)
  })

  it('ignores keystrokes typed into a program that is already printing', () => {
    // Typing during `npm install` would otherwise measure the interval to the next
    // progress tick, which has nothing to do with the link.
    const c = clock()
    const sampler = new Sampler(c.now)

    sampler.output()
    c.advance(5)
    sampler.keystroke()
    c.advance(20)
    sampler.output()

    expect(sampler.stats().count).toBe(0)
  })

  it('starts sampling again once the output has settled', () => {
    const c = clock()
    const sampler = new Sampler(c.now)

    sampler.output()
    c.advance(500)
    sampler.keystroke()
    c.advance(30)
    sampler.output()

    expect(sampler.stats().count).toBe(1)
  })

  it('counts an echo that never arrives as lost rather than as a slow sample', () => {
    // A two-second "latency" is not latency; it is a dropped keystroke, and
    // averaging it in would make a broken link look merely sluggish.
    const c = clock()
    const sampler = new Sampler(c.now)

    sampler.keystroke()
    c.advance(5_000)
    sampler.output()

    expect(sampler.stats()).toMatchObject({ count: 0, lost: 1 })
  })

  it('computes percentiles by nearest rank', () => {
    const c = clock()
    const sampler = new Sampler(c.now)

    for (const ms of [10, 20, 30, 40, 100]) {
      c.advance(1_000)
      sampler.keystroke()
      c.advance(ms)
      sampler.output()
    }

    const stats = sampler.stats()
    expect(stats.count).toBe(5)
    expect(stats.p50).toBe(30)
    expect(stats.p95).toBe(100)
    expect(stats.worst).toBe(100)
  })

  it('keeps only the most recent window of samples', () => {
    // The number describes the link the user is on now. A lifetime average mostly
    // describes the wifi they were on an hour ago.
    const c = clock()
    const sampler = new Sampler(c.now, 3)

    for (const ms of [10, 10, 10, 90, 90, 90]) {
      c.advance(1_000)
      sampler.keystroke()
      c.advance(ms)
      sampler.output()
    }

    expect(sampler.stats()).toMatchObject({ count: 3, p50: 90 })
  })

  it('drops a pending sample when the attachment goes away', () => {
    const c = clock()
    const sampler = new Sampler(c.now)

    sampler.keystroke()
    sampler.discardPending()
    c.advance(30)
    sampler.output()

    expect(sampler.stats().count).toBe(0)
  })
})

describe('verdict', () => {
  it('says nothing when there is nothing to say', () => {
    expect(verdict(null)).toBe('unknown')
  })

  it('treats the spec target as the boundary of good', () => {
    expect(verdict(60)).toBe('good')
    expect(verdict(61)).toBe('fair')
  })

  it('calls anything past the point of feeling remote poor', () => {
    expect(verdict(150)).toBe('fair')
    expect(verdict(151)).toBe('poor')
  })
})
