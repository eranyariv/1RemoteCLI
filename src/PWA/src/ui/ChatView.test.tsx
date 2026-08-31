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
  sendChatPrompt = vi.fn(async () => null as HubError | null)
  cancelChatAttachment = vi.fn(async () => {})
  respondChatPermission = vi.fn(async () => null as HubError | null)

  uploadChatAttachment = vi.fn(
    async (
      _sessionId: string,
      _attachmentId: string,
      file: File,
      onProgress: (progress: { confirmedBytes: number; totalBytes: number }) => void,
    ) => {
      onProgress({ confirmedBytes: file.size, totalBytes: file.size })
      return { ready: true, error: null as HubError | null, cancelled: false }
    },
  )

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
  chatCapabilities: null,
  suggestedProjectId: null,
  suggestedProjectMoves: 0,
  chatState: 'Ready',
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
    planTurnId: null,
    planRevision: 0,
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
    let ids = 0
    vi.stubGlobal('crypto', {
      ...globalThis.crypto,
      randomUUID: () => `attachment-${++ids}`,
    })
    vi.stubGlobal('URL', {
      ...globalThis.URL,
      createObjectURL: vi.fn(() => 'blob:preview'),
      revokeObjectURL: vi.fn(),
    })
  })

  afterEach(cleanup)

  it('reserves space between the composer divider and Send button for voice mode', () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    const form = screen.getByRole('button', { name: 'Send' }).closest('form')
    expect(form?.className).toContain('pt-[4.5rem]')
    expect(screen.getByRole('button', { name: 'Send' }).className).not.toContain('mr-16')
  })

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

  it('blocks prompts while Copilot Desktop owns the session and retries the handoff', async () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={{ ...session, chatState: 'Busy' }}
        onClose={() => {}}
      />,
    )

    expect(
      screen.getByText(/This chat is open in Copilot Desktop or another Copilot process/),
    ).toBeTruthy()
    expect((screen.getByLabelText('Message agent') as HTMLTextAreaElement).disabled).toBe(true)
    expect((screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement).disabled).toBe(true)

    fireEvent.click(screen.getByRole('button', { name: 'Retry handoff' }))
    await waitFor(() => expect(relay.attach).toHaveBeenCalledTimes(2))
    expect(relay.sendChatMessage).not.toHaveBeenCalled()
  })

  it('explains that a ready Copilot chat is a sequential handoff', () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={session}
        onClose={() => {}}
      />,
    )

    expect(screen.getByText(/Copilot Desktop does not live-sync with this view/)).toBeTruthy()
    expect((screen.getByLabelText('Message agent') as HTMLTextAreaElement).disabled).toBe(false)
  })

  it('offers the same handoff retry for Claude Code chats', () => {
    render(
      <ChatView
        client={relay.client}
        connected
        machine={machine}
        session={{ ...session, cliType: 'ClaudeCode', chatState: 'Busy' }}
        onClose={() => {}}
      />,
    )

    expect(screen.getByText(/This chat is open in Claude Code or another Claude Code process/)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Retry handoff' })).toBeTruthy()
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

  it('keeps active operations collapsed in Compact until explicitly opened', () => {
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
            eventId: 'tool-active',
            kind: 'ToolCall',
            text: 'Verbose deployment output',
            title: 'Deploy corrected version',
            status: 'pending',
            toolKind: 'terminal',
          }),
          chatEvent({
            eventId: 'plan-active',
            kind: 'Plan',
            text: '',
            planEntries: [
              {
                content: 'Wait for deployment',
                priority: 'medium',
                status: 'in_progress',
                taskId: 'wait',
                parentTaskId: null,
                depth: 0,
              },
            ],
          }),
        ],
      })
    })

    expect(screen.getByText('Verbose deployment output')).toBeTruthy()
    expect(screen.getByText('Wait for deployment')).toBeTruthy()

    fireEvent.click(screen.getByRole('button', { name: 'Compact' }))

    const tool = screen.getByRole('button', { name: /Deploy corrected version.*pending/ })
    const plan = screen.getByRole('button', { name: /Plan.*0\/1/ })
    expect(tool.getAttribute('aria-expanded')).toBe('false')
    expect(plan.getAttribute('aria-expanded')).toBe('false')
    expect(screen.queryByText('Verbose deployment output')).toBeNull()
    expect(screen.queryByText('Wait for deployment')).toBeNull()

    fireEvent.click(tool)
    expect(screen.getByText('Verbose deployment output')).toBeTruthy()
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
              {
                content: 'Read settings',
                priority: 'high',
                status: 'completed',
                taskId: 'read',
                parentTaskId: null,
                depth: 0,
              },
              {
                content: 'Edit settings',
                priority: 'medium',
                status: 'in_progress',
                taskId: 'edit',
                parentTaskId: null,
                depth: 0,
              },
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

  describe('attachments', () => {
    const withCapabilities = (image: boolean, embeddedContext: boolean): SessionInfo => ({
      ...session,
      chatCapabilities: { image, embeddedContext },
    })

    function view(target: SessionInfo) {
      return render(
        <ChatView
          client={relay.client}
          connected
          machine={machine}
          session={target}
          onClose={() => {}}
        />,
      )
    }

    function pick(testId: string, ...files: File[]) {
      const input = screen.getByTestId(testId) as HTMLInputElement
      Object.defineProperty(input, 'files', { value: files, configurable: true })
      fireEvent.change(input)
    }

    function image(name = 'receipt.png', size = 1024): File {
      const file = new File([new Uint8Array(1)], name, { type: 'image/png' })
      Object.defineProperty(file, 'size', { value: size })
      return file
    }

    it('offers no attachment controls when the agent advertised none', () => {
      view(session)

      expect(screen.queryByLabelText('Attach a file')).toBeNull()
      expect(screen.queryByLabelText('Attach a photo')).toBeNull()
      expect(screen.queryByLabelText('Take a photo')).toBeNull()
    })

    it('offers only the picker the negotiated capabilities justify', () => {
      const imagesOnly = view(withCapabilities(true, false))

      expect(screen.getByLabelText('Attach a photo')).toBeTruthy()
      expect(screen.getByLabelText('Take a photo')).toBeTruthy()
      expect(screen.queryByLabelText('Attach a file')).toBeNull()

      imagesOnly.unmount()
      view(withCapabilities(false, true))

      expect(screen.getByLabelText('Attach a file')).toBeTruthy()
      expect(screen.queryByLabelText('Take a photo')).toBeNull()
    })

    it('stages a photo at selection time, previews it, and sends it with the text', async () => {
      view(withCapabilities(true, true))

      pick('chat-image-input', image())

      await waitFor(() => expect(relay.uploadChatAttachment).toHaveBeenCalledTimes(1))
      expect(screen.getByText('receipt.png')).toBeTruthy()
      await waitFor(() => expect(screen.getByText(/ready/)).toBeTruthy())
      expect(screen.getByAltText('')).toBeTruthy()

      fireEvent.change(screen.getByLabelText('Message agent'), {
        target: { value: '  what does this say?  ' },
      })
      fireEvent.click(screen.getByRole('button', { name: 'Send' }))

      await waitFor(() =>
        expect(relay.sendChatPrompt).toHaveBeenCalledWith('chat-1', 'what does this say?', [
          'attachment-1',
        ]),
      )

      // Cleared only once the machine accepted it.
      await waitFor(() => expect(screen.queryByText('receipt.png')).toBeNull())
      expect((screen.getByLabelText('Message agent') as HTMLTextAreaElement).value).toBe('')
      expect(relay.sendChatMessage).not.toHaveBeenCalled()
    })

    it('sends an attachment with no text at all', async () => {
      view(withCapabilities(true, true))
      pick('chat-camera-input', image('photo.jpg'))

      await waitFor(() => expect(screen.getByText(/ready/)).toBeTruthy())
      fireEvent.click(screen.getByRole('button', { name: 'Send' }))

      await waitFor(() =>
        expect(relay.sendChatPrompt).toHaveBeenCalledWith('chat-1', '', ['attachment-1']),
      )
    })

    it('keeps sending text-only prompts through the message method', async () => {
      view(withCapabilities(true, true))

      fireEvent.change(screen.getByLabelText('Message agent'), { target: { value: 'continue' } })
      fireEvent.click(screen.getByRole('button', { name: 'Send' }))

      await waitFor(() => expect(relay.sendChatMessage).toHaveBeenCalledWith('chat-1', 'continue'))
      expect(relay.sendChatPrompt).not.toHaveBeenCalled()
    })

    it('refuses a type this agent cannot carry, without uploading it', async () => {
      view(withCapabilities(true, false))

      pick('chat-image-input', new File([new Uint8Array(1)], 'notes.txt', { type: 'text/plain' }))

      await waitFor(() =>
        expect(screen.getByRole('alert').textContent).toContain('does not accept file attachments'),
      )
      expect(relay.uploadChatAttachment).not.toHaveBeenCalled()
    })

    it('refuses a fifth attachment and one that would blow the aggregate limit', async () => {
      view(withCapabilities(true, true))

      pick('chat-image-input', image('a.png'), image('b.png'), image('c.png'), image('d.png'))
      await waitFor(() => expect(relay.uploadChatAttachment).toHaveBeenCalledTimes(4))

      pick('chat-image-input', image('e.png'))
      await waitFor(() =>
        expect(screen.getByRole('alert').textContent).toContain('at most 4 attachments'),
      )
      expect(relay.uploadChatAttachment).toHaveBeenCalledTimes(4)

      cleanup()
      view(withCapabilities(true, true))
      pick(
        'chat-image-input',
        image('big-1.png', 4 * 1024 * 1024),
        image('big-2.png', 4 * 1024 * 1024),
        image('big-3.png', 4 * 1024 * 1024),
      )

      await waitFor(() =>
        expect(screen.getByRole('alert').textContent).toContain('All attachments on one prompt'),
      )
    })

    it('keeps a batch rejection visible when a later file is accepted', async () => {
      view(withCapabilities(true, true))

      pick(
        'chat-image-input',
        image('too-big.png', 6 * 1024 * 1024),
        image('accepted.png'),
      )

      await waitFor(() =>
        expect(screen.getByRole('alert').textContent).toContain('larger than 5.0 MB'),
      )
      expect(screen.getByText('accepted.png')).toBeTruthy()
      expect(relay.uploadChatAttachment).toHaveBeenCalledTimes(1)
    })

    it('holds Send while an upload is in flight and shows what it is doing', async () => {
      let finish: ((outcome: unknown) => void) | null = null
      relay.uploadChatAttachment.mockImplementationOnce(
        (_sessionId: string, _attachmentId: string, file: File, onProgress) => {
          onProgress({ confirmedBytes: file.size / 2, totalBytes: file.size })
          return new Promise((resolve) => {
            finish = resolve as (outcome: unknown) => void
          }) as never
        },
      )

      view(withCapabilities(true, true))
      pick('chat-image-input', image())

      await waitFor(() =>
        expect((screen.getByRole('button', { name: 'Attaching…' }) as HTMLButtonElement).disabled).toBe(
          true,
        ),
      )
      expect(screen.getByText(/50%/)).toBeTruthy()

      act(() => finish!({ ready: true, error: null, cancelled: false }))
      await waitFor(() =>
        expect((screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement).disabled).toBe(
          false,
        ),
      )
    })

    it('shows a staging failure against the file it belongs to, and holds Send until it is removed', async () => {
      relay.uploadChatAttachment.mockResolvedValueOnce({
        ready: false,
        error: { code: 'attachment_failed', message: 'The disk is full.', sessionId: 'chat-1' },
        cancelled: false,
      })

      view(withCapabilities(true, true))
      pick('chat-image-input', image())

      await waitFor(() => expect(screen.getByText('The disk is full.')).toBeTruthy())
      fireEvent.change(screen.getByLabelText('Message agent'), { target: { value: 'carry on' } })
      expect((screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement).disabled).toBe(true)

      fireEvent.click(screen.getByRole('button', { name: 'Remove receipt.png' }))
      await waitFor(() =>
        expect((screen.getByRole('button', { name: 'Send' }) as HTMLButtonElement).disabled).toBe(
          false,
        ),
      )
    })

    it('removes an attachment on request, deleting the staged bytes and its preview', async () => {
      view(withCapabilities(true, true))
      pick('chat-image-input', image())

      await waitFor(() => expect(screen.getByText(/ready/)).toBeTruthy())
      fireEvent.click(screen.getByRole('button', { name: 'Remove receipt.png' }))

      expect(relay.cancelChatAttachment).toHaveBeenCalledWith('chat-1', 'attachment-1')
      expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:preview')
      expect(screen.queryByText('receipt.png')).toBeNull()
    })

    it('keeps the draft and the selection when the machine refuses the prompt', async () => {
      relay.sendChatPrompt.mockResolvedValueOnce({
        code: 'attachment_unsupported',
        message: 'This agent does not accept images.',
        sessionId: 'chat-1',
      })

      view(withCapabilities(true, true))
      pick('chat-image-input', image())
      await waitFor(() => expect(screen.getByText(/ready/)).toBeTruthy())

      fireEvent.change(screen.getByLabelText('Message agent'), { target: { value: 'look' } })
      fireEvent.click(screen.getByRole('button', { name: 'Send' }))

      await waitFor(() =>
        expect(screen.getByRole('alert').textContent).toContain('does not accept images'),
      )
      expect(screen.getByText('receipt.png')).toBeTruthy()
      expect((screen.getByLabelText('Message agent') as HTMLTextAreaElement).value).toBe('look')
    })

    it('does not accept or erase a new attachment while a prompt acknowledgement is pending', async () => {
      let accept: ((error: HubError | null) => void) | null = null
      relay.sendChatPrompt.mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            accept = resolve
          }),
      )

      view(withCapabilities(true, true))
      pick('chat-image-input', image('sent.png'))
      await waitFor(() => expect(screen.getByText(/ready/)).toBeTruthy())

      fireEvent.click(screen.getByRole('button', { name: 'Send' }))
      await waitFor(() =>
        expect((screen.getByLabelText('Attach a file') as HTMLButtonElement).disabled).toBe(true),
      )

      pick('chat-image-input', image('late.png'))
      expect(screen.getByRole('alert').textContent).toContain('current message')
      expect(relay.uploadChatAttachment).toHaveBeenCalledTimes(1)

      act(() => accept!(null))
      await waitFor(() => expect(screen.queryByText('sent.png')).toBeNull())
      expect(screen.queryByText('late.png')).toBeNull()
    })

    it('cancels everything still staged when the chat is closed', async () => {
      const open = view(withCapabilities(true, true))
      pick('chat-image-input', image())
      await waitFor(() => expect(screen.getByText(/ready/)).toBeTruthy())

      open.unmount()

      expect(relay.cancelChatAttachment).toHaveBeenCalledWith('chat-1', 'attachment-1')
      expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:preview')
    })

    it('shows what was attached in the user bubble, without any bytes', () => {
      view(withCapabilities(true, true))

      act(() => {
        relay.emit({
          sessionId: 'chat-1',
          seq: 8,
          kind: 'Snapshot',
          events: [
            chatEvent({
              eventId: 'prompt-1',
              kind: 'UserMessage',
              text: 'what does this say?',
              content: [
                contentBlock({
                  type: 'resource_link',
                  uri: 'attachment://1remotecli/attachment-1/receipt.png',
                  name: 'receipt.png',
                  mimeType: 'image/png',
                  size: 2048,
                }),
              ],
            }),
          ],
        })
      })

      const bubble = screen.getByText('what does this say?').closest('article')!
      expect(bubble.textContent).toContain('receipt.png')
      expect(bubble.textContent).toContain('2 KB')
      expect(bubble.querySelector('img')).toBeNull()
    })
  })
})
