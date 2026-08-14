import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const start = vi.fn<() => Promise<void>>()
const stop = vi.fn(async () => {})
const getAccessToken = vi.fn<() => Promise<string | null>>()

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
      return { start, stop, on: () => {}, onclose: () => {}, onreconnecting: () => {}, onreconnected: () => {} }
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
 * A first connection that fails is the ordinary case — the app is opened in a lift,
 * or before the phone has picked up a signal. SignalR's automatic reconnect does not
 * cover it, so without our own retry the app sits on "offline" until the user
 * happens to reload, which is exactly the moment they are least able to.
 */
describe('the first connection', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    start.mockReset()
    stop.mockReset()
    getAccessToken.mockReset()
    getAccessToken.mockResolvedValue('token')
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('is tried again after it fails', async () => {
    start.mockRejectedValue(new Error('no route to host'))

    const client = new RelayClient('http://example.invalid/hub')
    const seen: string[] = []
    client.on('status', (status) => seen.push(status))

    await client.start()
    expect(start).toHaveBeenCalledTimes(1)
    expect(seen).toContain('offline')

    // The first retry is immediate, so a blip costs nothing.
    await vi.advanceTimersByTimeAsync(0)
    expect(start).toHaveBeenCalledTimes(2)

    // The second backs off, and is still pending a second later.
    await vi.advanceTimersByTimeAsync(500)
    expect(start).toHaveBeenCalledTimes(2)

    await vi.advanceTimersByTimeAsync(2_500)
    expect(start).toHaveBeenCalledTimes(3)

    await client.stop()
  })

  it('stops being tried once the client is stopped', async () => {
    start.mockRejectedValue(new Error('no route to host'))

    const client = new RelayClient('http://example.invalid/hub')
    await client.start()
    expect(start).toHaveBeenCalledTimes(1)

    await client.stop()

    await vi.advanceTimersByTimeAsync(60_000)
    expect(start).toHaveBeenCalledTimes(1)
  })

  it('is not retried when there is nobody signed in', async () => {
    getAccessToken.mockResolvedValue(null)

    const client = new RelayClient('http://example.invalid/hub')
    const seen: string[] = []
    client.on('status', (status) => seen.push(status))

    await client.start()

    expect(seen).toEqual(['signed-out'])
    expect(start).not.toHaveBeenCalled()

    // Retrying would spin forever against a state only a sign-in can change.
    await vi.advanceTimersByTimeAsync(60_000)
    expect(start).not.toHaveBeenCalled()

    await client.stop()
  })
})
