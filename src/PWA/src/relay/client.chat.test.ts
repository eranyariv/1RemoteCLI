import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorCodes } from '../protocol/errors'
import { Server } from '../protocol/methods'
import { CHAT_ATTACHMENT_CHUNK_BYTES, MAX_CHAT_ATTACHMENT_BYTES } from '../chat/attachment'

const invoke = vi.fn<(method: string, argument?: unknown) => Promise<unknown>>()
const getAccessToken = vi.fn(async () => 'token' as string | null)

vi.mock('../auth/impl', () => ({ auth: { getAccessToken: () => getAccessToken() } }))

vi.mock('@microsoft/signalr', () => {
  class HubConnectionBuilder {
    withUrl() {
      return this
    }
    withHubProtocol() {
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      return {
        start: async () => {},
        invoke,
        stop: async () => {},
        state: 'Connected',
        on: () => {},
        onclose: () => {},
        onreconnecting: () => {},
        onreconnected: () => {},
      }
    }
  }

  return {
    HubConnectionBuilder,
    HubConnectionState: { Connected: 'Connected', Disconnected: 'Disconnected' },
    LogLevel: { Information: 1, Warning: 2 },
  }
})

vi.mock('@microsoft/signalr-protocol-msgpack', () => ({ MessagePackHubProtocol: class {} }))

const { RelayClient } = await import('./client')

const attachmentId = '6cbb0f4d-2a41-4cf6-8b4a-0a26f2b71f61'

async function connected() {
  const client = new RelayClient('http://example.invalid/hub')
  await client.start()
  return client
}

describe('chat attachment staging', () => {
  let totalBytes = 0

  beforeEach(() => {
    invoke.mockReset()
    getAccessToken.mockClear()
    totalBytes = 0

    invoke.mockImplementation(async (method, argument) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null

      const request = argument as unknown[]
      if (method === Server.BeginChatAttachment) {
        totalBytes = request[4] as number
        return [attachmentId, 0, totalBytes, false, null, null]
      }
      if (method === Server.UploadChatAttachmentChunk) {
        const offset = request[2] as number
        const confirmed = offset + (request[3] as Uint8Array).byteLength
        return [attachmentId, confirmed, totalBytes, confirmed === totalBytes, null, null]
      }
      if (method === Server.CancelChatAttachment) {
        return [attachmentId, 0, totalBytes, false, ErrorCodes.AttachmentCancelled, 'Removed']
      }
      if (method === Server.SendChatPrompt) return [true, null, null]

      throw new Error(`Unexpected method ${method}`)
    })
  })

  it('chunks sequentially and only reports agent-confirmed progress', async () => {
    const client = await connected()
    const bytes = new Uint8Array(CHAT_ATTACHMENT_CHUNK_BYTES + 9).fill(0x42)
    const progress: number[] = []

    const outcome = await client.uploadChatAttachment(
      'chat-1',
      attachmentId,
      new File([bytes], 'receipt.png', { type: 'image/png' }),
      (update) => progress.push(update.confirmedBytes),
    )

    expect(outcome).toEqual({ ready: true, error: null, cancelled: false })
    expect(progress).toEqual([0, CHAT_ATTACHMENT_CHUNK_BYTES, bytes.byteLength])

    const begin = invoke.mock.calls.find(([method]) => method === Server.BeginChatAttachment)!
    expect((begin[1] as unknown[])[3]).toBe('image/png')

    const chunks = invoke.mock.calls.filter(([method]) => method === Server.UploadChatAttachmentChunk)
    expect(chunks).toHaveLength(2)
    expect((chunks[0][1] as unknown[])[2]).toBe(0)
    expect((chunks[1][1] as unknown[])[2]).toBe(CHAT_ATTACHMENT_CHUNK_BYTES)
  })

  it('never asks for a machine path back', async () => {
    const client = await connected()

    const outcome = await client.uploadChatAttachment(
      'chat-1',
      attachmentId,
      new File([new Uint8Array(4)], 'notes.txt', { type: 'text/plain' }),
      () => {},
    )

    expect(Object.keys(outcome)).toEqual(['ready', 'error', 'cancelled'])
  })

  it('rejects an oversized attachment before sending any bytes', async () => {
    const client = await connected()
    const file = { name: 'huge.png', type: 'image/png', size: MAX_CHAT_ATTACHMENT_BYTES + 1 } as File

    const outcome = await client.uploadChatAttachment('chat-1', attachmentId, file, () => {})

    expect(outcome.error?.code).toBe(ErrorCodes.AttachmentTooLarge)
    expect(invoke.mock.calls.some(([method]) => method === Server.BeginChatAttachment)).toBe(false)
  })

  it('removes the half-staged bytes when the user cancels mid-upload', async () => {
    const client = await connected()
    const controller = new AbortController()

    const outcome = await client.uploadChatAttachment(
      'chat-1',
      attachmentId,
      new File([new Uint8Array(128)], 'notes.txt', { type: 'text/plain' }),
      () => controller.abort(),
      controller.signal,
    )

    expect(outcome.cancelled).toBe(true)
    expect(invoke.mock.calls.some(([method]) => method === Server.CancelChatAttachment)).toBe(true)
  })

  it("surfaces a staging refusal with the machine's own reason", async () => {
    const client = await connected()
    invoke.mockImplementation(async (method) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null
      if (method === Server.BeginChatAttachment) {
        return [
          attachmentId,
          0,
          1,
          false,
          ErrorCodes.AttachmentBudgetExceeded,
          'A prompt carries at most 4 attachments.',
        ]
      }

      throw new Error(`Unexpected method ${method}`)
    })

    const outcome = await client.uploadChatAttachment(
      'chat-1',
      attachmentId,
      new File([new Uint8Array(1)], 'fifth.txt', { type: 'text/plain' }),
      () => {},
    )

    expect(outcome.ready).toBe(false)
    expect(outcome.error).toEqual({
      code: ErrorCodes.AttachmentBudgetExceeded,
      message: 'A prompt carries at most 4 attachments.',
      sessionId: 'chat-1',
    })

    // Nothing was staged, so nothing is cancelled: a failed Begin has no bytes.
    expect(invoke.mock.calls.some(([method]) => method === Server.CancelChatAttachment)).toBe(false)
  })
})

