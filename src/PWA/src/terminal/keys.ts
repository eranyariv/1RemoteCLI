/**
 * The byte sequences a phone keyboard cannot produce.
 *
 * A touch device may have no keyboard open at all, and its software keyboard still
 * has no Ctrl, Esc, Tab or arrow keys. Those are most of what operating a terminal
 * actually requires. Answering an agent's prompt needs Enter; escaping a menu needs
 * Esc; recalling the last command needs cursor-up; stopping a runaway build needs
 * Ctrl+C. Without an on-screen row for these, the app can only read, and a read-only
 * terminal is not what anyone is away from their desk wishing for.
 *
 * These are the sequences a real terminal emits, byte for byte, because the whole
 * design rests on the PTY being unable to tell the difference between the phone and
 * the keyboard on the desk. `Ctrl+C` is `0x03` on the wire — not a flag, not a
 * special message the agent interprets — precisely so that a program with its own
 * meaning for `0x03` gets what it expects.
 *
 * Ctrl+C is nonetheless *also* exposed as a distinct hub method, because a session
 * that has wedged badly enough to stop reading its input still needs to be
 * interruptible; that path signals the process rather than writing to the pipe.
 */

import { encodeCsi, type Modifiers } from './modifiers'

export interface KeyDefinition {
  /** What goes on the button. */
  label: string
  /** Longer name, for assistive technology. */
  name: string
  bytes: Uint8Array
  /**
   * The final character of the key's CSI sequence, for the keys that can carry a
   * modifier parameter. Present means "this key knows how to be Ctrl-ed".
   */
  csiFinal?: string
  /** True when the key should stand out — the one that stops things. */
  emphasis?: boolean
  /**
   * Routed through the interrupt hub method rather than written as bytes.
   * <p>
   * A flag rather than a comparison against the label, because the label is a
   * presentation detail: renaming the button from `^C` to `Ctrl+C` should not
   * silently turn the one key that has to work on a wedged session into a byte
   * written to a pipe nobody is reading.
   */
  interrupt?: boolean
}

const ESC = 0x1b

function seq(...bytes: number[]): Uint8Array {
  return new Uint8Array(bytes)
}

/**
 * Cursor keys in **normal** mode (`CSI A`), not application mode (`SS3 A`).
 *
 * Full-screen programs switch the terminal into application cursor mode and then
 * expect `\x1bOA`; a shell at a prompt expects `\x1b[A`. Getting this right in
 * general requires tracking DECCKM from the output stream, which is the emulator's
 * job in Stage 2. Until it exists, normal mode is the correct default: it is what
 * readline, PowerShell and every agent prompt want, which is where cursor-up is
 * actually used. Inside `vim`, arrows may misbehave until the emulator lands — an
 * honest Stage 1 limitation rather than a guess that breaks the common case.
 */
export const Keys = {
  escape: { label: 'Esc', name: 'Escape', bytes: seq(ESC) },
  tab: { label: 'Tab', name: 'Tab', bytes: seq(0x09) },
  ctrlC: {
    label: '^C',
    name: 'Ctrl+C — interrupt',
    bytes: seq(0x03),
    emphasis: true,
    interrupt: true,
  },
  ctrlD: { label: '^D', name: 'Ctrl+D — end of input', bytes: seq(0x04) },
  ctrlZ: { label: '^Z', name: 'Ctrl+Z — suspend', bytes: seq(0x1a) },
  up: { label: '↑', name: 'Cursor up', bytes: seq(ESC, 0x5b, 0x41), csiFinal: 'A' },
  down: { label: '↓', name: 'Cursor down', bytes: seq(ESC, 0x5b, 0x42), csiFinal: 'B' },
  right: { label: '→', name: 'Cursor right', bytes: seq(ESC, 0x5b, 0x43), csiFinal: 'C' },
  left: { label: '←', name: 'Cursor left', bytes: seq(ESC, 0x5b, 0x44), csiFinal: 'D' },
  enter: { label: 'Enter ↵', name: 'Enter — submit line', bytes: seq(0x0d) },
} as const satisfies Record<string, KeyDefinition>

/**
 * The row shown above the keyboard, in the order the spec draws it: the modifiers
 * first, because they change what the rest of the keyboard means; then Enter where it
 * is always visible; then the keys a phone is missing outright and the arrows as a
 * group; and the interrupt at the far end, alone, where a thumb lands and nothing else
 * is.
 *
 * The modifiers are not in this list. They are not keys — they send nothing — and
 * modelling them as `KeyDefinition`s would mean every consumer of this row had to
 * know which entries were real.
 */
export const KeyBarLayout: KeyDefinition[] = [
  // Keep Enter first so it remains visible without horizontally scrolling the bar.
  Keys.enter,
  Keys.escape,
  Keys.tab,
  Keys.up,
  Keys.down,
  Keys.left,
  Keys.right,
  Keys.ctrlC,
]

/**
 * The keys behind the "more" disclosure — real, but rarely the thing you need.
 *
 * `^D` and `^Z` stay here rather than being left to the sticky Ctrl. They are
 * reachable that way, but both are one tap from ending a session, and a shortcut you
 * arrive at accidentally is worse than one you have to go looking for.
 */
export const ExtraKeys: KeyDefinition[] = [Keys.ctrlD, Keys.ctrlZ]

/**
 * The bytes a bar key sends with the given modifiers armed.
 *
 * Cursor keys carry their modifiers inside the sequence, because that is how a
 * terminal reports `Ctrl+Left` and how readline recognises it as "move a word". Every
 * other key falls back to the general rule, where Alt prefixes an escape and Ctrl,
 * having nothing to modify, is ignored — arming Ctrl and then pressing Tab should
 * send a Tab, not nothing.
 */
export function encodeKey(key: KeyDefinition, modifiers: Modifiers): Uint8Array {
  if (key.csiFinal) return encodeCsi(key.csiFinal, modifiers)

  if (!modifiers.alt) return key.bytes

  const out = new Uint8Array(key.bytes.length + 1)
  out[0] = ESC
  out.set(key.bytes, 1)
  return out
}

const encoder = new TextEncoder()

/**
 * UTF-8, because that is what a modern PTY expects and what the agent writes
 * unaltered into the pipe. A phone keyboard produces emoji and accented characters
 * as a matter of course, and encoding them as anything else puts mojibake into a
 * command line.
 */
export function encodeText(text: string): Uint8Array {
  return encoder.encode(text)
}

/**
 * xterm's `onBinary` hands back a string whose char codes are already bytes — a
 * latin1-ish channel used for mouse reports and similar. Re-encoding it as UTF-8
 * would corrupt every byte above 0x7f.
 */
export function encodeBinary(text: string): Uint8Array {
  const bytes = new Uint8Array(text.length)

  for (let i = 0; i < text.length; i += 1) {
    bytes[i] = text.charCodeAt(i) & 0xff
  }

  return bytes
}
