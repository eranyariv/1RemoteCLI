/**
 * The byte sequences a phone keyboard cannot produce.
 *
 * A software keyboard has letters, digits and Return. It has no Ctrl, no Esc, no
 * Tab, and no arrow keys — and those are most of what operating a terminal actually
 * requires. Answering an agent's prompt needs Return; escaping a menu needs Esc;
 * recalling the last command needs cursor-up; stopping a runaway build needs Ctrl+C.
 * Without an on-screen row for these, the app can only read, and a read-only terminal
 * is not what anyone is away from their desk wishing for.
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

export interface KeyDefinition {
  /** What goes on the button. */
  label: string
  /** Longer name, for assistive technology. */
  name: string
  bytes: Uint8Array
  /** True when the key should stand out — the one that stops things. */
  emphasis?: boolean
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
  ctrlC: { label: '^C', name: 'Ctrl+C — interrupt', bytes: seq(0x03), emphasis: true },
  ctrlD: { label: '^D', name: 'Ctrl+D — end of input', bytes: seq(0x04) },
  ctrlZ: { label: '^Z', name: 'Ctrl+Z — suspend', bytes: seq(0x1a) },
  up: { label: '↑', name: 'Cursor up', bytes: seq(ESC, 0x5b, 0x41) },
  down: { label: '↓', name: 'Cursor down', bytes: seq(ESC, 0x5b, 0x42) },
  right: { label: '→', name: 'Cursor right', bytes: seq(ESC, 0x5b, 0x43) },
  left: { label: '←', name: 'Cursor left', bytes: seq(ESC, 0x5b, 0x44) },
  enter: { label: '⏎', name: 'Return', bytes: seq(0x0d) },
} as const satisfies Record<string, KeyDefinition>

/**
 * The row shown above the keyboard, in reach order: the keys that stop or escape
 * something sit at the ends where a thumb lands, and the arrows cluster in the
 * middle where they are used as a group.
 */
export const KeyBarLayout: KeyDefinition[] = [
  Keys.escape,
  Keys.tab,
  Keys.left,
  Keys.up,
  Keys.down,
  Keys.right,
  Keys.ctrlC,
]

/** The keys behind the "more" disclosure — real, but rarely the thing you need. */
export const ExtraKeys: KeyDefinition[] = [Keys.ctrlD, Keys.ctrlZ, Keys.enter]

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