describe('sending a prompt', () => {
  beforeEach(() => {
    invoke.mockReset()
    invoke.mockImplementation(async (method) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null
      if (method === Server.SendChatPrompt) return [true, null, null]

      throw new Error(`Unexpected method ${method}`)
    })
  })

  it('carries text and the staged attachment ids in selection order', async () => {
    const client = await connected()
    const second = '0f2f2a2a-1111-4222-8333-444455556666'

    expect(await client.sendChatPrompt('chat-1', 'look', [attachmentId, second])).toBeNull()

    const call = invoke.mock.calls.find(([method]) => method === Server.SendChatPrompt)!
    expect(call[1]).toEqual(['chat-1', 'look', [attachmentId, second]])
  })

  it("reports the agent's refusal rather than pretending the prompt was sent", async () => {
    const client = await connected()
    invoke.mockImplementation(async (method) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null
      if (method === Server.SendChatPrompt) {
        return [false, ErrorCodes.AttachmentUnsupported, 'This agent does not accept images.']
      }

      throw new Error(`Unexpected method ${method}`)
    })

    const error = await client.sendChatPrompt('chat-1', 'look', [attachmentId])

    expect(error).toEqual({
      code: ErrorCodes.AttachmentUnsupported,
      message: 'This agent does not accept images.',
      sessionId: 'chat-1',
    })
  })

  it('still sends text-only prompts through the original method', async () => {
    const client = await connected()
    invoke.mockImplementation(async (method) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null
      if (method === Server.SendChatMessage) return null

      throw new Error(`Unexpected method ${method}`)
    })

    expect(await client.sendChatMessage('chat-1', 'continue')).toBeNull()
    expect(invoke.mock.calls.some(([method]) => method === Server.SendChatMessage)).toBe(true)
  })
})
