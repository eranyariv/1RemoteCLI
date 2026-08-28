import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type {
  ChatEvent,
  ChatTranscript,
  HubError,
  MachineInfo,
  ProjectInfo,
  SessionInfo,
  TerminalOutput,
} from '../protocol/wire'
import type { RelayClient, RelayStatus } from '../relay/client'
import type { SpeechProvider } from '../voice/azureSpeech'
import { VoiceMode } from './VoiceMode'

type ClientEvent = 'chatTranscript' | 'terminalOutput'

class FakeSpeechProvider implements SpeechProvider {
  readonly speak = vi.fn(async () => {})
  readonly listen = vi.fn(() => {
    return new Promise<string>((resolve, reject) => {
      this.pending = { resolve, reject }
    })
  })
  readonly dispose = vi.fn()
  readonly cancel = vi.fn(() => {
    this.pending?.reject(new Error('cancelled'))
    this.pending = null
  })

  private pending: {
    resolve(value: string): void
    reject(error: Error): void
  } | null = null

  answer(value: string): void {
    const pending = this.pending
    if (!pending) throw new Error('Voice mode is not listening.')
    this.pending = null
    pending.resolve(value)
  }
}

class FakeRelay {
  readonly attach = vi.fn(
    async (_machineId: string, _sessionId: string, _cols: number, _rows: number) =>
      null as HubError | null,
  )
  readonly sendChatMessage = vi.fn(
    async (_sessionId: string, _text: string) => null as HubError | null,
  )
  readonly sendInput = vi.fn(
    async (_sessionId: string, _data: Uint8Array) => null as HubError | null,
  )
  readonly respondChatPermission = vi.fn(
    async (_sessionId: string, _requestId: string, _optionId: string) =>
      null as HubError | null,
  )

  private readonly handlers = new Map<ClientEvent, Set<(value: never) => void>>()

  on(event: ClientEvent, handler: (value: never) => void): () => void {
    const listeners = this.handlers.get(event) ?? new Set()
    listeners.add(handler)
    this.handlers.set(event, listeners)
    return () => listeners.delete(handler)
  }

  emit(event: 'chatTranscript', value: ChatTranscript): void
  emit(event: 'terminalOutput', value: TerminalOutput): void
  emit(event: ClientEvent, value: ChatTranscript | TerminalOutput): void {
    for (const handler of this.handlers.get(event) ?? []) handler(value as never)
  }

  get client(): RelayClient {
    return this as unknown as RelayClient
  }
}

const general: ProjectInfo = {
  projectId: 'general',
  name: 'General',
  description: null,
  siteUrl: null,
  repoUrl: null,
  isGeneral: true,
  iconVersion: 0,
  createdAt: new Date(),
}

function session(kind: SessionInfo['kind'], id: string, name: string): SessionInfo {
  return {
    sessionId: id,
    program: kind === 'AgentChat' ? 'GitHub Copilot' : 'pwsh',
    args: [],
    cwd: 'C:\\repo',
    cols: 80,
    rows: 24,
    startedAt: new Date(),
    displayName: name,
    awaitingInput: false,
    cliType: kind === 'AgentChat' ? 'CopilotCli' : 'PowerShell',
    customName: null,
    pinned: false,
    kind,
    projectId: null,
    chatCapabilities: null,
  }
}

const chat = session('AgentChat', 'chat-1', 'Issue chat')
const terminal = session('Terminal', 'terminal-1', 'Build shell')
const machine: MachineInfo = {
  machineId: 'machine-1',
  displayName: 'Desk',
  os: 'Windows',
  agentVersion: '0.40',
  online: true,
  sessions: [chat, terminal],
}

function permissionEvent(): ChatEvent {
  return {
    eventId: 'permission-1',
    kind: 'Permission',
    text: 'Allow the edit?',
    title: 'Edit src/App.tsx',
    status: 'pending',
    toolKind: null,
    permissionRequestId: 'request-1',
    options: [
      { optionId: 'allow', name: 'Allow once', kind: 'select' },
      { optionId: 'reject', name: 'Reject', kind: 'select' },
    ],
    content: [],
    locations: [],
    planEntries: [],
    rawInputJson: null,
    rawOutputJson: null,
  }
}

async function answer(provider: FakeSpeechProvider, listenNumber: number, value: string) {
  await waitFor(() => expect(provider.listen).toHaveBeenCalledTimes(listenNumber))
  await act(async () => provider.answer(value))
}

function setup(selectedSession?: SessionInfo, initialRelayStatus: RelayStatus = 'connected') {
  const provider = new FakeSpeechProvider()
  const relay = new FakeRelay()
  const onSelectProject = vi.fn()
  const onOpenSession = vi.fn()
  const onCloseSession = vi.fn()

  if (selectedSession) {
    localStorage.setItem(
      '1remote.voice.location.v1',
      JSON.stringify({
        projectId: general.projectId,
        machineId: machine.machineId,
        sessionId: selectedSession.sessionId,
      }),
    )
  }

  const props = {
    client: relay.client,
    projects: [general],
    machines: [machine],
    selectedProjectId: null,
    onSelectProject,
    onOpenSession,
    onCloseSession,
    createProvider: () => provider,
  }

  const rendered = render(
    <VoiceMode
      client={relay.client}
      relayStatus={initialRelayStatus}
      projects={[general]}
      machines={[machine]}
      selectedProjectId={null}
      onSelectProject={onSelectProject}
      onOpenSession={onOpenSession}
      onCloseSession={onCloseSession}
      createProvider={() => provider}
    />,
  )
  fireEvent.click(screen.getByRole('button', { name: 'Start voice mode' }))

  return {
    provider,
    relay,
    onSelectProject,
    onOpenSession,
    onCloseSession,
    setRelayStatus(status: RelayStatus) {
      rendered.rerender(<VoiceMode {...props} relayStatus={status} />)
    },
  }
}

