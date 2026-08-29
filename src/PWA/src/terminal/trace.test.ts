import { describe, expect, it } from 'vitest'

import { TraceRecorder, fromBase64, toBase64 } from './trace'

function bytes(...values: number[]): Uint8Array {
  return new Uint8Array(values)
}

describe('base64', () => {
  it('round-trips arbitrary bytes', () => {
    const original = bytes(0x00, 0x1b, 0x5b, 0x41, 0xff, 0x7f, 0x80)
    expect(fromBase64(toBase64(original))).toEqual(original)
  })

  it('handles a payload large enough to overflow an argument list', () => {
    // `String.fromCharCode(...bytes)` throws on inputs around this size, which is
    // exactly the size of a trace of anything interesting.
    const original = new Uint8Array(300_000)
    for (let i = 0; i < original.length; i += 1) original[i] = i % 256

    expect(fromBase64(toBase64(original))).toEqual(original)
  })

  it('produces the same encoding the platform does', () => {
    expect(toBase64(bytes(0x68, 0x69))).toBe('aGk=')
  })
})

describe('TraceRecorder', () => {
  it('records nothing until it is started', () => {
    const recorder = new TraceRecorder()
    recorder.frame(1, 'Delta', bytes(0x61))

    expect(recorder.frameCount).toBe(0)
    expect(recorder.recording).toBe(false)
  })

  it('captures frames with their timing once started', () => {
    let t = 0
    const recorder = new TraceRecorder(() => t)

    recorder.start()
    t = 40
    recorder.frame(7, 'Delta', bytes(0x61, 0x62))
    t = 250
    recorder.frame(8, 'Snapshot', bytes(0x1b, 0x5b, 0x32, 0x4a))

    const trace = recorder.build({ program: 'pwsh', machine: 'desk', cols: 80, rows: 24 })

    expect(trace.frames).toEqual([
      { at: 40, seq: 7, kind: 'Delta', data: toBase64(bytes(0x61, 0x62)) },
      { at: 250, seq: 8, kind: 'Snapshot', data: toBase64(bytes(0x1b, 0x5b, 0x32, 0x4a)) },
    ])
  })

  it('captures timestamped diagnostics alongside terminal output', () => {
    let t = 0
    const recorder = new TraceRecorder(() => t)

    recorder.start()
    t = 25
    recorder.diagnostic('touch-scroll', 'touchstart', { x: 10, cancelable: true })

    expect(
      recorder.build({ program: 'pwsh', machine: 'desk', cols: 80, rows: 24 }).diagnostics,
    ).toEqual([
      {
        at: 25,
        source: 'touch-scroll',
        event: 'touchstart',
        details: { x: 10, cancelable: true },
      },
    ])
    expect(recorder.entryCount).toBe(1)
  })

  it('preserves idle gaps, because burst structure is part of what is being tested', () => {
    // An emulator that only ever sees frames back to back never exercises the case
    // where a control sequence is split across two arrivals.
    let t = 0
    const recorder = new TraceRecorder(() => t)

    recorder.start()
    t = 5_000
    recorder.frame(1, 'Delta', bytes(0x61))

    expect(recorder.build({ program: 'x', machine: 'y', cols: 1, rows: 1 }).frames[0].at).toBe(5_000)
  })

  it('starts a fresh recording rather than appending to the last one', () => {
    const recorder = new TraceRecorder(() => 0)

    recorder.start()
    recorder.frame(1, 'Delta', bytes(0x61))
    recorder.diagnostic('touch-scroll', 'touchstart', { x: 10 })
    recorder.start()

    expect(recorder.frameCount).toBe(0)
    expect(recorder.entryCount).toBe(0)
  })

  it('stops capturing after stop', () => {
    const recorder = new TraceRecorder(() => 0)

    recorder.start()
    recorder.stop()
    recorder.frame(1, 'Delta', bytes(0x61))

    expect(recorder.frameCount).toBe(0)
  })

  it('reports the payload size so the UI can warn before a trace gets silly', () => {
    const recorder = new TraceRecorder(() => 0)

    recorder.start()
    recorder.frame(1, 'Delta', new Uint8Array(1_000))
    recorder.frame(2, 'Delta', new Uint8Array(1_001))

    expect(recorder.byteCount).toBe(2_001)
  })

  it('records the metadata that makes a trace readable a month later', () => {
    const recorder = new TraceRecorder(() => 0)
    recorder.start()

    const trace = recorder.build({ program: 'claude', machine: 'desk', cols: 120, rows: 40 })

    expect(trace).toMatchObject({ version: 1, program: 'claude', machine: 'desk', cols: 120, rows: 40 })
    expect(Date.parse(trace.recordedAt)).not.toBeNaN()
  })
})
