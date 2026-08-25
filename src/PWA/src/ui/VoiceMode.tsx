import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from 'react'

import type { MachineInfo, ProjectInfo, SessionInfo, ChatEvent } from '../protocol/wire'
import type { RelayClient, RelayStatus } from '../relay/client'
import { sessionLabel } from '../relay/machines'
import { GENERAL_PROJECT_ID, projectStats } from '../relay/projects'
import { encodeText } from '../terminal/keys'
import { AzureSpeechProvider, type SpeechProvider } from '../voice/azureSpeech'
import {
  chatEventSpeech,
  RecentUtterances,
  speechChunk,
  summarizeTerminal,
  terminalText,
} from '../voice/output'
import {
  matchSpokenChoice,
  numberedChoices,
  routeVoiceUtterance,
  type SpokenChoice,
} from '../voice/routing'
import {
  initialVoiceState,
  loadVoiceLocation,
  saveVoiceLocation,
  voiceReducer,
  type VoiceAction,
  type VoiceState,
} from '../voice/state'
import { terminalRisk } from '../voice/terminalSafety'
import { receive, startOfStream, type StreamPosition } from '../terminal/stream'

interface SessionChoiceValue {
  machine: MachineInfo
  session: SessionInfo
}

interface PendingApproval {
  event: ChatEvent
  sessionId: string
}

export interface VoiceModeProps {
  client: RelayClient
  relayStatus: RelayStatus
  projects: readonly ProjectInfo[]
  machines: readonly MachineInfo[]
  selectedProjectId: string | null
  onSelectProject(projectId: string | null): void
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
  onCloseSession(): void
  createProvider?: () => SpeechProvider
}

function projectChoices(
  projects: readonly ProjectInfo[],
  machines: readonly MachineInfo[],
): SpokenChoice<ProjectInfo>[] {
  return projects.map((project) => {
    const stats = projectStats(machines, project.projectId)
    const status =
      stats.sessionCount === 0
        ? 'no active sessions'
        : `${stats.sessionCount} active ${stats.sessionCount === 1 ? 'session' : 'sessions'}${
            stats.awaitingInputCount > 0 ? `, ${stats.awaitingInputCount} waiting` : ''
          }`

    return {
      value: project,
      label: `${project.name}, ${status}`,
      aliases: [project.name],
    }
  })
}

function sessionChoices(
  projectId: string | null,
  machines: readonly MachineInfo[],
): SpokenChoice<SessionChoiceValue>[] {
  if (!projectId) return []

  return machines.flatMap((machine) =>
    machine.sessions
      .filter((session) => (session.projectId ?? GENERAL_PROJECT_ID) === projectId)
      .map((session) => {
        const name = sessionLabel(session)
        const kind = session.kind === 'AgentChat' ? 'agent chat' : 'terminal'
        const waiting = session.awaitingInput ? ', waiting for input' : ''

        return {
          value: { machine, session },
          label: `${name}, ${kind}${waiting}, on ${machine.displayName}`,
          aliases: [name, `${name} on ${machine.displayName}`],
        }
      }),
  )
}

function approvalChoices(approval: PendingApproval): SpokenChoice<string>[] {
  return approval.event.options.map((option) => {
    const normalized = option.name.toLocaleLowerCase()
    const aliases = [option.name]
    if (/\b(?:allow|approve|accept|yes|continue|proceed)\b/.test(normalized)) aliases.push('yes')
    if (/\b(?:deny|decline|reject|no|cancel)\b/.test(normalized)) aliases.push('no')
    return { value: option.optionId, label: option.name, aliases }
  })
}

function statusLabel(state: VoiceState): string {
  if (state.activity === 'error' && state.error) return state.error
  if (state.activity === 'idle') return 'Voice mode is off'
  return `${state.activity[0].toUpperCase()}${state.activity.slice(1)}`
}

