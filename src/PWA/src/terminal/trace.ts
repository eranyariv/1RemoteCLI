/**
 * Records the raw bytes of a session so the Stage 2 emulator can be tested against
 * real programs rather than invented ones.
 *
 * The headless emulator has to reproduce what `claude`, `copilot`, `vim`, `htop` and
 * a progress-bar-heavy `npm install` actually put on the wire — not what the VT
 * specification says they might. Those programs use overlapping and occasionally
 * contradictory subsets of the escape repertoire, and the interesting cases are the
 * ones nobody would think to write by hand: a spinner that redraws by cursor-up and
 * erase-line, alternate-screen entry and exit, bracketed paste, an OSC title change
 * arriving mid-frame.
 *
 * Collecting them is nearly free right now, while a real session is on screen, and
 * expensive later, when it means setting the whole rig up again for the sole purpose
 * of recording. So the terminal view can record, and a recording is a file the
 * emulator tests can replay byte for byte.
 *
 * The format is deliberately plain JSON with base64 payloads: a trace has to survive
 * being committed to the repo, read in a diff, and loaded by a C# test, and none of
 * those want a bespoke binary container.
 */

export interface TraceFrame {
  /** Milliseconds since recording started. Preserves burst structure and idle gaps. */
  at: number
  seq: number
  kind: 'Delta' | 'Snapshot'
  /** Base64 of the raw bytes exactly as they left the PTY. */
  data: string
}

export interface Trace {
  version: 1
  /** What was running. The single most useful thing when reading a trace later. */
  program: string
  machine: string
  cols: number
  rows: number
  recordedAt: string
  frames: TraceFrame[]
}

export class TraceRecorder {
  private readonly frames: TraceFrame[] = []
  private readonly now: () => number
  private startedAt: number | null = null

  constructor(now: () => number = () => performance.now()) {
    this.now = now
  }

  get recording(): boolean {
    return this.startedAt !== null
  }

  get frameCount(): number {
    return this.frames.length
  }

  /** Total payload bytes captured, so the UI can warn before a trace gets silly. */
  get byteCount(): number {
    return this.frames.reduce((total, frame) => total + base64Bytes(frame.data), 0)
  }

  start(): void {
    this.frames.length = 0
    this.startedAt = this.now()
  }

  stop(): void {
    this.startedAt = null
  }

  frame(seq: number, kind: 'Delta' | 'Snapshot', data: Uint8Array): void {
    if (this.startedAt === null) return

    this.frames.push({
      at: Math.round(this.now() - this.startedAt),
      seq,
      kind,
      data: toBase64(data),
    })
  }

  build(meta: { program: string; machine: string; cols: number; rows: number }): Trace {
    return {
      version: 1,
      program: meta.program,
      machine: meta.machine,
      cols: meta.cols,
      rows: meta.rows,
      recordedAt: new Date().toISOString(),
      frames: [...this.frames],
    }
  }
}

/**
 * Chunked rather than `String.fromCharCode(...bytes)`: spreading a megabyte of
 * terminal output into an argument list overflows the call stack, and a trace of
 * `npm install` is exactly the case where it would.
 */
export function toBase64(bytes: Uint8Array): string {
  let binary = ''
  const chunk = 0x8000

  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk))
  }

  return btoa(binary)
}

export function fromBase64(value: string): Uint8Array {
  const binary = atob(value)
  const bytes = new Uint8Array(binary.length)

  for (let i = 0; i < binary.length; i += 1) {
    bytes[i] = binary.charCodeAt(i)
  }

  return bytes
}

function base64Bytes(value: string): number {
  const padding = value.endsWith('==') ? 2 : value.endsWith('=') ? 1 : 0
  return (value.length * 3) / 4 - padding
}

/**
 * Hands the trace to the browser as a download. On a phone this lands in Files,
 * which is enough to get it off the device and into the repo.
 */
export function downloadTrace(trace: Trace): void {
  const safe = trace.program.replace(/[^a-z0-9]+/gi, '-').toLowerCase() || 'session'
  const stamp = trace.recordedAt.replace(/[:.]/g, '-')
  const blob = new Blob([JSON.stringify(trace, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)

  const link = document.createElement('a')
  link.href = url
  link.download = `${safe}-${stamp}.trace.json`
  link.click()

  URL.revokeObjectURL(url)
}
