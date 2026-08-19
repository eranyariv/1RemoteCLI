import { beforeEach, describe, expect, it, vi } from 'vitest'

import { Server } from '../protocol/methods'

const start = vi.fn(async () => {})
const invoke = vi.fn<(method: string, ...args: unknown[]) => Promise<unknown>>()
const getAccessToken = vi.fn(async () => 'token' as string | null)

let reconnected: (() => void | Promise<void>) | null = null

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
        start,
        invoke,
        stop: async () => {},
        state: 'Connected',
        on: () => {},
        onclose: () => {},
        onreconnecting: () => {},
        onreconnected: (handler: () => void | Promise<void>) => {
          reconnected = handler
        },
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

/**
 * What has to happen again when SignalR reconnects for us.
 *
 * The hub keys everything it knows about a client — who they are, and which session
 * they are attached to — by connection id, and a reconnect produces a new one. A
 * connection that skipped the handshake is therefore a stranger: the hub refuses to
 * attach it and refuses to carry its keystrokes, while the app, having been told by
 * SignalR that it is connected, shows a live terminal and lets the user type into it.
 *
 * This is not an unusual situation on the device this app is for. A lift, a tunnel, or
 * a screen locked for a minute is enough, and the symptom — input that silently goes
 * nowhere — is the worst one a remote terminal has, because it is indistinguishable
 * from a slow command.
 */
describe('reconnecting', () => {
  beforeEach(() => {
    reconnected = null
    start.mockClear()
    invoke.mockReset()
    // The handshake answers with a rejection or nothing; ListMachines and ListProjects answer with a list.
    invoke.mockImplementation(async (method: string) =>
      method === Server.ListMachines || method === Server.ListProjects ? [] : null,
    )
    getAccessToken.mockClear()
  })

  it('introduces itself to the hub again', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()

    expect(invoke).toHaveBeenCalledWith(Server.ClientHandshake, expect.anything())
    invoke.mockClear()

    expect(reconnected).not.toBeNull()
    await reconnected!()

    expect(invoke).toHaveBeenCalledWith(Server.ClientHandshake, expect.anything())
  })

  it('does not say it is connected until the hub has agreed', async () => {
    const client = new RelayClient('http://example.invalid/hub')
    await client.start()

    const seen: string[] = []
    client.on('status', (status) => seen.push(status))

    const order: string[] = []
    invoke.mockImplementation(async (method: string) => {
      order.push(method)
      if (method === Server.ClientHandshake) {
        // Anything the app does on hearing "connected" would be issued here, before
        // the hub has any record of this connection.
        expect(seen).not.toContain('connected')
      }
      return method === Server.ListMachines || method === Server.ListProjects ? [] : null
    })

    await reconnected!()

    expect(seen).toContain('connected')
    // And the handshake comes first: listing machines against a connection the hub
    // does not know is the same mistake one method earlier.
    expect(order[0]).toBe(Server.ClientHandshake)
  })
})
