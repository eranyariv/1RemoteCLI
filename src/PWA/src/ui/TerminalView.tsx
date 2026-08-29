import { useCallback, useEffect, useRef, useState } from 'react'
import { FitAddon } from '@xterm/addon-fit'
import { WebLinksAddon } from '@xterm/addon-web-links'
import { Terminal } from '@xterm/xterm'
import '@xterm/xterm/css/xterm.css'

import { describeError } from '../protocol/errors'
import { CLI_TYPES, type CliType, type MachineInfo, type SessionInfo, type TerminalOutputKind } from '../protocol/wire'
import type { RelayClient } from '../relay/client'
import { sessionLabel } from '../relay/machines'
import { catalogFor, labelFor, type CommandDefinition } from '../terminal/catalog'
import { needsOnScreenKeys } from '../terminal/device'
import {
  ExtraKeys,
  KeyBarLayout,
  encodeBinary,
  encodeKey,
  encodeText,
  type KeyDefinition,
} from '../terminal/keys'
import { NoModifiers, applyModifiers, isArmed, type Modifiers } from '../terminal/modifiers'
import {
  measureViewport,
  sameViewport,
  viewportStyle,
  type ViewportBox,
} from '../terminal/viewport'
import { applyOutput } from '../terminal/apply'
import { verdict } from '../terminal/latency'
import { downloadTrace } from '../terminal/trace'
import { pasteClipboardText, quoteTerminalPath } from '../terminal/attachment'
import { refocusTerminalIfActive } from '../terminal/focus'
import { installTouchScroll } from '../terminal/touchScroll'
import { useAttachedSession } from '../terminal/useAttachedSession'
import { Banner } from './Chrome'
import { PAN_X, useLockHorizontalPan } from './useLockHorizontalPan'

export interface TerminalViewProps {
  client: RelayClient
  connected: boolean
  machine: MachineInfo
  session: SessionInfo
  onClose(): void
}

/** Matches the surrounding UI so the terminal does not look like a pasted-in widget. */
const THEME = {
  background: '#020617',
  foreground: '#e2e8f0',
  cursor: '#38bdf8',
  cursorAccent: '#020617',
  selectionBackground: '#1e40af88',
  black: '#0f172a',
  red: '#f87171',
  green: '#4ade80',
  yellow: '#fbbf24',
  blue: '#60a5fa',
  magenta: '#c084fc',
  cyan: '#22d3ee',
  white: '#e2e8f0',
  brightBlack: '#475569',
  brightRed: '#fca5a5',
  brightGreen: '#86efac',
  brightYellow: '#fde047',
  brightBlue: '#93c5fd',
  brightMagenta: '#d8b4fe',
  brightCyan: '#67e8f9',
  brightWhite: '#f8fafc',
}

