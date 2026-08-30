import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'

import { ErrorCodes } from '../protocol/errors'
import type { RelayClient } from '../relay/client'
import type { HubError } from '../protocol/wire'
import { useAttachedSession } from './useAttachedSession'

/**
 * What the terminal says about itself while the network misbehaves.
 *
 * The rule under test is honesty. A terminal that has quietly stopped receiving
 * output but still looks live is the worst state this app can be in, because the
 * user reads a stale screen and acts on it — answering a prompt that has already
 * timed out, or assuming a build is still running when it finished ten minutes
 * ago.
 */

type Handler = (...args: never[]) => void

class FakeRelay {
  private readonly listeners = new Map<string, Set<Handler>>()

  attach = vi.fn<
    (m: string, s: string, c: number, r: number, seq: number | null) => Promise<HubError | null>
  >(async () => null)

  detach = vi.fn(async () => null)
  sendInput = vi.fn(async () => null)
  interrupt = vi.fn(async () => null)
  resize = vi.fn(async () => null)

  on(event: string, handler: Handler): () => void {
    let set = this.listeners.get(event)

    if (!set) {
      set = new Set()
      this.listeners.set(event, set)
    }

    set.add(handler)
    return () => set.delete(handler)
  }

  emit(event: string, ...args: unknown[]): void {
    for (const handler of [...(this.listeners.get(event) ?? [])]) {
      ;(handler as (...a: unknown[]) => void)(...args)
    }
  }

  get client(): RelayClient {
    return this as unknown as RelayClient
  }
}

function options(relay: FakeRelay, connected: boolean) {
  return {
    client: relay.client,
    machineId: 'machine-1',
    sessionId: 'session-1',
    cols: 80,
    rows: 25,
    connected,
    onOutput: () => {},
  }
}

