import { describe, expect, it } from 'vitest'

import { ExtraKeys, KeyBarLayout, Keys, encodeBinary, encodeKey, encodeText } from './keys'
import { NoModifiers } from './modifiers'

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

    for (const label of ['Esc', 'Tab', '^C', '↑', '↓', '←', '→', '⏎']) {
      expect(shown.has(label)).toBe(true)
    }
  })

  it('keeps the interrupt on the always-visible row', () => {
    // Never behind a disclosure. Stopping a runaway agent is the most time-critical
    // thing anybody does here, and it must not cost a second tap or a moment's
    // thought about where the key went.
    expect(KeyBarLayout.some((key) => key.interrupt)).toBe(true)
    expect(ExtraKeys.some((key) => key.interrupt)).toBe(false)
  })

  it('marks the interrupt key as the one that stands out', () => {
    // It is the key people reach for in a hurry, and the only one on the row with
    // consequences.
    const emphasised = KeyBarLayout.filter((key) => key.emphasis)
    expect(emphasised.map((key) => key.label)).toEqual(['^C'])
  })

  it('identifies the interrupt by a flag, not by its label', () => {
    // The label is presentation. Routing the one key that has to work on a wedged
    // session by comparing against it would make renaming the button a silent
    // functional change.
    const flagged = [...KeyBarLayout, ...ExtraKeys].filter((key) => key.interrupt)
    expect(flagged.map((key) => key.label)).toEqual(['^C'])
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

describe('bar keys with modifiers armed', () => {
  it('sends its plain bytes when nothing is armed', () => {
    expect([...encodeKey(Keys.tab, NoModifiers)]).toEqual([0x09])
    expect([...encodeKey(Keys.left, NoModifiers)]).toEqual([0x1b, 0x5b, 0x44])
  })

  it('folds modifiers into cursor keys', () => {
    // Ctrl+Left is how readline is told to move back a word.
    expect([...encodeKey(Keys.left, { ctrl: true, alt: false })]).toEqual([
      0x1b, 0x5b, 0x31, 0x3b, 0x35, 0x44,
    ])
  })

  it('still sends a key Ctrl cannot modify', () => {
    // Arming Ctrl and pressing Tab must send a Tab. Sending nothing would look
    // like the bar had stopped working.
    expect([...encodeKey(Keys.tab, { ctrl: true, alt: false })]).toEqual([0x09])
  })

  it('prefixes an escape for Alt', () => {
    expect([...encodeKey(Keys.tab, { ctrl: false, alt: true })]).toEqual([0x1b, 0x09])
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