export function TerminalView({ client, connected, machine, session, onClose }: TerminalViewProps) {
  const hostRef = useRef<HTMLDivElement | null>(null)
  const screenRef = useRef<HTMLDivElement | null>(null)
  const termRef = useRef<Terminal | null>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const fileInputRef = useRef<HTMLInputElement | null>(null)
  const uploadControllerRef = useRef<AbortController | null>(null)
  const [geometry, setGeometry] = useState({ cols: session.cols, rows: session.rows })
  const [showExtras, setShowExtras] = useState(false)
  const [showActions, setShowActions] = useState(false)
  const [showPicker, setShowPicker] = useState(false)
  const [modifiers, setModifiers] = useState<Modifiers>(NoModifiers)
  const [showOnScreenKeys] = useState(() => needsOnScreenKeys(window))
  const [transferError, setTransferError] = useState<string | null>(null)
  const [upload, setUpload] = useState<{
    name: string
    confirmedBytes: number
    totalBytes: number
  } | null>(null)

  // Read by the xterm callbacks, which are wired once and must see what is armed
  // *now* rather than what was armed on the render that registered them.
  const modifiersRef = useRef<Modifiers>(NoModifiers)
  modifiersRef.current = modifiers

  // The area the browser is actually showing, which on iOS is not what CSS thinks.
  const [box, setBox] = useState<ViewportBox | null>(() => measureViewport(window.visualViewport))

  /**
   * Takes whatever is armed and disarms it.
   *
   * Sticky modifiers apply to exactly one keypress. Leaving Ctrl latched after it
   * has been used would turn the next letter into a control code nobody asked for —
   * and on a terminal, an unintended control code is not a typo you can see and
   * correct.
   */
  const consumeModifiers = useCallback((): Modifiers => {
    const armed = modifiersRef.current
    if (!isArmed(armed)) return NoModifiers

    modifiersRef.current = NoModifiers
    setModifiers(NoModifiers)
    return armed
  }, [])

  // Held in a ref and read by the xterm callbacks: those are wired up once, when the
  // terminal is created, and must always reach the *current* attachment rather than
  // the one that existed on the render where they were registered.
  const sendRef = useRef<(bytes: Uint8Array) => void>(() => {})

  const write = useCallback((data: Uint8Array, kind: TerminalOutputKind) => {
    const term = termRef.current
    if (term) applyOutput(term, data, kind)
  }, [])

  const attached = useAttachedSession({
    client,
    connected,
    machineId: machine.machineId,
    sessionId: session.sessionId,
    cols: geometry.cols,
    rows: geometry.rows,
    onOutput: write,
    // What the desk terminal was before the phone reshaped it. Handing it back on
    // the way out means walking away from the phone does not leave a 45-column
    // program stranded inside a wide desktop window.
    restoreOnDetach: { cols: session.cols, rows: session.rows },
  })

  sendRef.current = attached.send

  // Create the terminal exactly once. Recreating it on any dependency change would
  // throw away the screen, and the screen is the only copy of what happened here —
  // the agent holds the authoritative one and only sends it on attach.
  useEffect(() => {
    const host = hostRef.current
    if (!host) return

    const term = new Terminal({
      allowProposedApi: true,
      cursorBlink: true,
      // Deliberately small: a phone is roughly 45 columns wide at a readable size,
      // and most agent output is written for 80. Shrinking the type is what keeps
      // lines from wrapping into unreadable ribbons.
      fontSize: 12,
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
      lineHeight: 1.2,
      theme: THEME,
      // Enough to scroll back through a build, not enough to exhaust a phone's
      // memory. This is scrollback for output seen *while attached* — a snapshot
      // carries only the screen, because the agent keeps no history either.
      scrollback: 5_000,
      // The phone keyboard's Return must produce CR, which is what a PTY in
      // canonical mode expects; LF here would submit nothing at most prompts.
      convertEol: false,
    })

    const fit = new FitAddon()
    term.loadAddon(fit)
    term.loadAddon(new WebLinksAddon())
    term.open(host)
    const disposeTouchScroll = installTouchScroll(host, term, (event, details) => {
      attached.recorder.diagnostic('touch-scroll', event, details)
    })

    term.onData((data) => sendRef.current(applyModifiers(data, consumeModifiers())))
    term.onBinary((data) => sendRef.current(encodeBinary(data)))

    termRef.current = term
    fitRef.current = fit

    return () => {
      disposeTouchScroll()
      term.dispose()
      termRef.current = null
      fitRef.current = null
    }
  }, [attached.recorder, consumeModifiers])

  // Fit to the container, and tell the far end when the shape changed.
  //
  // A phone changes shape constantly: the software keyboard takes half the screen,
  // rotation swaps the axes, and the browser's URL bar slides away as you scroll. A
  // PTY that still believes it has the old geometry wraps lines in the wrong place,
  // and a full-screen program draws its interface off the edge of the screen.
  useEffect(() => {
    const host = hostRef.current
    if (!host) return

    const apply = () => {
      const fit = fitRef.current
      const term = termRef.current
      if (!fit || !term) return

      // `proposeDimensions` returns undefined while the element is hidden or has
      // zero size, which happens on the first frame and whenever iOS collapses the
      // page during a rotation. Fitting to that produces a 1×1 terminal.
      const proposed = fit.proposeDimensions()
      if (!proposed || !proposed.cols || !proposed.rows) return

      fit.fit()
      setGeometry((current) =>
        current.cols === term.cols && current.rows === term.rows
          ? current
          : { cols: term.cols, rows: term.rows },
      )
    }

    apply()

    const observer = new ResizeObserver(apply)
    observer.observe(host)

    // visualViewport is what actually moves when the software keyboard opens; the
    // window resize event does not fire for it on iOS.
    window.visualViewport?.addEventListener('resize', apply)
    window.addEventListener('orientationchange', apply)

    return () => {
      observer.disconnect()
      window.visualViewport?.removeEventListener('resize', apply)
      window.removeEventListener('orientationchange', apply)
    }
  }, [])

  // Track the visible area itself, separately from fitting the terminal into it.
  //
  // On iOS the layout viewport does not shrink when the keyboard opens — the page is
  // scrolled inside a smaller visual viewport — so a `inset-0` element keeps its full
  // height and hides its own bottom behind the keyboard. The accessory bar and the
  // last lines of output are exactly what is down there.
  useEffect(() => {
    const viewport = window.visualViewport
    if (!viewport) return

    const apply = () => {
      const next = measureViewport(viewport)
      setBox((current) => (sameViewport(current, next) ? current : next))
    }

    apply()

    viewport.addEventListener('resize', apply)
    // Scroll, not just resize: the keyboard opening moves the visual viewport
    // down inside the layout one without changing its size at all.
    viewport.addEventListener('scroll', apply)

    return () => {
      viewport.removeEventListener('resize', apply)
      viewport.removeEventListener('scroll', apply)
    }
  }, [])

  // Report geometry separately from measuring it, so a burst of resize events while
  // the keyboard animates open produces one message rather than thirty.
  const resize = attached.resize
  useEffect(() => {
    if (attached.state !== 'attached') return

    const timer = setTimeout(() => resize(geometry.cols, geometry.rows), 120)
    return () => clearTimeout(timer)
  }, [geometry.cols, geometry.rows, attached.state, resize])

  const press = useCallback(
    (key: KeyDefinition) => {
      // Ctrl+C goes down the dedicated path, not as a byte. A session wedged badly
      // enough to have stopped reading its input is exactly the session you are
      // trying to interrupt, and writing 0x03 into a pipe nobody is draining does
      // nothing at all.
      if (key.interrupt) {
        // Whatever was armed is discarded rather than applied. Nobody taps Ctrl
        // meaning to change what the interrupt key does, and this is the one key
        // that must do the same thing every time it is pressed.
        consumeModifiers()
        attached.interrupt()
      } else {
        attached.send(encodeKey(key, consumeModifiers()))
      }

      refocusTerminalIfActive(hostRef.current, termRef.current)
    },
    [attached, consumeModifiers],
  )

  /**
   * Arms or disarms a sticky modifier.
   *
   * Tapping the same one again disarms it, because the alternative — a modifier you
   * can only clear by using it — leaves the keyboard in a state the user has to type
   * their way out of.
   */
  const toggleModifier = useCallback((which: keyof Modifiers) => {
    setModifiers((current) => {
      const next = { ...current, [which]: !current[which] }
      modifiersRef.current = next
      return next
    })

    refocusTerminalIfActive(hostRef.current, termRef.current)
  }, [])

  /**
   * Types a command without sending it.
   *
   * The Return is left to the user, because half of these take an argument and the
   * other half — `/clear` foremost — discard work that cannot be recovered by
   * apologising to the model afterwards.
   */
  const type = useCallback(
    (command: CommandDefinition) => {
      attached.send(encodeText(command.text))
      refocusTerminalIfActive(hostRef.current, termRef.current)
    },
    [attached],
  )

  /**
   * Corrects the guess.
   *
   * Nothing is set locally. The request goes to the machine, which owns session
   * state and answers with a `SessionUpdated` that reaches every device — so the
   * phone in your pocket and the settings window on the desk cannot disagree about
   * what is running here.
   */
  const chooseType = useCallback(
    (next: CliType) => {
      setShowPicker(false)
      void client.setSessionType(session.sessionId, next)
      refocusTerminalIfActive(hostRef.current, termRef.current)
    },
    [client, session.sessionId],
  )

  /**
   * Opens the picker from the header.
   *
   * The sheet comes with it. The picker sits inside the sheet because what it
   * changes is the sheet's contents, and showing somebody the buttons they just
   * chose is the whole confirmation this needs.
   */
  const editType = useCallback(() => {
    setShowActions(true)
    setShowPicker(true)
  }, [])

  const pasteClipboard = useCallback(async () => {
    setTransferError(null)

    try {
      if (!navigator.clipboard?.readText) {
        throw new Error('Clipboard access is not available in this browser.')
      }

      const text = await navigator.clipboard.readText()
      if (text.length > 0 && termRef.current) pasteClipboardText(termRef.current, text)
      refocusTerminalIfActive(hostRef.current, termRef.current)
    } catch (error) {
      setTransferError(error instanceof Error ? error.message : 'The clipboard could not be read.')
    }
  }, [])

  const attachFile = useCallback(
    async (file: File) => {
      setTransferError(null)

      const controller = new AbortController()
      uploadControllerRef.current = controller
      setUpload({ name: file.name, confirmedBytes: 0, totalBytes: file.size })

      const outcome = await client.uploadTerminalFile(
        session.sessionId,
        file,
        (progress) =>
          setUpload((current) =>
            current
              ? {
                  ...current,
                  confirmedBytes: progress.confirmedBytes,
                  totalBytes: progress.totalBytes,
                }
              : current,
          ),
        controller.signal,
      )

      if (uploadControllerRef.current !== controller) return

      uploadControllerRef.current = null
      setUpload(null)

      if (outcome.cancelled) return
      if (outcome.error) {
        setTransferError(describeError(outcome.error.code, outcome.error.message))
        return
      }

      if (outcome.remotePath) {
        if (termRef.current) {
          pasteClipboardText(
            termRef.current,
            quoteTerminalPath(outcome.remotePath, session.cliType),
          )
        }
        refocusTerminalIfActive(hostRef.current, termRef.current)
      }
    },
    [client, session.cliType, session.sessionId],
  )

  useEffect(
    () => () => {
      uploadControllerRef.current?.abort()
      uploadControllerRef.current = null
    },
    [],
  )

  const catalog = catalogFor(session.cliType)

  const startTrace = useCallback(() => {
    attached.startRecording()

    const host = hostRef.current
    const term = termRef.current
    const viewport = window.visualViewport
    attached.recorder.diagnostic('touch-scroll', 'recording-started', {
      appVersion: __APP_VERSION__,
      userAgent: navigator.userAgent,
      platform: navigator.platform,
      maxTouchPoints: navigator.maxTouchPoints,
      devicePixelRatio: window.devicePixelRatio,
      screenWidth: window.screen.width,
      screenHeight: window.screen.height,
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      visualViewportWidth: viewport?.width ?? null,
      visualViewportHeight: viewport?.height ?? null,
      visualViewportOffsetTop: viewport?.offsetTop ?? null,
      hostClientWidth: host?.clientWidth ?? null,
      hostClientHeight: host?.clientHeight ?? null,
      terminalRows: term?.rows ?? null,
      terminalCols: term?.cols ?? null,
      viewportY: term?.buffer.active.viewportY ?? null,
      baseY: term?.buffer.active.baseY ?? null,
    })
  }, [attached])

  const saveTrace = useCallback(() => {
    attached.stopRecording()
    downloadTrace(
      attached.recorder.build({
        program: session.program,
        machine: machine.displayName,
        cols: geometry.cols,
        rows: geometry.rows,
      }),
    )
  }, [attached, session.program, machine.displayName, geometry.cols, geometry.rows])

  useLockHorizontalPan(screenRef)

  const tone = verdict(attached.latency.p50)

  return (
    <div
      ref={screenRef}
      className="fixed inset-x-0 top-0 z-20 flex flex-col bg-slate-950"
      style={{ height: '100dvh', ...viewportStyle(box) }}
    >
      <header className="flex items-center gap-2 border-b border-slate-800 pb-2 pl-[max(0.5rem,env(safe-area-inset-left))] pr-[max(0.5rem,env(safe-area-inset-right))] pt-[max(0.5rem,env(safe-area-inset-top))]">
        <button
          type="button"
          onClick={onClose}
          className="min-h-10 rounded-lg px-3 text-sm text-sky-400 transition active:bg-slate-800"
        >
          ‹ Back
        </button>

        <div className="min-w-0 flex-1">
          <p className="truncate text-[15px] font-semibold text-slate-100">
            {sessionLabel(session)}
          </p>
          <p className="flex items-baseline gap-1 text-xs text-slate-500">
            <span className="min-w-0 truncate">
              {machine.displayName} · {geometry.cols}×{geometry.rows}
            </span>
            <span aria-hidden="true">·</span>
            {/*
              The guess, where you are standing when you find out it is wrong. It is
              also the way to correct it: opening the sheet rather than a picker of
              its own, so there is one picker in the app and it is always in the same
              place, next to the buttons the answer decides.
            */}
            <button
              type="button"
              onClick={editType}
              aria-label={
                session.cliType === 'Generic'
                  ? 'Set what this session is running'
                  : `Running ${labelFor(session.cliType)} — change`
              }
              className="shrink-0 underline decoration-dotted underline-offset-2 transition active:text-slate-300"
            >
              {session.cliType === 'Generic' ? 'Set type' : labelFor(session.cliType)}
            </button>
          </p>
        </div>

        <StateDot state={attached.state} />

        <button
          type="button"
          onClick={attached.recording ? saveTrace : startTrace}
          aria-label={
            attached.recording ? 'Stop recording and save' : 'Record terminal diagnostics'
          }
          className={`min-h-10 rounded-lg px-3 text-sm transition active:bg-slate-800 ${
            attached.recording ? 'text-rose-400' : 'text-slate-500'
          }`}
        >
          {attached.recording ? `● ${attached.recorder.entryCount}` : '○'}
        </button>
      </header>

      {attached.state === 'failed' && attached.error ? (
        <div className="px-3 pt-3">
          <Banner
            tone="error"
            title={describeError(attached.error.code, attached.error.message)}
            action={
              <button
                type="button"
                onClick={attached.retry}
                className="min-h-10 rounded-lg border border-rose-500/40 px-4 text-sm"
              >
                Try again
              </button>
            }
          />
        </div>
      ) : null}

      {attached.state === 'reconnecting' ? (
        <div className="px-3 pt-3">
          <Banner tone="warning" title="Reconnecting">
            The connection dropped. What is on screen is from before it did — nothing
            you type will reach the machine until this clears.
          </Banner>
        </div>
      ) : null}

      {attached.state === 'closed' ? (
        <div className="px-3 pt-3">
          <Banner tone="info" title="This session has ended">
            {attached.endedWhileAway
              ? 'It was gone by the time the connection came back — the terminal was closed at the desk. The screen above is the last thing we saw.'
              : attached.exitCode === null
                ? 'The program exited.'
                : `The program exited with code ${attached.exitCode}. The screen above is the last thing it printed.`}
          </Banner>
        </div>
      ) : null}

      {attached.missedOutput ? (
        <div className="px-3 pt-3">
          <Banner
            tone="warning"
            title="Some output was missed"
            action={
              <button
                type="button"
                onClick={attached.dismissMissedOutput}
                className="min-h-10 rounded-lg border border-amber-400/40 px-4 text-sm"
              >
                Dismiss
              </button>
            }
          >
            The connection dropped and the gap could not be recovered. What is on screen
            may not be the whole story.
          </Banner>
        </div>
      ) : null}

      {/*
        Between the attach and the snapshot arriving there is a real, if short, gap:
        the agent has to reshape the console to this screen's geometry first. Saying
        so beats an unexplained blank rectangle, which reads as a failure.
      */}
      {attached.state === 'attached' && attached.lastSeq === null ? (
        <p className="px-4 pt-3 text-xs text-slate-500">Restoring the screen…</p>
      ) : null}

      <div ref={hostRef} className="min-h-0 flex-1 overflow-hidden px-1 py-1" />

      <div className="border-t border-slate-800 bg-slate-900/60 pb-[env(safe-area-inset-bottom)]">
        <input
          ref={fileInputRef}
          type="file"
          className="hidden"
          onChange={(event) => {
            const file = event.currentTarget.files?.[0]
            event.currentTarget.value = ''
            if (file) void attachFile(file)
          }}
        />

        {transferError ? (
          <div className="border-b border-slate-800 px-2 py-2">
            <Banner
              tone="error"
              title={transferError}
              action={
                <button
                  type="button"
                  onClick={() => setTransferError(null)}
                  className="min-h-10 rounded-lg border border-rose-500/40 px-3 text-sm"
                >
                  Dismiss
                </button>
              }
            />
          </div>
        ) : null}

        {upload ? (
          <div className="border-b border-slate-800 px-3 py-2" role="status">
            <div className="flex items-center gap-3">
              <div className="min-w-0 flex-1">
                <p className="truncate text-xs text-slate-300">Attaching {upload.name}</p>
                <div className="mt-1 h-1.5 overflow-hidden rounded-full bg-slate-800">
                  <div
                    className="h-full bg-sky-400 transition-[width]"
                    style={{
                      width: `${
                        upload.totalBytes === 0
                          ? 100
                          : Math.round((upload.confirmedBytes / upload.totalBytes) * 100)
                      }%`,
                    }}
                  />
                </div>
              </div>
              <button
                type="button"
                onClick={() => uploadControllerRef.current?.abort()}
                className="min-h-10 rounded-lg px-3 text-sm text-slate-400 active:bg-slate-800"
              >
                Cancel
              </button>
            </div>
          </div>
        ) : null}

        {/*
          The per-CLI sheet, above the key row rather than below it: the row is the
          one thing that must never move, because it is aimed at by muscle memory
          while looking at the terminal rather than at the keys.
        */}
        {showActions ? (
          <div className="border-b border-slate-800 px-2 py-2">
            {showOnScreenKeys && catalog.shortcuts.length > 0 ? (
              <div {...PAN_X} className="flex items-center gap-1 overflow-x-auto pb-2">
                {catalog.shortcuts.map((key) => (
                  <KeyButton key={key.name} definition={key} onPress={press} />
                ))}
              </div>
            ) : null}

            {catalog.commands.length > 0 ? (
              <div className="flex flex-wrap gap-1">
                {catalog.commands.map((command) => (
                  <button
                    key={command.text}
                    type="button"
                    aria-label={`${command.text} — ${command.description}`}
                    onPointerDown={(event) => {
                      event.preventDefault()
                      type(command)
                    }}
                    className="min-h-10 rounded-lg bg-slate-800 px-3 font-mono text-sm text-slate-200 transition active:bg-slate-700"
                  >
                    {command.text}
                  </button>
                ))}
              </div>
            ) : null}

            <div className="mt-2 flex flex-wrap items-center gap-2">
              <button
                type="button"
                aria-expanded={showPicker}
                onClick={() => setShowPicker((v) => !v)}
                className="text-[11px] text-slate-500 underline decoration-dotted underline-offset-2"
              >
                {session.cliType === 'Generic'
                  ? "We couldn't tell what this is running. Set it →"
                  : `Running ${labelFor(session.cliType)}. Not right? →`}
              </button>

              {showPicker
                ? CLI_TYPES.map((option) => (
                    <button
                      key={option}
                      type="button"
                      aria-pressed={option === session.cliType}
                      onClick={() => chooseType(option)}
                      className={`min-h-9 rounded-lg px-3 text-xs transition ${
                        option === session.cliType
                          ? 'bg-sky-400 text-slate-950'
                          : 'bg-slate-800 text-slate-300 active:bg-slate-700'
                      }`}
                    >
                      {labelFor(option)}
                    </button>
                  ))
                : null}
            </div>
          </div>
        ) : null}

        <div {...PAN_X} className="flex items-center gap-1 overflow-x-auto px-2 py-2">
          {showOnScreenKeys ? (
            <>
              <ModifierButton
                label="Ctrl"
                armed={modifiers.ctrl}
                onToggle={() => toggleModifier('ctrl')}
              />
              <ModifierButton
                label="Alt"
                armed={modifiers.alt}
                onToggle={() => toggleModifier('alt')}
              />

              <span className="mx-1 h-6 w-px shrink-0 bg-slate-700" aria-hidden="true" />

              {KeyBarLayout.map((key) => (
                <KeyButton key={key.name} definition={key} onPress={press} />
              ))}
            </>
          ) : null}

          <button
            type="button"
            aria-expanded={showActions}
            onClick={() => setShowActions((v) => !v)}
            aria-label={`Shortcuts and commands for ${labelFor(session.cliType)}`}
            className={`min-h-10 shrink-0 rounded-lg px-3 text-sm transition active:bg-slate-700 ${
              showActions ? 'bg-slate-700 text-slate-100' : 'text-slate-400'
            }`}
          >
            ⌘
          </button>

          <button
            type="button"
            onClick={() => void pasteClipboard()}
            disabled={attached.state !== 'attached' || upload !== null}
            className="min-h-10 shrink-0 rounded-lg px-3 text-sm text-slate-300 transition enabled:active:bg-slate-700 disabled:text-slate-600"
          >
            Paste
          </button>

          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            disabled={attached.state !== 'attached' || upload !== null}
            className="min-h-10 shrink-0 rounded-lg px-3 text-sm text-slate-300 transition enabled:active:bg-slate-700 disabled:text-slate-600"
          >
            Attach
          </button>

          {showOnScreenKeys ? (
            <button
              type="button"
              onClick={() => setShowExtras((v) => !v)}
              aria-label="More keys"
              className="min-h-10 shrink-0 rounded-lg px-3 font-mono text-sm text-slate-400 transition active:bg-slate-700"
            >
              {showExtras ? '×' : '···'}
            </button>
          ) : null}
        </div>

        {showOnScreenKeys && showExtras ? (
          <div className="flex items-center gap-1 border-t border-slate-800 px-2 py-2">
            {ExtraKeys.map((key) => (
              <KeyButton key={key.name} definition={key} onPress={press} />
            ))}
          </div>
        ) : null}

        <p className="px-3 pb-2 text-[11px] text-slate-600">
          {attached.latency.p50 === null ? (
            'Latency: measuring…'
          ) : (
            <>
              Latency{' '}
              <span
                className={
                  tone === 'good'
                    ? 'text-emerald-400'
                    : tone === 'fair'
                      ? 'text-amber-400'
                      : 'text-rose-400'
                }
              >
                {Math.round(attached.latency.p50)} ms
              </span>{' '}
              median, {Math.round(attached.latency.p95 ?? 0)} ms p95, over{' '}
              {attached.latency.count} keystrokes
            </>
          )}
        </p>
      </div>
    </div>
  )
}

