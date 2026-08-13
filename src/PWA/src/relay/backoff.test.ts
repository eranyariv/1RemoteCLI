import { describe, expect, it } from 'vitest'

import { ForeverRetryPolicy, MAX_DELAY_MS, reconnectDelay } from './backoff'

/**
 * The reconnect schedule.
 *
 * Worth testing on its own because the failure it guards against is invisible in
 * development: a policy that stops retrying looks identical to a working one until
 * someone walks into a tunnel.
 */
describe('reconnectDelay', () => {
  const noJitter = () => 0

  it('tries again immediately the first time', () => {
    // Most drops are a blip. Waiting a second before even looking would make every
    // one of them visible to the user for no reason.
    expect(reconnectDelay(0, noJitter)).toBe(0)
  })

  it('backs off geometrically', () => {
    expect(reconnectDelay(1, noJitter)).toBe(1_000)
    expect(reconnectDelay(2, noJitter)).toBe(2_000)
    expect(reconnectDelay(3, noJitter)).toBe(4_000)
    expect(reconnectDelay(4, noJitter)).toBe(8_000)
  })

  it('never waits longer than the cap, however long the outage', () => {
    // A person who has walked back into signal is watching the screen. Backing off
    // to minutes would save nothing that matters and cost all of their confidence.
    for (const attempt of [10, 50, 1_000, 100_000]) {
      expect(reconnectDelay(attempt, noJitter)).toBe(MAX_DELAY_MS)
    }
  })

  it('does not overflow at absurd attempt counts', () => {
    // 2 ** 2000 is Infinity. A tab left open for a week in a drawer really can get
    // here, and Infinity milliseconds is a timer that never fires.
    expect(Number.isFinite(reconnectDelay(2_000, noJitter))).toBe(true)
  })

  it('adds jitter, and only ever adds', () => {
    // Every phone attached to a hub that restarts would otherwise come back on the
    // same schedule and knock it over again.
    expect(reconnectDelay(3, () => 0.999)).toBeGreaterThan(reconnectDelay(3, noJitter))
    expect(reconnectDelay(3, () => 0)).toBe(4_000)
  })
})

describe('ForeverRetryPolicy', () => {
  it('never gives up', () => {
    // Returning null is how a policy tells SignalR to stop for good. The app would
    // then sit on "offline" until reloaded, with nothing on screen to say so.
    const policy = new ForeverRetryPolicy(() => 0)

    for (const previousRetryCount of [0, 1, 5, 20, 500]) {
      const delay = policy.nextRetryDelayInMilliseconds({
        previousRetryCount,
        elapsedMilliseconds: previousRetryCount * 1_000,
        retryReason: new Error('offline'),
      })

      expect(delay).not.toBeNull()
      expect(delay).toBeLessThanOrEqual(MAX_DELAY_MS + 1_000)
    }
  })
})
