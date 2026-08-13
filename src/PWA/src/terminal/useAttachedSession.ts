import { useCallback, useEffect, useRef, useState } from 'react'

import type { RelayClient } from '../relay/client'
import type { HubError } from '../protocol/wire'
import { EMPTY_STATS, Sampler, type LatencyStats } from './latency'
import { TraceRecorder } from './trace'

export type AttachState = 'attaching' | 'attached' | 'closed' | 'failed'

export interface AttachedSession {
  state: AttachState
  error: HubError | null
  /** Set once the program exits, so the view can say why the screen stopped. */
  exitCode: number | null
  /** Highest output sequence seen, so a re-attach can ask for the gap. */
  lastSeq: number | null
  /** True when a re-attach found a gap the hub could not fill. */
  missedOutput: boolean
  latency: LatencyStats
  recorder: TraceRecorder
  recording: boolean
  send(data: Uint8Array): void
  interrupt(): void
  resize(cols: number, rows: number): void
  startRecording(): void
  stopRecording(): void
  retry(): void
}

export interface AttachOptions {
  client: RelayClient
  machineId: string
  sessionId: string
  cols: number
  rows: number
  /** Called for every output frame. Deliberately not React state — see below. */
  onOutput(data: Uint8Array): void
  /** Whether the relay connection is currently up. Drives re-attach. */
  connected: boolean
}

/**
 * Owns one attachment for as long as the terminal is on screen.
 *
 * Output does **not** go through React state. A busy program emits output faster
 * than React can reconcile, and routing it through `setState` turns a terminal into
 * a slideshow while burning the phone's battery on renders nobody sees. The bytes go
 * straight from the socket to the emulator via `onOutput`; only things a human reads
 * at human speed — the connection state, the latency figure — are state.
 *
 * `onOutput` is held in a ref so a caller that passes an inline closure does not
 * resubscribe the socket on every render. That is not a micro-optimisation: an
 * unsubscribe/resubscribe cycle between two frames drops whatever arrived in between,
 * and dropped output in a terminal is corruption, not lag.
 */
export function useAttachedSession(options: AttachOptions): AttachedSession {
  const { client, machineId, sessionId, connected } = options

  const onOutput = useRef(options.onOutput)
  onOutput.current = options.onOutput

  // The geometry the terminal had when it last measured itself. Held in a ref
  // because the attach effect must not re-run — and re-attach — on every resize.
  const geometry = useRef({ cols: options.cols, rows: options.rows })
  geometry.current = { cols: options.cols, rows: options.rows }

  const samplerRef = useRef<Sampler | null>(null)
  samplerRef.current ??= new Sampler()
  const sampler = samplerRef.current

  const recorderRef = useRef<TraceRecorder | null>(null)
  recorderRef.current ??= new TraceRecorder()
  const recorder = recorderRef.current

  const lastSeq = useRef<number | null>(null)
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<AttachState>('attaching')
  const [error, setError] = useState<HubError | null>(null)
  const [exitCode, setExitCode] = useState<number | null>(null)
  const [missedOutput, setMissedOutput] = useState(false)
  const [latency, setLatency] = useState<LatencyStats>(EMPTY_STATS)
  const [recording, setRecording] = useState(false)

  useEffect(() => {
    const off = [
      client.on('terminalOutput', (output) => {
        if (output.sessionId !== sessionId) return

        // A gap means output was produced that we will never see. Say so rather
        // than rendering the remainder as though it were continuous — a terminal
        // silently missing a chunk is worse than one that admits it, because the
        // user acts on what is on the screen.
        const previous = lastSeq.current
        if (previous !== null && output.seq > previous + 1) setMissedOutput(true)
        lastSeq.current = output.seq

        sampler.output()
        recorder.frame(output.seq, output.kind, output.data)
        onOutput.current(output.data)
      }),

      client.on('sessionClosed', (closedMachine, closedSession, code) => {
        if (closedMachine !== machineId || closedSession !== sessionId) return
        sampler.discardPending()
        setExitCode(code)
        setState('closed')
      }),

      client.on('machineOffline', (offlineMachine) => {
        if (offlineMachine !== machineId) return
        sampler.discardPending()
        setState('failed')
        setError({
          code: 'MachineOffline',
          message: 'The machine went offline.',
          sessionId,
        })
      }),
    ]

    return () => {
      for (const unsubscribe of off) unsubscribe()
    }
  }, [client, machineId, sessionId, sampler, recorder])

  // Attach, and re-attach whenever the connection comes back. The hub's attachment
  // registry is keyed by connection, so a reconnected socket has no attachment at
  // all — re-attaching is the normal path after a phone unlocks, not error recovery.
  useEffect(() => {
    if (!connected) return

    let cancelled = false
    setState('attaching')
    setError(null)

    void (async () => {
      const { cols, rows } = geometry.current
      const problem = await client.attach(machineId, sessionId, cols, rows, lastSeq.current)
      if (cancelled) return

      if (problem) {
        setError(problem)
        setState('failed')
        return
      }

      setState('attached')
    })()

    return () => {
      cancelled = true
      sampler.discardPending()
      // Detaching is a courtesy the hub uses to stop fanning output at a screen
      // nobody is looking at. It is fire-and-forget: if the socket is already gone
      // the hub cleans up on disconnect anyway, and surfacing a failure here would
      // put an error on screen at the exact moment the user navigated away.
      void client.detach(sessionId)
    }
  }, [client, machineId, sessionId, connected, attempt, sampler])

  // The latency figure is read by a human, so it updates at human speed. Sampling
  // it on every frame would re-render the whole view thousands of times a minute to
  // change a number nobody can read that fast.
  useEffect(() => {
    const timer = setInterval(() => setLatency(sampler.stats()), 1_000)
    return () => clearInterval(timer)
  }, [sampler])

  const send = useCallback(
    (data: Uint8Array) => {
      sampler.keystroke()
      void client.sendInput(sessionId, data)
    },
    [client, sessionId, sampler],
  )

  const interrupt = useCallback(() => {
    sampler.keystroke()
    void client.interrupt(sessionId)
  }, [client, sessionId, sampler])

  const resize = useCallback(
    (cols: number, rows: number) => {
      void client.resize(sessionId, cols, rows)
    },
    [client, sessionId],
  )

  const startRecording = useCallback(() => {
    recorder.start()
    setRecording(true)
  }, [recorder])

  const stopRecording = useCallback(() => {
    recorder.stop()
    setRecording(false)
  }, [recorder])

  const retry = useCallback(() => setAttempt((n) => n + 1), [])

  return {
    state,
    error,
    exitCode,
    lastSeq: lastSeq.current,
    missedOutput,
    latency,
    recorder,
    recording,
    send,
    interrupt,
    resize,
    startRecording,
    stopRecording,
    retry,
  }
}