describe('VoiceMode', () => {
  beforeEach(() => {
    localStorage.clear()
    Object.defineProperty(window, 'isSecureContext', { configurable: true, value: true })
    Object.defineProperty(navigator, 'mediaDevices', {
      configurable: true,
      value: { getUserMedia: vi.fn() },
    })
  })

  afterEach(cleanup)

  it('shows an icon-only microphone button with an accessible name', () => {
    const provider = new FakeSpeechProvider()
    const relay = new FakeRelay()

    render(
      <VoiceMode
        client={relay.client}
        relayStatus="connected"
        projects={[general]}
        machines={[machine]}
        selectedProjectId={null}
        onSelectProject={() => {}}
        onOpenSession={() => {}}
        onCloseSession={() => {}}
        createProvider={() => provider}
      />,
    )

    const button = screen.getByRole('button', { name: 'Start voice mode' })
    expect(button.textContent).toBe('')
    expect(button.querySelector('svg[aria-hidden="true"]')).not.toBeNull()
    expect(button.className).toContain('bottom-[max(1rem,env(safe-area-inset-bottom))]')
  })

  it('selects an ACP session and exchanges three turns without touch input', async () => {
    const view = setup()

    await answer(view.provider, 1, 'General')
    await answer(view.provider, 2, 'Issue chat')
    await waitFor(() => expect(view.onOpenSession).toHaveBeenCalledWith(machine, chat))

    await answer(view.provider, 3, 'First message')
    await waitFor(() => expect(view.relay.sendChatMessage).toHaveBeenCalledTimes(1))
    await answer(view.provider, 4, 'Second message')
    await waitFor(() => expect(view.relay.sendChatMessage).toHaveBeenCalledTimes(2))
    await answer(view.provider, 5, 'Third message')
    await waitFor(() => expect(view.relay.sendChatMessage).toHaveBeenCalledTimes(3))

    await answer(view.provider, 6, 'back to sessions')
    await waitFor(() => expect(view.onCloseSession).toHaveBeenCalled())
    expect(view.relay.sendChatMessage).toHaveBeenCalledTimes(3)
  })

  it('requires confirmation before sending a risky terminal command', async () => {
    const view = setup()

    await answer(view.provider, 1, 'General')
    await answer(view.provider, 2, 'Build shell')
    await answer(view.provider, 3, 'git reset --hard')

    await waitFor(() =>
      expect(view.provider.speak).toHaveBeenCalledWith(expect.stringContaining('Say yes to send it')),
    )
    expect(view.relay.sendInput).not.toHaveBeenCalled()

    await answer(view.provider, 4, 'yes')
    await waitFor(() => expect(view.relay.sendInput).toHaveBeenCalledTimes(1))
    expect(new TextDecoder().decode(view.relay.sendInput.mock.calls[0][1])).toBe(
      'git reset --hard\r',
    )

    await answer(view.provider, 5, 'back to projects')
    await waitFor(() => expect(view.onSelectProject).toHaveBeenLastCalledWith(null))
    expect(view.relay.sendInput).toHaveBeenCalledTimes(1)
  })

  it('interrupts listening for an ACP permission and accepts a spoken option', async () => {
    const view = setup(chat)
    await waitFor(() => expect(view.provider.listen).toHaveBeenCalledTimes(1))

    act(() => {
      view.relay.emit('chatTranscript', {
        sessionId: chat.sessionId,
        seq: 1,
        kind: 'Delta',
        events: [permissionEvent()],
      })
    })

    await answer(view.provider, 2, 'Allow once')
    await waitFor(() =>
      expect(view.relay.respondChatPermission).toHaveBeenCalledWith(
        chat.sessionId,
        'request-1',
        'allow',
      ),
    )
  })

  it('preserves the selected session across reconnect and deduplicates a retried utterance', async () => {
    const view = setup(terminal)
    await answer(view.provider, 1, 'npm test')
    await waitFor(() => expect(view.relay.sendInput).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(view.provider.listen).toHaveBeenCalledTimes(2))

    act(() => view.setRelayStatus('reconnecting'))
    await waitFor(() =>
      expect(view.provider.speak).toHaveBeenCalledWith(
        'Disconnected from the relay. Voice navigation is paused.',
      ),
    )
    expect(screen.getByText('Disconnected')).toBeTruthy()

    act(() => view.setRelayStatus('connected'))
    await answer(view.provider, 3, 'npm test')
    await waitFor(() =>
      expect(view.provider.speak).toHaveBeenCalledWith(
        'That utterance was already handled. Please continue.',
      ),
    )
    expect(view.relay.sendInput).toHaveBeenCalledTimes(1)
    expect(view.onOpenSession).toHaveBeenCalledWith(machine, terminal)
  })

  it('does not misreport the initial relay handshake as a disconnect', async () => {
    const view = setup(undefined, 'connecting')
    await waitFor(() => expect(view.provider.listen).toHaveBeenCalledTimes(1))

    expect(view.provider.speak).not.toHaveBeenCalledWith(
      'Disconnected from the relay. Voice navigation is paused.',
    )

    act(() => view.setRelayStatus('connected'))
    expect(view.provider.listen).toHaveBeenCalledTimes(1)
    await answer(view.provider, 1, 'General')
    await waitFor(() => expect(view.onSelectProject).toHaveBeenCalledWith(general.projectId))
  })
})
