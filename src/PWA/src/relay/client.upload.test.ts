import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorCodes } from '../protocol/errors'
import { Server } from '../protocol/methods'
import {
  MAX_TERMINAL_UPLOAD_BYTES,
  TERMINAL_UPLOAD_CHUNK_BYTES,
} from '../terminal/attachment'

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

const uploadId = '29fb210b-e7b4-4d41-8913-74a57a4eb753'

describe('terminal file upload', () => {
  let totalBytes = 0

  beforeEach(() => {
    invoke.mockReset()
    getAccessToken.mockClear()
    vi.stubGlobal('crypto', { randomUUID: () => uploadId })

    invoke.mockImplementation(async (method, argument) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null

      const request = argument as unknown[]
      if (method === Server.BeginTerminalUpload) {
        totalBytes = request[3] as number
        return [uploadId, 0, totalBytes, totalBytes === 0 ? 'C:\\Temp\\empty.txt' : null, null, null]
      }
      if (method === Server.UploadTerminalChunk) {
        const offset = request[2] as number
        const data = request[3] as Uint8Array
        const confirmed = offset + data.byteLength
        return [
          uploadId,
          confirmed,
          totalBytes,
          confirmed === totalBytes ? 'C:\\Temp\\photo.bin' : null,
          null,
          null,
        ]
      }
      if (method === Server.CancelTerminalUpload) {
        return [uploadId, 0, totalBytes, null, ErrorCodes.UploadCancelled, 'Cancelled']
      }

      throw new Error(`Unexpected method ${method}`)
    })
  })

  it('chunks sequentially and reports only agent-confirmed progress', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()
    const bytes = new Uint8Array(TERMINAL_UPLOAD_CHUNK_BYTES + 7).fill(0x5a)
    const progress: number[] = []

    const outcome = await client.uploadTerminalFile(
      'session-1',
      new File([bytes], 'photo.bin'),
      (update) => progress.push(update.confirmedBytes),
    )

    expect(outcome).toEqual({
      remotePath: 'C:\\Temp\\photo.bin',
      error: null,
      cancelled: false,
    })
    expect(progress).toEqual([0, TERMINAL_UPLOAD_CHUNK_BYTES, bytes.byteLength])

    const chunks = invoke.mock.calls.filter(([method]) => method === Server.UploadTerminalChunk)
    expect(chunks).toHaveLength(2)
    expect((chunks[0][1] as unknown[])[2]).toBe(0)
    expect((chunks[1][1] as unknown[])[2]).toBe(TERMINAL_UPLOAD_CHUNK_BYTES)
  })

  it('cancels the agent-side partial file when the user cancels', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()
    const controller = new AbortController()

    const outcome = await client.uploadTerminalFile(
      'session-1',
      new File([new Uint8Array(100)], 'notes.txt'),
      () => controller.abort(),
      controller.signal,
    )

    expect(outcome.cancelled).toBe(true)
    expect(invoke.mock.calls.some(([method]) => method === Server.CancelTerminalUpload)).toBe(true)
    expect(invoke.mock.calls.some(([method]) => method === Server.UploadTerminalChunk)).toBe(false)
  })

  it('does not report success when cancellation races the final acknowledgement', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()
    const controller = new AbortController()
    invoke.mockImplementation(async (method, argument) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null
      if (method === Server.BeginTerminalUpload) return [uploadId, 0, 1, null, null, null]
      if (method === Server.UploadTerminalChunk) {
        controller.abort()
        return [uploadId, 1, 1, 'C:\\Temp\\photo.jpg', null, null]
      }
      if (method === Server.CancelTerminalUpload) {
        return [uploadId, 1, 1, null, ErrorCodes.UploadCancelled, 'Cancelled']
      }

      throw new Error(`Unexpected method ${method}: ${String(argument)}`)
    })

    const outcome = await client.uploadTerminalFile(
      'session-1',
      new File([new Uint8Array(1)], 'photo.jpg'),
      () => {},
      controller.signal,
    )

    expect(outcome).toEqual({ remotePath: null, error: null, cancelled: true })
    expect(invoke.mock.calls.some(([method]) => method === Server.CancelTerminalUpload)).toBe(true)
  })

  it('rejects an oversized file before sending bytes to the hub', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()
    const file = {
      name: 'large.bin',
      size: MAX_TERMINAL_UPLOAD_BYTES + 1,
    } as File

    const outcome = await client.uploadTerminalFile('session-1', file, () => {})

    expect(outcome.error?.code).toBe(ErrorCodes.FileTooLarge)
    expect(invoke.mock.calls.some(([method]) => method === Server.BeginTerminalUpload)).toBe(false)
  })

  it('reports a failed chunk and asks the agent to remove the partial file', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()
    invoke.mockImplementation(async (method, argument) => {
      if (method === Server.ListMachines || method === Server.ListProjects) return [[]]
      if (method === Server.ClientHandshake) return null
      if (method === Server.BeginTerminalUpload) {
        return [uploadId, 0, 3, null, null, null]
      }
      if (method === Server.UploadTerminalChunk) {
        throw new Error('connection lost')
      }
      if (method === Server.CancelTerminalUpload) {
        return [uploadId, 0, 3, null, ErrorCodes.UploadCancelled, 'Cancelled']
      }

      throw new Error(`Unexpected method ${method}: ${String(argument)}`)
    })

    const outcome = await client.uploadTerminalFile(
      'session-1',
      new File([new Uint8Array(3)], 'notes.txt'),
      () => {},
    )

    expect(outcome).toEqual({
      remotePath: null,
      error: {
        code: ErrorCodes.UploadFailed,
        message: 'connection lost',
        sessionId: 'session-1',
      },
      cancelled: false,
    })
    expect(invoke.mock.calls.some(([method]) => method === Server.CancelTerminalUpload)).toBe(true)
  })
})
