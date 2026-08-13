/**
 * Keystroke-to-echo latency measurement.
 *
 * The design spec targets a p50 of 60 ms from a keypress on the phone to the echo
 * appearing on it. That number is not decoration: below roughly 100 ms typing feels
 * like typing, and above roughly 200 ms it feels like operating a machine over a
 * link — people stop trusting what they typed and start double-checking. Which side
 * of that line we land on is a property of the whole path (phone → hub → agent → PTY
 * and back), so it can only be measured end to end, on a real device, over a real
 * network.
 *
 * Measuring it now rather than at the end is the entire point. If the interaction is
 * wrong, a fix at this stage is a design change; the same fix once the emulator,
 * scrollback and reconnect logic are built on top is a rewrite.
 *
 * The measurement is a deliberate approximation. Nothing in the protocol correlates
 * an input frame with the output it caused, and adding a correlation id purely for
 * telemetry would put a field on the hot path forever. Instead: when a keystroke goes
 * out and nothing is outstanding, note the time; the next output frame for that
 * session closes it. That over-reports when the program was about to print anyway
 * (a sample taken mid-`npm install` measures the next progress tick, not the echo),
 * so samples taken while output is already flowing are discarded and the kept count
 * is reported alongside the number. A figure with a known bias and an honest sample
 * count beats a precise-looking one that quietly measures something else.
 */

/** How long a pending sample may wait before we assume the echo never came. */
const SAMPLE_TIMEOUT_MS = 2_000

/**
 * Output frames arriving this close together mean the program is producing output on
 * its own, so the next frame is not an echo of anything.
 */
const BUSY_GAP_MS = 40

export interface LatencyStats {
  /** Samples kept, after discarding those taken while output was already flowing. */
  count: number
  p50: number | null
  p95: number | null
  worst: number | null
  /** Keystrokes whose echo never arrived within the timeout. */
  lost: number
}

export const EMPTY_STATS: LatencyStats = { count: 0, p50: null, p95: null, worst: null, lost: 0 }

export class Sampler {
  private readonly samples: number[] = []
  private readonly now: () => number
  private readonly limit: number
  private pendingAt: number | null = null
  private lastOutputAt = 0
  private lost = 0

  constructor(now: () => number = () => performance.now(), limit = 500) {
    this.now = now
    this.limit = limit
  }

  /** Call when a keystroke is handed to the transport. */
  keystroke(): void {
    // Only one outstanding sample at a time. A fast typist would otherwise have
    // every keystroke resolved by the echo of the first one.
    if (this.pendingAt !== null) return

    const at = this.now()

    // Typing into a program that is already printing measures the program, not the
    // link, so do not start a sample there.
    if (at - this.lastOutputAt < BUSY_GAP_MS) return

    this.pendingAt = at
  }

  /** Call for every output frame that arrives for the attached session. */
  output(): void {
    const at = this.now()
    const pending = this.pendingAt
    this.lastOutputAt = at

    if (pending === null) return

    this.pendingAt = null
    const elapsed = at - pending

    if (elapsed > SAMPLE_TIMEOUT_MS) {
      this.lost += 1
      return
    }

    this.samples.push(elapsed)

    // A rolling window: the last few hundred keystrokes describe the link the user
    // is on now, which is the thing they can act on. A lifetime average mostly
    // describes the wifi they were on an hour ago.
    if (this.samples.length > this.limit) this.samples.shift()
  }

  /** Call when the pending keystroke can no longer be answered — a detach, say. */
  discardPending(): void {
    this.pendingAt = null
  }

  stats(): LatencyStats {
    if (this.samples.length === 0) {
      return { ...EMPTY_STATS, lost: this.lost }
    }

    const sorted = [...this.samples].sort((a, b) => a - b)

    return {
      count: sorted.length,
      p50: percentile(sorted, 0.5),
      p95: percentile(sorted, 0.95),
      worst: sorted[sorted.length - 1],
      lost: this.lost,
    }
  }

  reset(): void {
    this.samples.length = 0
    this.pendingAt = null
    this.lost = 0
  }
}

/** Nearest-rank percentile. Exact, and honest about tiny sample counts. */
function percentile(sorted: number[], fraction: number): number {
  const rank = Math.ceil(fraction * sorted.length)
  const index = Math.min(sorted.length - 1, Math.max(0, rank - 1))
  return sorted[index]
}

/** How the number should be read, for the status bar. */
export function verdict(p50: number | null): 'good' | 'fair' | 'poor' | 'unknown' {
  if (p50 === null) return 'unknown'
  if (p50 <= 60) return 'good'
  if (p50 <= 150) return 'fair'
  return 'poor'
}
