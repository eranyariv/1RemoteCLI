import { cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { RelayClient } from '../relay/client'
import type { PushPreferences, PushRegistration } from './subscription'
import { usePushRegistration } from './usePush'

const registerMocks = vi.hoisted(() => ({
  fetchVapidKey: vi.fn(async () => new Uint8Array([1, 2, 3])),
  pushSupported: vi.fn(() => true),
  subscribe: vi.fn(),
}))

vi.mock('./register', () => registerMocks)

const subscription: PushRegistration = {
  endpoint: 'https://push.example/device',
  keys: { p256dh: 'p256dh', auth: 'auth' },
}

describe('push preference registration', () => {
  beforeEach(() => {
    registerMocks.fetchVapidKey.mockClear()
    registerMocks.pushSupported.mockClear()
    registerMocks.subscribe.mockReset()
    registerMocks.subscribe.mockResolvedValue(subscription)
    vi.stubGlobal('Notification', { permission: 'granted' })
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { ready: Promise.resolve({}) },
    })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('serializes updates so the newest category preferences win', async () => {
    let finishFirst: ((value: null) => void) | undefined
    const registerPush = vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise<null>((resolve) => {
            finishFirst = resolve
          }),
      )
      .mockResolvedValue(null)
    const client = { registerPush } as unknown as RelayClient
    const initial: PushPreferences = {
      awaitingInput: true,
      sessionFinished: true,
      announcements: true,
    }
    const latest: PushPreferences = {
      awaitingInput: false,
      sessionFinished: true,
      announcements: false,
    }

    const hook = renderHook(
      ({ preferences }) => usePushRegistration(client, true, preferences),
      { initialProps: { preferences: initial } },
    )
    await waitFor(() => expect(registerPush).toHaveBeenCalledOnce())

    hook.rerender({ preferences: latest })
    expect(registerPush).toHaveBeenCalledOnce()

    finishFirst?.(null)
    await waitFor(() => expect(registerPush).toHaveBeenCalledTimes(2))
    expect(registerPush.mock.calls[1][1]).toEqual(latest)
  })
})
