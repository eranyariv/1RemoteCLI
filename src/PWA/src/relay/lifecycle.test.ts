import { describe, expect, it, vi } from 'vitest'

import { watchRelayLifecycle, type RelayLifecycleEnvironment } from './lifecycle'

const iphone =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_6 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148'

function environment(platform: 'ios' | 'desktop') {
  const page = new EventTarget() as Document
  const browser = new EventTarget() as Window
  let visibility: DocumentVisibilityState = 'visible'

  Object.defineProperty(page, 'visibilityState', {
    configurable: true,
    get: () => visibility,
  })

  return {
    environment: {
      document: page,
      window: browser,
      userAgent: platform === 'ios' ? iphone : 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
      maxTouchPoints: platform === 'ios' ? 5 : 0,
    } satisfies RelayLifecycleEnvironment,
    page,
    browser,
    setVisibility(next: DocumentVisibilityState) {
      visibility = next
      page.dispatchEvent(new Event('visibilitychange'))
    },
  }
}

describe('relay lifecycle', () => {
  it('disconnects a hidden iOS app and starts a fresh connection when it returns', async () => {
    const client = {
      connected: true,
      start: vi.fn(async () => {}),
      stop: vi.fn(async () => {}),
      restart: vi.fn(async () => {}),
    }
    const moved = vi.fn()
    const ios = environment('ios')
    const dispose = watchRelayLifecycle(client, moved, ios.environment)

    ios.setVisibility('hidden')
    await vi.waitFor(() => expect(client.stop).toHaveBeenCalledTimes(1))

    ios.setVisibility('visible')
    ios.browser.dispatchEvent(new Event('pageshow'))

    await vi.waitFor(() => expect(client.restart).toHaveBeenCalledTimes(1))
    expect(client.stop).toHaveBeenCalledTimes(1)
    expect(client.start).not.toHaveBeenCalled()
    expect(moved).toHaveBeenCalledTimes(2)

    dispose()
  })

  it('does not disconnect a desktop tab merely because it was hidden', async () => {
    const client = {
      connected: true,
      start: vi.fn(async () => {}),
      stop: vi.fn(async () => {}),
      restart: vi.fn(async () => {}),
    }
    const desktop = environment('desktop')
    const dispose = watchRelayLifecycle(client, () => {}, desktop.environment)

    desktop.setVisibility('hidden')
    desktop.browser.dispatchEvent(new Event('pagehide'))
    await Promise.resolve()

    expect(client.stop).not.toHaveBeenCalled()
    expect(client.start).not.toHaveBeenCalled()
    expect(client.restart).not.toHaveBeenCalled()

    dispose()
  })
})
