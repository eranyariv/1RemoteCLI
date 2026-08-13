/**
 * Ctrl and Alt for a keyboard that has neither.
 *
 * A phone's software keyboard produces letters, digits and Return. There is no Ctrl
 * key on it, which means no `Ctrl+R` to search shell history, no `Ctrl+A` to jump to
 * the start of a line, and no `Ctrl+W` to rub out a word — a large part of what using
 * a terminal actually consists of. The accessory bar supplies the modifiers, but a
 * modifier is not a key: it changes what the *next* key means. So it is armed by a
 * tap and consumed by whatever is typed next.
 *
 * Sticky rather than held, because a thumb cannot hold one button while pressing
 * another on a screen the size of a phone. This is the same affordance iOS and
 * Android already use for their own Shift key, so the interaction is not new to
 * anybody holding the device.
 *
 * Everything here is a pure function over bytes. The encodings are not ours: they are
 * what a real terminal puts on the wire, and the whole design rests on the PTY being
 * unable to tell the phone apart from the keyboard on the desk.
 */

export interface Modifiers {
  ctrl: boolean
  alt: boolean
}

export const NoModifiers: Modifiers = { ctrl: false, alt: false }

/** Nothing is armed, so nothing needs consuming. */
export function isArmed(modifiers: Modifiers): boolean {
  return modifiers.ctrl || modifiers.alt
}

const ESC = 0x1b
const DEL = 0x7f

/**
 * The control code a character produces when Ctrl is held, or null when Ctrl has no
 * meaning for it.
 *
 * The letter rule is the whole of ASCII's design: control codes are the printable
 * characters with bit 6 cleared, so `A` (0x41) becomes 0x01 and `_` (0x5f) becomes
 * 0x1f. The digit row is a convention from DEC terminals that survives because
 * `Ctrl+[` — the same code as Esc — is hard to type on many layouts, and because
 * `Ctrl+8` is the usual way to reach Delete.
 *
 * Returning null rather than a guess matters: a character Ctrl does not modify must
 * be sent unchanged, so that arming Ctrl and then typing `é` produces `é` rather
 * than silence.
 */
export function controlCode(character: string): number | null {
  if (character.length !== 1) return null

  const code = character.codePointAt(0)!

  // A–Z and a–z. Lower case is folded up first, since Ctrl ignores case.
  if ((code >= 0x41 && code <= 0x5a) || (code >= 0x61 && code <= 0x7a)) {
    return code & 0x1f
  }

  switch (character) {
    // The ASCII rule again, for the punctuation that lands in the control range.
    case '@':
      return 0x00
    case '[':
      return ESC
    case '\\':
      return 0x1c
    case ']':
      return 0x1d
    case '^':
      return 0x1e
    case '_':
      return 0x1f
    case ' ':
      // Ctrl+Space is NUL, which is how emacs and readline set a mark.
      return 0x00
    case '?':
      return DEL

    // The digit row, for the codes whose punctuation is awkward to reach.
    case '2':
      return 0x00
    case '3':
      return ESC
    case '4':
      return 0x1c
    case '5':
      return 0x1d
    case '6':
      return 0x1e
    case '7':
      return 0x1f
    case '8':
      return DEL

    default:
      return null
  }
}

const encoder = new TextEncoder()

/**
 * Applies the armed modifiers to text the software keyboard produced.
 *
 * Ctrl only applies to a single character, because "Ctrl held down while a word was
 * pasted" is not a thing a keyboard can express and guessing at one would corrupt the
 * paste. Alt applies regardless, since ESC-prefixing a run of text is exactly what a
 * terminal does when Alt is held — and `Alt+.`, which inserts the last argument of
 * the previous command, is one of the reasons anybody wants Alt on a phone at all.
 */
export function applyModifiers(text: string, modifiers: Modifiers): Uint8Array {
  let body: Uint8Array

  const code = modifiers.ctrl ? controlCode(text) : null

  if (code !== null) {
    body = new Uint8Array([code])
  } else {
    body = encoder.encode(text)
  }

  if (!modifiers.alt) return body

  // Meta sends escape: the convention every terminal emulator has settled on, and
  // the one readline, bash and zsh are written against.
  const out = new Uint8Array(body.length + 1)
  out[0] = ESC
  out.set(body, 1)
  return out
}

/**
 * The parameter a terminal puts in a CSI sequence to say which modifiers were held.
 *
 * One-based, with shift 1, alt 2 and ctrl 4 added on: so Alt is 3, Ctrl is 5 and both
 * are 7. Shift is absent here because the software keyboard produces shifted
 * characters directly and there is no separate Shift on the accessory bar.
 */
export function modifierParameter(modifiers: Modifiers): number {
  return 1 + (modifiers.alt ? 2 : 0) + (modifiers.ctrl ? 4 : 0)
}

/**
 * A cursor or function key, with the modifiers folded into the sequence.
 *
 * Unmodified keys keep the short form (`CSI A`) rather than the technically
 * equivalent `CSI 1 A`: the short form is what real terminals send, and a program
 * matching on the exact bytes — which agent prompts do — would not recognise the
 * long one.
 */
export function encodeCsi(final: string, modifiers: Modifiers): Uint8Array {
  const parameter = modifierParameter(modifiers)

  const sequence = parameter === 1 ? `\u001b[${final}` : `\u001b[1;${parameter}${final}`

  return encoder.encode(sequence)
}
