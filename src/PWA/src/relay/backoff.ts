import type { IRetryPolicy, RetryContext } from '@microsoft/signalr'

/**
 * How long to wait before the next attempt, and when to give up.
 *
 * The answer to "when to give up" is never. A phone spends its life moving
 * between Wi-Fi, cellular and nothing at all, and the tab may be suspended in a
 * pocket for hours. A policy that stops after a fixed number of tries turns a
 * tunnel into a dead app that only a manual reload will fix — and the user has no
 * way to know that a reload is what is needed, because the screen still shows a
 * terminal.
 */

/** First wait. Effectively "try again now", for the common instant blip. */
const FIRST_DELAY_MS = 0

/** Second wait, and the base the backoff doubles from. */
const BASE_DELAY_MS = 1_000

/**
 * The longest gap between attempts.
 *
 * Thirty seconds is the point where a person who has walked back into signal
 * would start wondering whether the app is broken. Backing off further would save
 * a negligible amount of battery and cost a great deal of trust.
 */
export const MAX_DELAY_MS = 30_000

/**
 * Up to this much is added at random.
 *
 * Every phone attached to a hub that restarts would otherwise reconnect on the
 * same schedule, arriving together and knocking it over again. The jitter is
 * added rather than applied as a factor so it cannot shorten a delay.
 */
const JITTER_MS = 1_000

/**
 * The delay before attempt number `retry`, counted from zero.
 *
 * Deliberately a pure function of the attempt number and a random source, so the
 * schedule can be asserted rather than observed.
 */
export function reconnectDelay(retry: number, random: () => number = Math.random): number {
  if (retry <= 0) return FIRST_DELAY_MS

  const doubled = BASE_DELAY_MS * 2 ** (retry - 1)
  const capped = Math.min(doubled, MAX_DELAY_MS)

  return capped + Math.floor(random() * JITTER_MS)
}

/**
 * SignalR's reconnect schedule: the same backoff, and never `null`.
 *
 * Returning `null` is how a policy tells SignalR to stop for good, so this one
 * never does.
 */
export class ForeverRetryPolicy implements IRetryPolicy {
  private readonly random: () => number

  constructor(random: () => number = Math.random) {
    this.random = random
  }

  nextRetryDelayInMilliseconds(context: RetryContext): number {
    return reconnectDelay(context.previousRetryCount, this.random)
  }
}
