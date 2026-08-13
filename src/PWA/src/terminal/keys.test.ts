import { describe, expect, it } from 'vitest'

import { ExtraKeys, KeyBarLayout, Keys, encodeBinary, encodeText } from './keys'

/**
 * These are not really tests of code — they are tests of constants. That is the
 * point. Every value here is a byte sequence a real terminal emits, and the whole
 * design depends on the PTY being unable to tell the phone apart from the keyboard
 * on the desk. A wrong byte here does not crash anything; it just means Ctrl+C stops
 * working, or cursor-up types a `[A` into the prompt. Pinning them makes a change
 * deliberate rather than accidental.
 */
describe('control key bytes', () => {
  it('sends the ASCII control characters programs actually listen for', () => {
    expect(Keys.ctrlC.bytes).toEqual(new Uint8Array([0x03]))
    expect(Keys.ctrlD.bytes).toEqual(new Uint8Array([0x04]))
    expect(Keys.ctrlZ.bytes).toEqual(new Uint8Array([0x1a]))
    expect(Keys.escape.bytes).toEqual(new Uint8Array([0x1b]))
    expect(Keys.tab.bytes).toEqual(new Uint8Array([0x09]))
  })

  it('sends carriage return for Return, not line feed', () => {
    // A PTY in canonical mode submits on CR. LF here would leave every prompt
    // waiting for an answer that had already been typed.
    expect(Keys.enter.bytes).toEqual(new Uint8Array([0x0d]))
  })

  it('sends cursor keys in normal mode', () => {
    // CSI A, not SS3 A. Normal mode is what readline, PowerShell and agent prompts
    // expect; application mode is a full-screen program's business, and honouring
    // it needs DECCKM tracking that arrives with the emulator.
    expect(Keys.up.bytes).toEqual(new Uint8Array([0x1b, 0x5b, 0x41]))
    expect(Keys.down.bytes).toEqual(new Uint8Array([0x1b, 0x5b, 0x42]))
    expect(Keys.right.bytes).toEqual(new Uint8Array([0x1b, 0x5b, 0x43]))
    expect(Keys.left.bytes).toEqual(new Uint8Array([0x1b, 0x5b, 0x44]))
  })
})

describe('key bar', () => {
  it('offers every key a phone keyboard cannot produce', () => {
    const shown = new Set([...KeyBarLayout, ...ExtraKeys].map((key) => key.label))

    for (const label of ['Esc', 'Tab', '^C', '↑', '↓', '←', '→']) {
      expect(shown.has(label)).toBe(true)
    }
  })

  it('marks the interrupt key as the one that stands out', () => {
    // It is the key people reach for in a hurry, and the only one on the row with
    // consequences.
    const emphasised = KeyBarLayout.filter((key) => key.emphasis)
    expect(emphasised.map((key) => key.label)).toEqual(['^C'])
  })

  it('names every key for assistive technology', () => {
    for (const key of [...KeyBarLayout, ...ExtraKeys]) {
      // "↑" must read as "Cursor up"; a screen reader announcing "up arrow
      // character" is not the same thing, and "^C" is unpronounceable.
      expect(key.name.length).toBeGreaterThanOrEqual(key.label.length)
      expect(key.name).not.toMatch(/^[↑↓←→⏎]$/)
    }
  })
})

describe('encoding', () => {
  it('encodes typed text as UTF-8', () => {
    // Compared as plain arrays: jsdom's TextEncoder returns a Uint8Array from a
    // different realm, which fails a deep-equality check against this file's
    // Uint8Array despite holding identical bytes.
    expect([...encodeText('dir')]).toEqual([0x64, 0x69, 0x72])
    expect([...encodeText('é')]).toEqual([0xc3, 0xa9])
  })

  it('passes binary channel bytes through unchanged', () => {
    // Re-encoding this as UTF-8 would turn every byte above 0x7f into two.
    expect([...encodeBinary('\u00ff\u0080')]).toEqual([0xff, 0x80])
  })
})
