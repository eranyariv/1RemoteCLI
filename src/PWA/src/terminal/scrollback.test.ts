import { beforeAll, describe, expect, it } from 'vitest'
import { Terminal } from '@xterm/xterm'

import { applyOutput, HISTORY_LIFETIME_MS } from './apply'

/**
 * What a snapshot does to the history above the screen, asserted against a real
 * emulator rather than a stand-in.
 *
 * `apply.test.ts` covers the decision — whether `reset` is called — and can do that
 * against two recorded method names. It cannot cover the thing that actually went
 * wrong, which is what the bytes do once xterm has them: a snapshot used to open with
 * RIS, and RIS discards the scrollback along with the screen. Both halves looked
 * correct in isolation, and together they threw away every line the user had already
 * scrolled past on each reconnection.
 *
 * The agent never keeps scrollback — see `SessionScreen` — so what is here is the only
 * copy there is.
 */

/** Only the preamble matters here; `VtSnapshotWriter` owns the full payload. */
const SNAPSHOT_PREAMBLE = '[0m[2J[H'

const RIS = 'c'

const bytes = (text: string) => new TextEncoder().encode(text)

function terminal() {
  const term = new Terminal({ cols: 20, rows: 5, scrollback: 1_000, allowProposedApi: true })
  const host = document.createElement('div')
  document.body.appendChild(host)
  term.open(host)

  return term
}

const write = (term: Terminal, text: string) =>
  new Promise<void>((resolve) => term.write(text, resolve))

const apply = (term: Terminal, text: string, awayMs: number) =>
  new Promise<void>((resolve) => {
    applyOutput(
      { reset: () => term.reset(), write: (data) => term.write(data, resolve) },
      bytes(text),
      'Snapshot',
      awayMs,
    )
  })

/** Thirty lines through a five-row terminal, so most of them end up as scrollback. */
async function withHistory() {
  const term = terminal()

  for (let line = 1; line <= 30; line++) await write(term, `line${line}
`)

  return term
}

const scrollbackDepth = (term: Terminal) => term.buffer.active.baseY

const lineAt = (term: Terminal, row: number) =>
  term.buffer.active.getLine(row)?.translateToString(true) ?? ''

describe('a snapshot and the history above it', () => {
  beforeAll(() => {
    // jsdom has no matchMedia, and xterm reads it to work out the device pixel ratio.
    window.matchMedia ??= ((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => false,
    })) as never
  })

  it('keeps the scrollback when the connection dropped for a moment', async () => {
    const term = await withHistory()

    const before = scrollbackDepth(term)
    expect(before).toBeGreaterThan(0)

    await apply(term, `${SNAPSHOT_PREAMBLE}repainted`, 4_000)

    // The whole point: four seconds in a lift must not cost the user the lines they
    // had already scrolled past.
    expect(scrollbackDepth(term)).toBe(before)
    expect(lineAt(term, 0)).toBe('line1')
    expect(lineAt(term, before)).toBe('repainted')
  })

  it('discards the scrollback for a session picked up a day later', async () => {
    const term = await withHistory()

    await apply(term, `${SNAPSHOT_PREAMBLE}repainted`, HISTORY_LIFETIME_MS)

    expect(scrollbackDepth(term)).toBe(0)
    expect(lineAt(term, 0)).toBe('repainted')
  })

  it('shows why the preamble cannot be a reset', async () => {
    const term = await withHistory()

    // Pinning the behaviour that caused this, so that reintroducing RIS into the
    // payload fails here rather than silently on somebody's phone. The agent-side
    // guarantee that it is absent lives in SnapshotRoundTripTests.
    await write(term, RIS)

    expect(scrollbackDepth(term)).toBe(0)
    expect(lineAt(term, 0)).toBe('')
  })
})
