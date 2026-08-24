import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type {
  ChatContentBlock,
  ChatEvent,
  ChatTranscript,
  HubError,
  MachineInfo,
  SessionInfo,
} from '../protocol/wire'
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
  projectId: null,
}

function chatEvent(
  item: Pick<ChatEvent, 'eventId' | 'kind' | 'text'> & Partial<ChatEvent>,
): ChatEvent {
  return {
    title: null,
    status: null,
    toolKind: null,
    permissionRequestId: null,
    options: [],
    content: [],
    locations: [],
    planEntries: [],
    rawInputJson: null,
    rawOutputJson: null,
    ...item,
  }
}

function contentBlock(
  item: Pick<ChatContentBlock, 'type'> & Partial<ChatContentBlock>,
): ChatContentBlock {
  return {
    text: null,
    path: null,
    oldText: null,
    newText: null,
    terminalId: null,
    mimeType: null,
    data: null,
    uri: null,
    name: null,
    title: null,
    description: null,
    size: null,
    rawJson: null,
    ...item,
  }
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
          chatEvent({
            eventId: 'answer',
            kind: 'AgentMessage',
            text: 'Working',
          }),
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
          chatEvent({
            eventId: 'answer',
            kind: 'AgentMessage',
            text: 'Done',
          }),
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

  it('finishes loading when the transcript is empty', () => {
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
        seq: 1,
        kind: 'Snapshot',
        events: [],
      })
    })

    expect(screen.queryByText('Loading the transcript…')).toBeNull()
    expect(screen.getByText('No messages yet.')).toBeTruthy()
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
          chatEvent({
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
          }),
        ],
      })
    })

    fireEvent.click(screen.getByRole('button', { name: 'Allow once' }))

    expect(relay.respondChatPermission).toHaveBeenCalledWith('chat-1', 'req-1', 'yes')
    expect(screen.getByText('approval needed')).toBeTruthy()
  })

  it('renders an elicitation choice menu and forwards the selected answer', async () => {
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
        seq: 4,
        kind: 'Delta',
        events: [
          chatEvent({
            eventId: 'elicitation:req-2',
            kind: 'Permission',
            text: 'Which database should I use?',
            title: 'Database',
            status: 'pending',
            toolKind: 'ask-user-1',
            permissionRequestId: 'req-2',
            options: [
              { optionId: 'postgres', name: 'PostgreSQL', kind: 'select' },
              { optionId: 'sqlite', name: 'SQLite', kind: 'select' },
            ],
          }),
        ],
      })
    })

    fireEvent.click(screen.getByRole('button', { name: 'SQLite' }))
    expect(relay.respondChatPermission).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Submit answer' }))

    await waitFor(() =>
      expect(relay.respondChatPermission).toHaveBeenCalledWith('chat-1', 'req-2', 'sqlite'),
    )
    expect(screen.getByText('Which database should I use?')).toBeTruthy()
  })

  it('switches tool activity between compact, summary, and full detail', () => {
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
        seq: 5,
        kind: 'Snapshot',
        events: [
          chatEvent({
            eventId: 'tool-1',
            kind: 'ToolCall',
            text: 'A very long tool result',
            title: 'Inspect files',
            status: 'completed',
            toolKind: 'read',
            permissionRequestId: null,
            options: [],
          }),
        ],
      })
    })

    expect(screen.getByText('Inspect files')).toBeTruthy()
    expect(screen.queryByText('A very long tool result')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Full' }))
    expect(screen.getByText('A very long tool result')).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Compact' }))
    expect(screen.queryByText('Inspect files')).toBeNull()
  })

  it('renders AionUi-style thoughts, plans, locations, and diffs', () => {
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
        seq: 6,
        kind: 'Snapshot',
        events: [
          chatEvent({
            eventId: 'thought-1',
            kind: 'AgentThought',
            text: 'Inspecting the settings',
          }),
          chatEvent({
            eventId: 'plan',
            kind: 'Plan',
            text: '',
            planEntries: [
              { content: 'Read settings', priority: 'high', status: 'completed' },
              { content: 'Edit settings', priority: 'medium', status: 'in_progress' },
            ],
          }),
          chatEvent({
            eventId: 'tool-2',
            kind: 'ToolCall',
            text: 'Changed settings.json',
            title: 'Edit settings',
            status: 'completed',
            toolKind: 'edit',
            locations: [{ path: 'C:\\repo\\settings.json', line: 7 }],
            content: [
              contentBlock({
                type: 'diff',
                path: 'C:\\repo\\settings.json',
                oldText: '{"enabled":false}',
                newText: '{"enabled":true}',
              }),
            ],
          }),
        ],
      })
    })

    expect(screen.queryByText('Inspecting the settings')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Thought' }))
    expect(screen.getByText('Inspecting the settings')).toBeTruthy()

    expect(screen.getByText('1/2')).toBeTruthy()
    expect(screen.getByText('Read settings')).toBeTruthy()
    expect(screen.getAllByText('Edit settings')).toHaveLength(2)

    fireEvent.click(screen.getByRole('button', { name: /Edit settings.*completed/ }))
    expect(screen.getByText('settings.json:7')).toBeTruthy()
    expect(screen.getByText('Before')).toBeTruthy()
    expect(screen.getByText('After')).toBeTruthy()
  })

  it('renders user prompts distinctly and agent tables as semantic Markdown', () => {
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
        seq: 7,
        kind: 'Snapshot',
        events: [
          chatEvent({
            eventId: 'prompt-1',
            kind: 'UserMessage',
            text: 'Do a full issues sweep',
          }),
          chatEvent({
            eventId: 'answer-1',
            kind: 'AgentMessage',
            text: [
              '| Priority | Recommendation |',
              '| --- | --- |',
              '| 1 | **Build the plan view** |',
            ].join('\n'),
          }),
        ],
      })
    })

    const prompt = screen.getByText('Do a full issues sweep').closest('article')
    expect(prompt?.className).toContain('ml-auto')
    expect(prompt?.className).toContain('bg-slate-800')
    expect(screen.getByRole('table')).toBeTruthy()
    expect(screen.getByRole('columnheader', { name: 'Priority' })).toBeTruthy()
    expect(screen.getByRole('cell', { name: 'Build the plan view' }).querySelector('strong')).toBeTruthy()
  })
})