describe('useAttachedSession', () => {
  let relay: FakeRelay

  beforeEach(() => {
    relay = new FakeRelay()
  })

  it('reports reconnecting when the socket goes away, rather than staying green', async () => {
    const { result, rerender } = renderHook((connected: boolean) => useAttachedSession(options(relay, connected)), {
      initialProps: true,
    })

    await waitFor(() => expect(result.current.state).toBe('attached'))

    rerender(false)

    expect(result.current.state).toBe('reconnecting')
  })

  it('resumes from the last sequence it saw', async () => {
    const { result, rerender } = renderHook((connected: boolean) => useAttachedSession(options(relay, connected)), {
      initialProps: true,
    })

    await waitFor(() => expect(result.current.state).toBe('attached'))

    act(() => {
      relay.emit('terminalOutput', {
        sessionId: 'session-1',
        seq: 41,
        kind: 0,
        data: new Uint8Array([104, 105]),
      })
    })

    rerender(false)
    rerender(true)

    // Not a fresh attach. Asking for a repaint the agent could have answered with
    // the handful of frames we actually missed wastes the one resource a phone on
    // cellular does not have.
    await waitFor(() => expect(relay.attach).toHaveBeenLastCalledWith('machine-1', 'session-1', 80, 25, 41))
  })

  it('re-arms the gap notice after it is dismissed, so a second gap is not swallowed', async () => {
    const { result } = renderHook((connected: boolean) => useAttachedSession(options(relay, connected)), {
      initialProps: true,
    })

    await waitFor(() => expect(result.current.state).toBe('attached'))

    const gap = (seq: number) =>
      act(() => {
        relay.emit('terminalOutput', {
          sessionId: 'session-1',
          seq,
          kind: 0,
          data: new Uint8Array([104, 105]),
        })
      })

    // Sequence 1 arrives, then 9: everything between them is output we will never
    // see, and the user is told so.
    gap(1)
    gap(9)
    expect(result.current.missedOutput).toBe(true)

    act(() => result.current.dismissMissedOutput())
    expect(result.current.missedOutput).toBe(false)

    // The point of the dismissal is to reclaim the space, not to opt out of being
    // told again. A later gap is a new fact about a new stretch of the session, and
    // acknowledging the first must not hide it.
    gap(20)
    expect(result.current.missedOutput).toBe(true)
  })

  it('trusts an explicit snapshot report instead of inferring loss from its sequence', async () => {
    const { result } = renderHook(() => useAttachedSession(options(relay, true)))

    await waitFor(() => expect(result.current.state).toBe('attached'))

    act(() => {
      relay.emit('terminalOutput', {
        sessionId: 'session-1',
        seq: 1,
        kind: 'Delta',
        data: new Uint8Array([1]),
        continuityLost: false,
      })
      relay.emit('terminalOutput', {
        sessionId: 'session-1',
        seq: 9,
        kind: 'Snapshot',
        data: new Uint8Array([2]),
        continuityLost: false,
      })
    })

    expect(result.current.missedOutput).toBe(false)

    act(() => {
      relay.emit('terminalOutput', {
        sessionId: 'session-1',
        seq: 12,
        kind: 'Snapshot',
        data: new Uint8Array([3]),
        continuityLost: true,
      })
    })

    expect(result.current.missedOutput).toBe(true)
  })

  it('says the session ended when it is gone on the way back', async () => {
    const { result, rerender } = renderHook((connected: boolean) => useAttachedSession(options(relay, connected)), {
      initialProps: true,
    })

    await waitFor(() => expect(result.current.state).toBe('attached'))

    relay.attach.mockResolvedValue({
      code: ErrorCodes.SessionNotFound,
      message: 'No such session.',
      sessionId: 'session-1',
    })

    rerender(false)
    rerender(true)

    // Not 'failed'. The desk terminal was closed while we were away; there is
    // nothing to retry, and offering a retry button invites the user to keep
    // pressing it at a session that no longer exists.
    await waitFor(() => expect(result.current.state).toBe('closed'))
    expect(result.current.endedWhileAway).toBe(true)
    expect(result.current.error).toBeNull()
  })

  it('still offers a retry when the failure is one that might clear', async () => {
    relay.attach.mockResolvedValue({
      code: ErrorCodes.MachineOffline,
      message: 'That machine is offline.',
      sessionId: 'session-1',
    })

    const { result } = renderHook(() => useAttachedSession(options(relay, true)))

    // A machine that is asleep may well come back, unlike a session that ended.
    await waitFor(() => expect(result.current.state).toBe('failed'))
    expect(result.current.error?.code).toBe(ErrorCodes.MachineOffline)
    expect(result.current.endedWhileAway).toBe(false)
  })

  it('reattaches from a fresh snapshot when the agent restarts under the session', async () => {
    const { result } = renderHook(() => useAttachedSession(options(relay, true)))

    await waitFor(() => expect(result.current.state).toBe('attached'))

    act(() => {
      relay.emit('terminalOutput', {
        sessionId: 'session-1',
        seq: 41,
        kind: 0,
        data: new Uint8Array([104, 105]),
      })
      relay.emit('machineOffline', 'machine-1')
    })

    expect(result.current.state).toBe('failed')

    act(() => {
      relay.emit('sessionOpened', 'machine-1', { sessionId: 'session-1' })
    })

    await waitFor(() =>
      expect(relay.attach).toHaveBeenLastCalledWith('machine-1', 'session-1', 80, 25, null),
    )
    await waitFor(() => expect(result.current.state).toBe('attached'))
  })

  it('does not go looking for a session it watched exit', async () => {
    const { result, rerender } = renderHook((connected: boolean) => useAttachedSession(options(relay, connected)), {
      initialProps: true,
    })

    await waitFor(() => expect(result.current.state).toBe('attached'))

    act(() => {
      relay.emit('sessionClosed', 'machine-1', 'session-1', 0)
    })

    expect(result.current.state).toBe('closed')
    expect(result.current.exitCode).toBe(0)

    const attachesSoFar = relay.attach.mock.calls.length

    rerender(false)
    rerender(true)

    // Re-attaching would be answered with "no such session", and the view would
    // then report that it ended while we were away — a worse and less true story
    // than the exit code we actually watched arrive.
    expect(relay.attach.mock.calls.length).toBe(attachesSoFar)
    expect(result.current.state).toBe('closed')
    expect(result.current.endedWhileAway).toBe(false)
  })

  it('leaves a finished session finished when the network drops', async () => {
    const { result, rerender } = renderHook((connected: boolean) => useAttachedSession(options(relay, connected)), {
      initialProps: true,
    })

    await waitFor(() => expect(result.current.state).toBe('attached'))

    act(() => {
      relay.emit('sessionClosed', 'machine-1', 'session-1', 1)
    })

    rerender(false)

    // A session that ended has not become uncertain just because the network did.
    expect(result.current.state).toBe('closed')
  })

  it('hands the session back its desk shape when the phone leaves', async () => {
    // The phone reshaped the real PTY on attach, and nothing at the desk resizes
    // it back. Without this, walking away leaves a 45-column program stranded
    // inside a wide desktop window until somebody drags the corner.
    const { result, unmount } = renderHook(() =>
      useAttachedSession({ ...options(relay, true), restoreOnDetach: { cols: 120, rows: 30 } }),
    )

    await waitFor(() => expect(result.current.state).toBe('attached'))

    unmount()

    expect(relay.resize).toHaveBeenCalledWith('session-1', 120, 30)
  })

  it('restores before it detaches, not after', async () => {
    // The hub only forwards a resize from a client that is still attached, so the
    // order is the whole of whether this works.
    const order: string[] = []
    relay.resize.mockImplementation(async () => {
      order.push('resize')
      return null
    })
    relay.detach.mockImplementation(async () => {
      order.push('detach')
      return null
    })

    const { result, unmount } = renderHook(() =>
      useAttachedSession({ ...options(relay, true), restoreOnDetach: { cols: 120, rows: 30 } }),
    )

    await waitFor(() => expect(result.current.state).toBe('attached'))

    unmount()

    expect(order).toEqual(['resize', 'detach'])
  })

  it('does not resize a session it watched exit', async () => {
    // There is nothing left to reshape, and asking would draw an error about a
    // session the user already knows is over.
    const { result, unmount } = renderHook(() =>
      useAttachedSession({ ...options(relay, true), restoreOnDetach: { cols: 120, rows: 30 } }),
    )

    await waitFor(() => expect(result.current.state).toBe('attached'))

    act(() => {
      relay.emit('sessionClosed', 'machine-1', 'session-1', 0)
    })

    unmount()

    expect(relay.resize).not.toHaveBeenCalled()
  })

  it('does not restore on a reconnection, only on the way out', async () => {
    // A dropped socket is not the user walking away, and reshaping the desk
    // terminal every time the phone loses signal would be its own kind of mess.
    const { result, rerender } = renderHook(
      (connected: boolean) =>
        useAttachedSession({ ...options(relay, connected), restoreOnDetach: { cols: 120, rows: 30 } }),
      { initialProps: true },
    )

    await waitFor(() => expect(result.current.state).toBe('attached'))

    rerender(false)
    rerender(true)

    await waitFor(() => expect(result.current.state).toBe('attached'))

    expect(relay.resize).not.toHaveBeenCalled()
  })
})