/**
 * A sticky modifier.
 *
 * Announced as a toggle rather than a button, because that is what it is: a screen
 * reader saying "Ctrl, pressed" is the only way somebody who cannot see the highlight
 * finds out that the next letter they type will be a control code.
 */
function ModifierButton({
  label,
  armed,
  onToggle,
}: {
  label: string
  armed: boolean
  onToggle(): void
}) {
  return (
    <button
      type="button"
      aria-pressed={armed}
      aria-label={`${label} — applies to the next key`}
      onPointerDown={(event) => {
        event.preventDefault()
        onToggle()
      }}
      className={`min-h-10 shrink-0 rounded-lg px-3 text-sm font-semibold transition ${
        armed
          ? 'bg-sky-400 text-slate-950'
          : 'bg-slate-800 text-slate-300 active:bg-slate-700'
      }`}
    >
      {label}
    </button>
  )
}

function KeyButton({
  definition,
  onPress,
}: {
  definition: KeyDefinition
  onPress(key: KeyDefinition): void
}) {
  return (
    <button
      type="button"
      aria-label={definition.name}
      // Pointer-down rather than click: a terminal key should fire the instant the
      // thumb lands, and click waits for the release plus the browser's tap
      // disambiguation.
      onPointerDown={(event) => {
        event.preventDefault()
        onPress(definition)
      }}
      className={`min-h-10 min-w-11 shrink-0 rounded-lg px-3 font-mono text-sm transition active:bg-slate-700 ${
        definition.emphasis
          ? 'bg-rose-500/15 text-rose-300'
          : 'bg-slate-800 text-slate-200'
      }`}
    >
      {definition.label}
    </button>
  )
}

function StateDot({ state }: { state: string }) {
  const colour =
    state === 'attached'
      ? 'bg-emerald-400'
      : state === 'attaching' || state === 'reconnecting'
        ? 'bg-amber-400'
        : state === 'closed'
          ? 'bg-slate-600'
          : 'bg-rose-500'

  return <span className={`size-2 shrink-0 rounded-full ${colour}`} aria-label={state} />
}
