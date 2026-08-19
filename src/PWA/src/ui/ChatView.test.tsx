import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ChatTranscript, HubError, MachineInfo, SessionInfo } from '../protocol/wire'
import type { RelayClient } from '../relay/client'
import { ChatView } from './ChatView'

type Handler = (transcript: ChatTranscript) => void

class FakeRelay {
  private handler: Handler | null = null

  attach = vi.fn(async () => null as HubError | null)
  detach = vi.fn(async () => null as HubError | null)
  sendChatMessage = vi.fn(async () => null as HubError | null)
  respondChatPermission = vi.fn(async () => null as HubError | null)

  on(event: string, handler: Handler): () => void {
    if (event === 'chatTranscript') this.handler = handler
    return () => {
      if (this.handler === handler) this.handler = null
    }
  }

  emit(transcript: ChatTranscript): void {
    this.handler?.(transcript)
  }

  get client(): RelayClient {
    return this as unknown as RelayClient
  }
}

const machine: MachineInfo = {
  machineId: 'machine-1',
  displayName: 'Desk',
  os: 'Windows',
  agentVersion: '0.14',
  online: true,
  sessions: [],
}

const session: SessionInfo = {
  sessionId: 'chat-1',
  program: 'GitHub Copilot',
  args: [],
  cwd: 'C:\\repo',
  cols: 0,
  rows: 0,
  startedAt: new Date(),
  displayName: 'Issue 3',
  awaitingInput: false,
  cliType: 'CopilotCli',
  customName: null,
  pinned: false,
  kind: 'AgentChat',
}

describe('ChatView', () => {
  let relay: FakeRelay

  beforeEach(() => {
    relay = new FakeRelay()
    Element.prototype.scrollIntoView = vi.fn()
  })

  afterEach(cleanup)

  it('attaches, renders a snapshot, and replaces delta events by id', async () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    await waitFor(() => expect(relay.attach).toHaveBeenCalledWith('machine-1', 'chat-1', 0, 0))

    act(() => {
      relay.emit({
        sessionId: 'chat-1',
        seq: 1,
        kind: 'Snapshot',
        events: [
          {
            eventId: 'answer',
            kind: 'AgentMessage',
            text: 'Working',
            title: null,
            status: null,
            toolKind: null,
            permissionRequestId: null,
            options: [],
          },
        ],
      })

    })

    expect(screen.getByText('Working')).toBeTruthy()

    act(() => {
      relay.emit({
        sessionId: 'chat-1',
        seq: 2,
        kind: 'Delta',
        events: [
          {
            eventId: 'answer',
            kind: 'AgentMessage',
            text: 'Done',
            title: null,
            status: null,
            toolKind: null,
            permissionRequestId: null,
            options: [],
          },
        ],
      })
    })

    expect(screen.queryByText('Working')).toBeNull()
    expect(screen.getByText('Done')).toBeTruthy()
  })

  it('reattaches after the relay reconnects', async () => {
    const view = render(
      <ChatView
        client={relay.client}
        connected={false}
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    expect(relay.attach).not.toHaveBeenCalled()

    view.rerender(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    await waitFor(() => expect(relay.attach).toHaveBeenCalledTimes(1))
  })

  it('sends trimmed messages and clears the composer', async () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    const input = screen.getByLabelText('Message agent')
    fireEvent.change(input, { target: { value: '  continue  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    await waitFor(() =>
      expect(relay.sendChatMessage).toHaveBeenCalledWith('chat-1', 'continue'),
    )
    expect((input as HTMLTextAreaElement).value).toBe('')
  })

  it('renders every permission option and forwards the selected one', async () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    act(() => {
      relay.emit({
        sessionId: 'chat-1',
        seq: 3,
        kind: 'Delta',
        events: [
          {
            eventId: 'permission:req-1',
            kind: 'Permission',
            text: 'Approval required',
            title: 'Run tests',
            status: 'pending',
            toolKind: 'tool-1',
            permissionRequestId: 'req-1',
            options: [
              { optionId: 'yes', name: 'Allow once', kind: 'allow_once' },
              { optionId: 'no', name: 'Deny', kind: 'reject_once' },
            ],
          },
        ],
      })
    })

    fireEvent.click(screen.getByRole('button', { name: 'Allow once' }))

    expect(relay.respondChatPermission).toHaveBeenCalledWith('chat-1', 'req-1', 'yes')
    expect(screen.getByText('approval needed')).toBeTruthy()
  })
})
