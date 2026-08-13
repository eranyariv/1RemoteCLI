import { describe, expect, it } from 'vitest'

import {
  NoModifiers,
  applyModifiers,
  controlCode,
  encodeCsi,
  isArmed,
  modifierParameter,
} from './modifiers'

/**
 * Like the key table next door, most of this is a test of constants — and for the
 * same reason. A wrong byte here does not crash: it means `Ctrl+R` types an `r`, or
 * `Ctrl+A` sends nothing, and the person holding the phone concludes the product is
 * broken without ever being told why. Pinning the encodings makes changing one a
 * decision rather than an accident.
 */
describe('control codes', () => {
  it('folds letters into the control range, whatever their case', () => {
    // ASCII's own design: clear bit 6 and a printable becomes its control code.
    expect(controlCode('a')).toBe(0x01)
    expect(controlCode('A')).toBe(0x01)
    expect(controlCode('c')).toBe(0x03)
    expect(controlCode('r')).toBe(0x12)
    expect(controlCode('z')).toBe(0x1a)
  })

  it('maps the punctuation that lands in the control range', () => {
    expect(controlCode('@')).toBe(0x00)
    expect(controlCode('[')).toBe(0x1b)
    expect(controlCode('\\')).toBe(0x1c)
    expect(controlCode(']')).toBe(0x1d)
    expect(controlCode('^')).toBe(0x1e)
    expect(controlCode('_')).toBe(0x1f)
  })

  it('maps the digit row the way a DEC terminal does', () => {
    // Kept because `Ctrl+[` and `Ctrl+\` are awkward on a phone layout, and
    // `Ctrl+8` is the usual way to reach Delete.
    expect(controlCode('2')).toBe(0x00)
    expect(controlCode('3')).toBe(0x1b)
    expect(controlCode('8')).toBe(0x7f)
  })

  it('makes Ctrl+Space a NUL and Ctrl+? a Delete', () => {
    expect(controlCode(' ')).toBe(0x00)
    expect(controlCode('?')).toBe(0x7f)
  })

  it('says so when Ctrl means nothing for a character', () => {
    // Null rather than a guess. The caller sends the character unchanged, so
    // arming Ctrl and typing an accented letter produces the letter.
    expect(controlCode('é')).toBeNull()
    expect(controlCode('1')).toBeNull()
    expect(controlCode('')).toBeNull()
    expect(controlCode('ab')).toBeNull()
  })
})

describe('applying armed modifiers', () => {
  it('leaves text alone when nothing is armed', () => {
    expect([...applyModifiers('dir', NoModifiers)]).toEqual([0x64, 0x69, 0x72])
  })

  it('turns a letter into its control code when Ctrl is armed', () => {
    expect([...applyModifiers('r', { ctrl: true, alt: false })]).toEqual([0x12])
  })

  it('sends the character unchanged when Ctrl does not apply to it', () => {
    // Arming Ctrl must never make a keystroke disappear.
    expect([...applyModifiers('é', { ctrl: true, alt: false })]).toEqual([0xc3, 0xa9])
  })

  it('does not apply Ctrl to more than one character', () => {
    // "Ctrl held while a word was pasted" is not something a keyboard can express,
    // and guessing at one would corrupt the paste.
    expect([...applyModifiers('ab', { ctrl: true, alt: false })]).toEqual([0x61, 0x62])
  })

  it('prefixes an escape when Alt is armed', () => {
    // Meta sends escape: what readline, bash and zsh are written against.
    expect([...applyModifiers('b', { ctrl: false, alt: true })]).toEqual([0x1b, 0x62])
    expect([...applyModifiers('.', { ctrl: false, alt: true })]).toEqual([0x1b, 0x2e])
  })

  it('applies Alt to a run of text, not just a single character', () => {
    expect([...applyModifiers('ab', { ctrl: false, alt: true })]).toEqual([0x1b, 0x61, 0x62])
  })

  it('combines both, escape first', () => {
    // Alt+Ctrl+C is ESC then 0x03, in that order — the escape introduces the
    // sequence, so putting it second would send two unrelated keys.
    expect([...applyModifiers('c', { ctrl: true, alt: true })]).toEqual([0x1b, 0x03])
  })
})

describe('cursor keys with modifiers', () => {
  it('keeps the short form when nothing is armed', () => {
    // `CSI A`, not the technically equivalent `CSI 1 A`. Programs that match on
    // exact bytes — which agent prompts do — only recognise the short one.
    expect([...encodeCsi('A', NoModifiers)]).toEqual([0x1b, 0x5b, 0x41])
    expect([...encodeCsi('D', NoModifiers)]).toEqual([0x1b, 0x5b, 0x44])
  })

  it('numbers the modifiers the way a terminal reports them', () => {
    // One-based, with shift 1, alt 2 and ctrl 4 added on.
    expect(modifierParameter(NoModifiers)).toBe(1)
    expect(modifierParameter({ ctrl: false, alt: true })).toBe(3)
    expect(modifierParameter({ ctrl: true, alt: false })).toBe(5)
    expect(modifierParameter({ ctrl: true, alt: true })).toBe(7)
  })

  it('folds the modifiers into the sequence', () => {
    // Ctrl+Left is CSI 1;5D — what readline reads as "back one word".
    expect(new TextDecoder().decode(encodeCsi('D', { ctrl: true, alt: false }))).toBe(
      '\u001b[1;5D',
    )
    expect(new TextDecoder().decode(encodeCsi('C', { ctrl: false, alt: true }))).toBe(
      '\u001b[1;3C',
    )
  })
})

describe('armed state', () => {
  it('is armed when either modifier is', () => {
    expect(isArmed(NoModifiers)).toBe(false)
    expect(isArmed({ ctrl: true, alt: false })).toBe(true)
    expect(isArmed({ ctrl: false, alt: true })).toBe(true)
  })
})
