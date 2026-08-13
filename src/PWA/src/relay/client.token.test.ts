import { beforeEach, describe, expect, it, vi } from 'vitest'

const handlers = new Map<string, (...args: unknown[]) => void>()
const invoke = vi.fn<(method: string, ...args: unknown[]) => Promise<unknown>>()
const stop = vi.fn(async () => {})
const getAccessToken = vi.fn<() => Promise<string | null>>()

vi.mock('../auth/msal', () => ({ getAccessToken: () => getAccessToken() }))

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
        state: 'Connected',
        start: async () => {},
        stop,
        invoke,
        on: (method: string, handler: (...args: unknown[]) => void) => handlers.set(method, handler),
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

/** Every invoke the client makes, except the ones it makes on every connect. */
function refreshCalls() {
  return invoke.mock.calls.filter(([method]) => method === 'RefreshToken')
}

/**
 * SignalR checks the token during the handshake and never again, so a socket left
 * open outlives its token unless the hub asks and the client answers. Without this
 * the app works for exactly one token lifetime and then disconnects, on the device
 * least able to explain why.
 */
describe('a token that is about to expire', () => {
  const seen: [string, string | undefined][] = []

  beforeEach(async () => {
    handlers.clear()
    invoke.mockReset()
    stop.mockReset()
    getAccessToken.mockReset()

    getAccessToken.mockResolvedValue('first-token')
    // Null is the hub's "no complaint" answer to the handshake and to a refresh; the
    // machine list is the one invoke that has to come back shaped like something.
    invoke.mockImplementation(async (method) => (method === 'ListMachines' ? [[]] : null))

    const client = new RelayClient('http://example.invalid/hub')
    seen.length = 0
    client.on('status', (status, detail) => seen.push([status, detail]))
    await client.start()
  })

  it('is replaced without the user noticing', async () => {
    getAccessToken.mockResolvedValue('second-token')

    await handlers.get('TokenExpiring')!([new Date().toISOString()])

    expect(refreshCalls()).toEqual([['RefreshToken', ['second-token']]])
    expect(seen.map(([status]) => status)).not.toContain('signed-out')
  })

  it('reports a sign-in that could not be renewed', async () => {
    getAccessToken.mockResolvedValue(null)

    await handlers.get('TokenExpiring')!([new Date().toISOString()])

    expect(refreshCalls()).toEqual([])
    expect(seen.at(-1)?.[0]).toBe('signed-out')
  })

  it('reports a refresh the hub refused', async () => {
    invoke.mockImplementation(async (method) => {
      if (method === 'RefreshToken') return ['token_expired', 'That token is not valid.', null]
      return method === 'ListMachines' ? [[]] : null
    })

    await handlers.get('TokenExpiring')!([new Date().toISOString()])

    expect(seen.at(-1)?.[0]).toBe('signed-out')
  })

  /**
   * The hub aborts a connection whose refresh presents somebody else's identity, so
   * the invoke never completes. That is a disconnection for the reconnect path to
   * handle, not an unhandled rejection that takes the tab down.
   */
  it('survives the hub closing the connection mid-refresh', async () => {
    invoke.mockImplementation(async (method) => {
      if (method === 'RefreshToken') throw new Error('Invocation canceled.')
      return method === 'ListMachines' ? [[]] : null
    })

    await expect(handlers.get('TokenExpiring')!([new Date().toISOString()])).resolves.not.toThrow()
  })
})
