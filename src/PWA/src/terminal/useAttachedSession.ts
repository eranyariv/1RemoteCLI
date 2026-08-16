import { useCallback, useEffect, useRef, useState } from 'react'

import type { RelayClient } from '../relay/client'
import { ErrorCodes } from '../protocol/errors'
import type { HubError, TerminalOutputKind } from '../protocol/wire'
import { EMPTY_STATS, Sampler, type LatencyStats } from './latency'
import { receive, startOfStream, type StreamPosition } from './stream'
import { TraceRecorder } from './trace'

export type AttachState = 'attaching' | 'attached' | 'reconnecting' | 'closed' | 'failed'

export interface AttachedSession {
  state: AttachState
  error: HubError | null
  /** Set once the program exits, so the view can say why the screen stopped. */
  exitCode: number | null
  /**
   * True when the session was already gone by the time we got back to it, so the
   * view can say that rather than reporting an exit code it never saw.
   */
  endedWhileAway: boolean
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
  onOutput(data: Uint8Array, kind: TerminalOutputKind): void
  /** Whether the relay connection is currently up. Drives re-attach. */
  connected: boolean
  /**
   * The shape to hand the session back when the phone stops looking at it.
   *
   * Resizing on the phone reshapes the real PTY, and nothing at the desk resizes it
   * back — so without this, walking away leaves a 45-column program stranded inside a
   * wide desktop window until somebody drags the corner.
   */
  restoreOnDetach?: { cols: number; rows: number }
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

  const position = useRef<StreamPosition>(startOfStream)

  // Set once the session is known to be over. Read by the attach effect, which
  // must not go looking for a session it watched exit — the hub would answer
  // "no such session" and the view would report it as having ended while we were
  // away, which is a different and less true story.
  const finished = useRef(false)

  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<AttachState>('attaching')
  const [error, setError] = useState<HubError | null>(null)
  const [exitCode, setExitCode] = useState<number | null>(null)
  const [endedWhileAway, setEndedWhileAway] = useState(false)
  const [missedOutput, setMissedOutput] = useState(false)
  const [latency, setLatency] = useState<LatencyStats>(EMPTY_STATS)
  const [recording, setRecording] = useState(false)

  const restore = useRef(options.restoreOnDetach)
  restore.current = options.restoreOnDetach

  // Hand the session back its original shape on the way out.
  //
  // Declared before the attach effect so that its cleanup runs first: the hub only
  // forwards a resize from a client that is still attached, so sending this after
  // the detach would send it into a closed door. Its dependencies are deliberately
  // narrow — a reconnection re-runs the attach effect below and must not be read as
  // the user walking away.
  useEffect(() => {
    return () => {
      if (finished.current) return

      const shape = restore.current
      if (!shape) return

      // Fire-and-forget, like the detach it precedes. The socket may already be
      // gone, and an error here would be reported about a screen the user has
      // just left.
      void client.resize(sessionId, shape.cols, shape.rows)
    }
  }, [client, sessionId])

  useEffect(() => {
    const off = [
      client.on('terminalOutput', (output) => {
        if (output.sessionId !== sessionId) return

        // Delivery is at-least-once, so a frame can arrive twice; drawing it twice
        // would duplicate a chunk of the terminal. A gap is the opposite problem:
        // output was produced that we will never see, and saying so is better than
        // rendering the remainder as though it were continuous, because the user acts
        // on what is on the screen.
        const step = receive(position.current, output)
        position.current = step.position

        if (step.missed) setMissedOutput(true)
        if (!step.apply) return

        sampler.output()
        recorder.frame(output.seq, output.kind, output.data)
        onOutput.current(output.data, output.kind)
      }),

      client.on('sessionClosed', (closedMachine, closedSession, code) => {
        if (closedMachine !== machineId || closedSession !== sessionId) return
        sampler.discardPending()
        finished.current = true
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
    if (finished.current) return

    if (!connected) {
      // Say so. While the socket is down no output can arrive, and a terminal that
      // looks live but has quietly stopped updating is worse than one that admits
      // it is offline, because the user will read the stale screen and type into
      // it. States that are already final are left alone: a session that ended has
      // not become uncertain just because the network did.
      setState((current) => (current === 'attaching' || current === 'attached' ? 'reconnecting' : current))
      return
    }

    let cancelled = false
    setState('attaching')
    setError(null)

    void (async () => {
      const { cols, rows } = geometry.current
      const problem = await client.attach(machineId, sessionId, cols, rows, position.current.applied)
      if (cancelled) return

      if (problem) {
        // The session being gone is not a failure to recover from — it is the
        // answer. Offering a retry button for a terminal that was closed at the
        // desk half an hour ago invites the user to keep pressing it.
        if (problem.code === ErrorCodes.SessionNotFound) {
          finished.current = true
          setEndedWhileAway(true)
          setState('closed')
          return
        }

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

  const retry = useCallback(() => {
    finished.current = false
    setEndedWhileAway(false)
    setAttempt((n) => n + 1)
  }, [])

  return {
    state,
    error,
    exitCode,
    endedWhileAway,
    lastSeq: position.current.applied,
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