export function VoiceMode({
  client,
  relayStatus,
  projects,
  machines,
  selectedProjectId,
  onSelectProject,
  onOpenSession,
  onCloseSession,
  createProvider = () => new AzureSpeechProvider(),
}: VoiceModeProps) {
  const [state, reactDispatch] = useReducer(voiceReducer, initialVoiceState)
  const stateRef = useRef(state)
  const [heard, setHeard] = useState('')
  const [spoken, setSpoken] = useState('')
  const providerRef = useRef<SpeechProvider | null>(null)
  const operationRef = useRef(0)
  const handleRef = useRef<(text: string) => Promise<void>>(async () => {})
  const recentUtterances = useRef(new RecentUtterances())
  const lastSpokenRef = useRef('')
  const detailRef = useRef({ text: '', offset: 0 })
  const pendingApprovalRef = useRef<PendingApproval | null>(null)
  const terminalPositionRef = useRef<StreamPosition>(startOfStream)
  const terminalBufferRef = useRef('')
  const terminalTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const chatTimersRef = useRef(new Map<string, ReturnType<typeof setTimeout>>())
  const chatTextRef = useRef(new Map<string, string>())
  const disconnectedRef = useRef(false)
  const everConnectedRef = useRef(false)

  const apply = useCallback((action: VoiceAction) => {
    stateRef.current = voiceReducer(stateRef.current, action)
    reactDispatch(action)
  }, [])

  const choicesForProjects = useMemo(
    () => projectChoices(projects, machines),
    [projects, machines],
  )
  const choicesForSessions = useMemo(
    () => sessionChoices(state.projectId, machines),
    [state.projectId, machines],
  )
  const projectChoicesRef = useRef(choicesForProjects)
  const sessionChoicesRef = useRef(choicesForSessions)
  projectChoicesRef.current = choicesForProjects
  sessionChoicesRef.current = choicesForSessions

  const projectsPrompt = useCallback(
    () =>
      numberedChoices(
        'Available projects.',
        projectChoicesRef.current,
        'There are no available projects. Say repeat, or stop voice mode.',
      ),
    [],
  )

  const sessionsPrompt = useCallback(
    () =>
      numberedChoices(
        'Active sessions.',
        sessionChoicesRef.current,
        'This project has no active sessions. Say back to projects, repeat, or stop voice mode.',
      ),
    [],
  )

  const conversationPrompt = useCallback(() => {
    const current = stateRef.current
    const selected = machines
      .find((machine) => machine.machineId === current.machineId)
      ?.sessions.find((session) => session.sessionId === current.sessionId)

    if (!selected) return 'That session is no longer available. Say back to sessions.'
    return `Listening for ${sessionLabel(selected)}. Say a message or terminal command.`
  }, [machines])

  const promptForCurrentLevel = useCallback(() => {
    const current = stateRef.current
    if (current.level === 'projects') return projectsPrompt()
    if (current.level === 'sessions') return sessionsPrompt()
    return conversationPrompt()
  }, [conversationPrompt, projectsPrompt, sessionsPrompt])

  const runCycle = useCallback(
    async (requestedSpeech: string) => {
      const provider = providerRef.current
      if (!provider || !stateRef.current.active) return

      const operation = ++operationRef.current
      provider.cancel()
      const chunk = speechChunk(requestedSpeech)
      const message = chunk.text || 'Please try again.'
      if (chunk.nextOffset !== null) detailRef.current = { text: requestedSpeech, offset: chunk.nextOffset }

      lastSpokenRef.current = message
      setSpoken(message)
      apply({ type: 'activity', activity: 'speaking' })

      try {
        await provider.speak(message)
        if (operation !== operationRef.current || !stateRef.current.active) return

        if (relayStatus !== 'connected' && stateRef.current.level === 'conversation') {
          apply({ type: 'activity', activity: 'disconnected' })
          return
        }

        apply({ type: 'activity', activity: 'listening' })
        const transcript = await provider.listen()
        if (operation !== operationRef.current || !stateRef.current.active) return

        if (!transcript) {
          void runCycle(`I did not hear anything. ${promptForCurrentLevel()}`)
          return
        }
        if (recentUtterances.current.isDuplicate(transcript)) {
          void runCycle('That utterance was already handled. Please continue.')
          return
        }

        setHeard(transcript)
        apply({ type: 'activity', activity: 'thinking' })
        await handleRef.current(transcript)
      } catch (error) {
        if (operation !== operationRef.current || !stateRef.current.active) return
        apply({
          type: 'error',
          message: error instanceof Error ? error.message : 'Voice mode failed.',
        })
      }
    },
    [apply, promptForCurrentLevel, relayStatus],
  )

  const stopVoice = useCallback(
    async (announce: boolean) => {
      operationRef.current += 1
      const provider = providerRef.current
      provider?.cancel()

      if (announce && provider) {
        try {
          setSpoken('Voice mode stopped.')
          apply({ type: 'activity', activity: 'speaking' })
          await provider.speak('Voice mode stopped.')
        } catch {
          // Stop remains immediate and reliable even when the provider has failed.
        }
      }

      provider?.dispose()
      providerRef.current = null
      disconnectedRef.current = false
      everConnectedRef.current = false
      apply({ type: 'stop' })
      setHeard('')
      setSpoken('')
      pendingApprovalRef.current = null
    },
    [apply],
  )

  const goToProjects = useCallback(() => {
    pendingApprovalRef.current = null
    terminalPositionRef.current = startOfStream
    onCloseSession()
    onSelectProject(null)
    saveVoiceLocation(localStorage, null)
    apply({ type: 'back-to-projects' })
    void runCycle(projectsPrompt())
  }, [apply, onCloseSession, onSelectProject, projectsPrompt, runCycle])

  const goToSessions = useCallback(() => {
    const current = stateRef.current
    if (!current.projectId) {
      goToProjects()
      return
    }

    pendingApprovalRef.current = null
    terminalPositionRef.current = startOfStream
    onCloseSession()
    onSelectProject(current.projectId)
    saveVoiceLocation(localStorage, {
      projectId: current.projectId,
      machineId: null,
      sessionId: null,
    })
    apply({ type: 'back-to-sessions' })
    void runCycle(sessionsPrompt())
  }, [apply, goToProjects, onCloseSession, onSelectProject, runCycle, sessionsPrompt])

  const cancelCurrent = useCallback(async () => {
    const current = stateRef.current
    if (current.confirmation) {
      apply({ type: 'clear-confirmation' })
      void runCycle(`Cancelled. ${conversationPrompt()}`)
      return
    }

    const approval = pendingApprovalRef.current
    if (approval) {
      const reject = approvalChoices(approval).find((choice) =>
        choice.aliases?.some((alias) => /^(?:no|cancel)$/i.test(alias)),
      )
      if (reject) {
        pendingApprovalRef.current = null
        const error = await client.respondChatPermission(
          approval.sessionId,
          approval.event.permissionRequestId ?? '',
          reject.value,
        )
        void runCycle(error ? `The cancellation failed. ${error.message}` : 'Request cancelled.')
        return
      }
    }

    void runCycle(`Cancelled. ${promptForCurrentLevel()}`)
  }, [apply, client, conversationPrompt, promptForCurrentLevel, runCycle])

  const hearMore = useCallback(() => {
    const detail = detailRef.current
    if (!detail.text || detail.offset >= detail.text.length) {
      void runCycle('There is no more detail to read.')
      return
    }

    const chunk = speechChunk(detail.text, detail.offset)
    detailRef.current.offset = chunk.nextOffset ?? detail.text.length
    void runCycle(chunk.text || 'There is no more detail to read.')
  }, [runCycle])

  const handleProjectSelection = useCallback(
    (text: string) => {
      const result = matchSpokenChoice(text, projectChoicesRef.current)
      if (result.kind === 'none') {
        void runCycle(`I could not match that project. ${projectsPrompt()}`)
        return
      }
      if (result.kind === 'ambiguous') {
        void runCycle(
          numberedChoices('That name is ambiguous. Choose one.', result.choices, projectsPrompt()),
        )
        return
      }

      const project = result.choice.value
      onSelectProject(project.projectId)
      saveVoiceLocation(localStorage, {
        projectId: project.projectId,
        machineId: null,
        sessionId: null,
      })
      apply({ type: 'select-project', projectId: project.projectId })
      void runCycle(
        numberedChoices(
          `${project.name}. Active sessions.`,
          sessionChoices(project.projectId, machines),
          `${project.name} has no active sessions. Say back to projects.`,
        ),
      )
    },
    [apply, machines, onSelectProject, projectsPrompt, runCycle],
  )

  const handleSessionSelection = useCallback(
    async (text: string) => {
      const result = matchSpokenChoice(text, sessionChoicesRef.current)
      if (result.kind === 'none') {
        void runCycle(`I could not match that session. ${sessionsPrompt()}`)
        return
      }
      if (result.kind === 'ambiguous') {
        void runCycle(
          numberedChoices('That name is ambiguous. Choose one.', result.choices, sessionsPrompt()),
        )
        return
      }

      const { machine, session } = result.choice.value
      const error = await client.attach(
        machine.machineId,
        session.sessionId,
        session.cols,
        session.rows,
      )
      if (error) {
        void runCycle(`That session could not be opened. ${error.message}`)
        return
      }

      pendingApprovalRef.current = null
      terminalPositionRef.current = startOfStream
      terminalBufferRef.current = ''
      onOpenSession(machine, session)
      saveVoiceLocation(localStorage, {
        projectId: stateRef.current.projectId ?? GENERAL_PROJECT_ID,
        machineId: machine.machineId,
        sessionId: session.sessionId,
      })
      apply({ type: 'select-session', machineId: machine.machineId, sessionId: session.sessionId })
      void runCycle(
        `Opened ${sessionLabel(session)}, ${session.kind === 'AgentChat' ? 'agent chat' : 'terminal'}. ${conversationPrompt()}`,
      )
    },
    [apply, client, conversationPrompt, onOpenSession, runCycle, sessionsPrompt],
  )

  const handleApproval = useCallback(
    async (text: string, approval: PendingApproval) => {
      const result = matchSpokenChoice(text, approvalChoices(approval))
      if (result.kind === 'none') {
        void runCycle(`I could not match that answer. ${chatEventSpeech(approval.event)}`)
        return
      }
      if (result.kind === 'ambiguous') {
        void runCycle(
          numberedChoices(
            'That answer is ambiguous. Choose one.',
            result.choices,
            chatEventSpeech(approval.event),
          ),
        )
        return
      }

      pendingApprovalRef.current = null
      const error = await client.respondChatPermission(
        approval.sessionId,
        approval.event.permissionRequestId ?? '',
        result.choice.value,
      )
      void runCycle(error ? `The answer was not accepted. ${error.message}` : 'Answer accepted.')
    },
    [client, runCycle],
  )

  const handleConversation = useCallback(
    async (text: string, yesNo: 'yes' | 'no' | null) => {
      const current = stateRef.current
      const machine = machines.find((item) => item.machineId === current.machineId)
      const session = machine?.sessions.find((item) => item.sessionId === current.sessionId)
      if (!machine || !session) {
        void runCycle('That session is no longer available. Say back to sessions.')
        return
      }
      if (relayStatus !== 'connected') {
        apply({ type: 'activity', activity: 'disconnected' })
        return
      }

      const approval = pendingApprovalRef.current
      if (approval) {
        await handleApproval(yesNo ?? text, approval)
        return
      }

      if (current.confirmation) {
        if (yesNo === 'no') {
          apply({ type: 'clear-confirmation' })
          void runCycle('Command cancelled.')
          return
        }
        if (yesNo !== 'yes') {
          void runCycle(`Say yes to send the command, or no to cancel it. ${current.confirmation.reason}.`)
          return
        }

        const command = current.confirmation.text
        apply({ type: 'clear-confirmation' })
        const error = await client.sendInput(session.sessionId, encodeText(`${command}\r`))
        void runCycle(error ? `The command was not sent. ${error.message}` : 'Command sent.')
        return
      }

      if (session.kind === 'AgentChat') {
        const error = await client.sendChatMessage(session.sessionId, text)
        void runCycle(error ? `The message was not sent. ${error.message}` : 'Message sent.')
        return
      }

      const risk = terminalRisk(text)
      if (risk.risky) {
        apply({
          type: 'confirm-terminal',
          text,
          reason: risk.reason ?? 'it may have unintended effects',
        })
        void runCycle(
          `Confirm terminal command: ${text}. ${risk.reason ?? 'It may have unintended effects'}. Say yes to send it, or no to cancel.`,
        )
        return
      }

      const error = await client.sendInput(session.sessionId, encodeText(`${text}\r`))
      void runCycle(error ? `The command was not sent. ${error.message}` : 'Command sent.')
    },
    [apply, client, handleApproval, machines, relayStatus, runCycle],
  )

  const handleUtterance = useCallback(
    async (text: string) => {
      const intent = routeVoiceUtterance(text)
      if (intent.kind === 'stop') {
        await stopVoice(true)
        return
      }
      if (intent.kind === 'back-projects') {
        goToProjects()
        return
      }
      if (intent.kind === 'back-sessions') {
        goToSessions()
        return
      }
      if (intent.kind === 'repeat') {
        void runCycle(lastSpokenRef.current || promptForCurrentLevel())
        return
      }
      if (intent.kind === 'cancel') {
        await cancelCurrent()
        return
      }
      if (intent.kind === 'more') {
        hearMore()
        return
      }

      const current = stateRef.current
      if (current.level === 'projects') {
        handleProjectSelection(text)
      } else if (current.level === 'sessions') {
        await handleSessionSelection(text)
      } else {
        await handleConversation(
          intent.kind === 'content' ? intent.text : text,
          intent.kind === 'yes' || intent.kind === 'no' ? intent.kind : null,
        )
      }
    },
    [
      cancelCurrent,
      goToProjects,
      goToSessions,
      handleConversation,
      handleProjectSelection,
      handleSessionSelection,
      hearMore,
      promptForCurrentLevel,
      runCycle,
      stopVoice,
    ],
  )
  handleRef.current = handleUtterance

  const startVoice = useCallback(() => {
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
      apply({ type: 'start' })
      apply({
        type: 'error',
        message: 'Voice mode needs microphone access in a secure browser context.',
      })
      return
    }

    providerRef.current?.dispose()
    providerRef.current = createProvider()
    disconnectedRef.current = false
    everConnectedRef.current = relayStatus === 'connected'
    const stored = loadVoiceLocation(localStorage)
    const restoredProject = stored
      ? projects.find((project) => project.projectId === stored.projectId)
      : undefined
    const restoredMachine = stored
      ? machines.find((machine) => machine.machineId === stored.machineId)
      : undefined
    const restoredSession = restoredMachine?.sessions.find(
      (session) => session.sessionId === stored?.sessionId,
    )

    if (restoredProject && restoredMachine && restoredSession) {
      onSelectProject(restoredProject.projectId)
      onOpenSession(restoredMachine, restoredSession)
      apply({
        type: 'start',
        level: 'conversation',
        projectId: restoredProject.projectId,
        machineId: restoredMachine.machineId,
        sessionId: restoredSession.sessionId,
      })
      void runCycle(`Resuming ${sessionLabel(restoredSession)}. ${conversationPrompt()}`)
      return
    }

    const projectId =
      (selectedProjectId && projects.some((project) => project.projectId === selectedProjectId)
        ? selectedProjectId
        : restoredProject?.projectId) ?? null
    if (projectId) {
      onSelectProject(projectId)
      apply({ type: 'start', level: 'sessions', projectId })
      void runCycle(sessionsPrompt())
      return
    }

    onCloseSession()
    onSelectProject(null)
    apply({ type: 'start' })
    void runCycle(projectsPrompt())
  }, [
    apply,
    conversationPrompt,
    createProvider,
    machines,
    onCloseSession,
    onOpenSession,
    onSelectProject,
    projects,
    projectsPrompt,
    relayStatus,
    runCycle,
    selectedProjectId,
    sessionsPrompt,
  ])

  const toggleMute = useCallback(async () => {
    if (stateRef.current.activity === 'muted') {
      void runCycle(`Microphone on. ${promptForCurrentLevel()}`)
      return
    }

    operationRef.current += 1
    const provider = providerRef.current
    provider?.cancel()
    try {
      await provider?.speak('Microphone muted.')
    } catch {
      // The visible muted state remains authoritative if audio output failed.
    }
    apply({ type: 'activity', activity: 'muted' })
  }, [apply, promptForCurrentLevel, runCycle])

  useEffect(() => {
    const offTerminal = client.on('terminalOutput', (output) => {
      const current = stateRef.current
      if (!current.active || current.level !== 'conversation' || output.sessionId !== current.sessionId) {
        return
      }

      const step = receive(terminalPositionRef.current, output)
      terminalPositionRef.current = step.position
      if (!step.apply) return

      const text = terminalText(output.data)
      if (!text) return
      terminalBufferRef.current = `${terminalBufferRef.current} ${text}`.slice(-8_000)
      if (terminalTimerRef.current) clearTimeout(terminalTimerRef.current)
      terminalTimerRef.current = setTimeout(() => {
        const detail = terminalBufferRef.current
        terminalBufferRef.current = ''
        detailRef.current = { text: detail, offset: 0 }
        const summary = summarizeTerminal(detail)
        if (summary) void runCycle(`${summary}. Say more detail to hear more.`)
      }, 650)
    })

    const offChat = client.on('chatTranscript', (transcript) => {
      const current = stateRef.current
      if (!current.active || current.level !== 'conversation' || transcript.sessionId !== current.sessionId) {
        return
      }

      for (const event of transcript.events) {
        if (event.kind === 'Permission' && event.status === 'pending') {
          if (pendingApprovalRef.current?.event.eventId === event.eventId) continue
          pendingApprovalRef.current = { event, sessionId: transcript.sessionId }
          const message = chatEventSpeech(event)
          detailRef.current = { text: message, offset: 0 }
          if (message) void runCycle(message)
          continue
        }
        if (event.kind === 'Permission' && pendingApprovalRef.current?.event.eventId === event.eventId) {
          pendingApprovalRef.current = null
        }
        if (event.kind !== 'AgentMessage' || transcript.kind === 'Snapshot') continue

        const message = chatEventSpeech(event)
        if (!message || chatTextRef.current.get(event.eventId) === message) continue
        chatTextRef.current.set(event.eventId, message)
        const prior = chatTimersRef.current.get(event.eventId)
        if (prior) clearTimeout(prior)
        chatTimersRef.current.set(
          event.eventId,
          setTimeout(() => {
            chatTimersRef.current.delete(event.eventId)
            detailRef.current = { text: message, offset: 0 }
            void runCycle(`${summarizeTerminal(message, 650)}. Say more detail to hear more.`)
          }, 650),
        )
      }
    })

    return () => {
      offTerminal()
      offChat()
    }
  }, [client, runCycle])

  useEffect(() => {
    if (!state.active) return

    if (
      relayStatus !== 'connected' &&
      everConnectedRef.current &&
      !disconnectedRef.current
    ) {
      disconnectedRef.current = true
      operationRef.current += 1
      providerRef.current?.cancel()
      apply({ type: 'activity', activity: 'disconnected' })
      setSpoken('Disconnected from the relay. Voice navigation is paused.')
      void providerRef.current?.speak('Disconnected from the relay. Voice navigation is paused.').catch(() => {})
    } else if (relayStatus === 'connected') {
      const wasDisconnected = disconnectedRef.current
      const wasPausedBeforeInitialConnection = stateRef.current.activity === 'disconnected'
      everConnectedRef.current = true
      disconnectedRef.current = false
      if (wasDisconnected || wasPausedBeforeInitialConnection) {
        void runCycle(
          `${wasDisconnected ? 'Reconnected' : 'Connected'}. ${promptForCurrentLevel()}`,
        )
      }
    }
  }, [apply, promptForCurrentLevel, relayStatus, runCycle, state.active])

  useEffect(
    () => () => {
      operationRef.current += 1
      providerRef.current?.dispose()
      if (terminalTimerRef.current) clearTimeout(terminalTimerRef.current)
      for (const timer of chatTimersRef.current.values()) clearTimeout(timer)
      chatTimersRef.current.clear()
    },
    [],
  )

  if (!state.active) {
    return (
      <button
        type="button"
        onClick={startVoice}
        aria-label="Start voice mode"
        className="fixed bottom-[max(1rem,env(safe-area-inset-bottom))] right-[max(1rem,env(safe-area-inset-right))] z-40 min-h-12 rounded-full border border-sky-400/50 bg-sky-500 px-5 text-sm font-semibold text-slate-950 shadow-lg shadow-sky-950/50 transition active:bg-sky-300"
      >
        Voice mode
      </button>
    )
  }

  return (
    <aside
      aria-label="Voice mode"
      className="fixed inset-x-3 bottom-[max(0.75rem,env(safe-area-inset-bottom))] z-40 mx-auto max-w-lg rounded-2xl border border-sky-500/40 bg-slate-900/95 p-3 text-slate-100 shadow-2xl backdrop-blur"
    >
      <div className="flex items-center gap-3">
        <span
          aria-hidden
          className={`size-3 shrink-0 rounded-full ${
            state.activity === 'listening'
              ? 'animate-pulse bg-emerald-400'
              : state.activity === 'speaking'
                ? 'animate-pulse bg-sky-400'
                : state.activity === 'error' || state.activity === 'disconnected'
                  ? 'bg-amber-400'
                  : state.activity === 'muted'
                    ? 'bg-slate-500'
                    : 'bg-violet-400'
          }`}
        />
        <div className="min-w-0 flex-1" aria-live="polite">
          <p className="text-sm font-semibold">{statusLabel(state)}</p>
          <p className="truncate text-xs text-slate-400">
            {state.level === 'conversation'
              ? 'Conversation'
              : state.level === 'sessions'
                ? 'Session selection'
                : 'Project selection'}
          </p>
        </div>
        <button
          type="button"
          onClick={() => void stopVoice(false)}
          className="min-h-10 rounded-lg border border-rose-500/40 px-3 text-sm text-rose-200 active:bg-rose-500/20"
        >
          Stop
        </button>
      </div>

      {heard ? <p className="mt-2 line-clamp-2 text-xs text-slate-400">You: {heard}</p> : null}
      {spoken ? <p className="mt-1 line-clamp-2 text-xs text-slate-300">Voice: {spoken}</p> : null}

      <div className="mt-3 flex flex-wrap gap-2">
        <button
          type="button"
          onClick={() => void runCycle(lastSpokenRef.current || promptForCurrentLevel())}
          className="min-h-9 rounded-lg bg-slate-800 px-3 text-xs active:bg-slate-700"
        >
          Repeat
        </button>
        <button
          type="button"
          onClick={() => void toggleMute()}
          className="min-h-9 rounded-lg bg-slate-800 px-3 text-xs active:bg-slate-700"
        >
          {state.activity === 'muted' ? 'Unmute' : 'Mute'}
        </button>
        {state.level !== 'projects' ? (
          <button
            type="button"
            onClick={state.level === 'conversation' ? goToSessions : goToProjects}
            className="min-h-9 rounded-lg bg-slate-800 px-3 text-xs active:bg-slate-700"
          >
            {state.level === 'conversation' ? 'Back to sessions' : 'Back to projects'}
          </button>
        ) : null}
        {state.activity === 'error' ? (
          <button
            type="button"
            onClick={() => void runCycle(promptForCurrentLevel())}
            className="min-h-9 rounded-lg bg-amber-500/20 px-3 text-xs text-amber-200 active:bg-amber-500/30"
          >
            Retry
          </button>
        ) : null}
      </div>
    </aside>
  )
}
