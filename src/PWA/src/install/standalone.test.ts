import { describe, expect, it } from 'vitest'

import {
  detectPlatform,
  installGuide,
  iosVersion,
  isStandalone,
  pushReadiness,
  type PushEnvironment,
} from './standalone'

const iPhone =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_2 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Mobile/15E148 Safari/604.1'
const iPad =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15'
const mac =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'
const android =
  'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36'

function environment(overrides: Partial<PushEnvironment> = {}): PushEnvironment {
  return {
    standalone: true,
    platform: 'ios',
    iosVersion: 17.2,
    hasServiceWorker: true,
    hasPushManager: true,
    hasNotification: true,
    permission: 'default',
    ...overrides,
  }
}

describe('detectPlatform', () => {
  it('knows an iPhone', () => {
    expect(detectPlatform(iPhone, 5)).toBe('ios')
  })

  it('sees through an iPad pretending to be a Mac', () => {
    // iPadOS 13 dropped "iPad" from the user agent. Touch points are the only
    // thing left that distinguishes the tablet from the laptop, and getting it
    // wrong hides the add-to-home-screen step from a device that needs it.
    expect(detectPlatform(iPad, 5)).toBe('ios')
  })

  it('does not mistake a real Mac for an iPad', () => {
    expect(detectPlatform(mac, 0)).toBe('desktop')
  })

  it('knows an Android phone', () => {
    expect(detectPlatform(android, 5)).toBe('android')
  })
})

describe('iosVersion', () => {
  it('reads the version out of an iPhone user agent', () => {
    expect(iosVersion(iPhone)).toBe(17.2)
  })

  it('falls back to the Safari version on an iPad, which reports no OS', () => {
    expect(iosVersion(iPad)).toBe(17.2)
  })

  it('reads a two-digit minor without turning 16.4 into 16.04', () => {
    expect(iosVersion('CPU iPhone OS 16_4 like Mac OS X')).toBe(16.4)
  })

  it('says nothing rather than guessing when the agent does not say', () => {
    expect(iosVersion('something entirely new')).toBeNull()
  })
})

describe('isStandalone', () => {
  function fakeWindow(options: { legacy?: boolean; displayMode?: string }): Window {
    return {
      navigator: { standalone: options.legacy },
      matchMedia: (query: string) => ({ matches: query.includes(options.displayMode ?? 'never') }),
    } as unknown as Window
  }

  it('believes the non-standard flag Safari sets', () => {
    // Safari implements no display-mode media query, so this is the only signal
    // available on the one platform where the answer changes behaviour.
    expect(isStandalone(fakeWindow({ legacy: true }))).toBe(true)
  })

  it('believes the display-mode query everyone else implements', () => {
    expect(isStandalone(fakeWindow({ displayMode: 'standalone' }))).toBe(true)
  })

  it('is false in a plain tab', () => {
    expect(isStandalone(fakeWindow({ legacy: false }))).toBe(false)
  })

  it('does not throw where matchMedia is missing', () => {
    expect(isStandalone({ navigator: {} } as unknown as Window)).toBe(false)
  })
})

describe('pushReadiness', () => {
  it('tells an uninstalled iPhone to install, rather than asking for permission', () => {
    // A Safari tab can never receive push on iOS, no matter what the permission
    // says. Prompting there produces a granted permission and silence.
    expect(pushReadiness(environment({ standalone: false }))).toEqual({
      kind: 'needs-install',
      platform: 'ios',
    })
  })

  it('blames the OS version, not the browser, on an old iPhone', () => {
    const readiness = pushReadiness(environment({ standalone: false, iosVersion: 15.6 }))

    expect(readiness.kind).toBe('unsupported')
    // The install advice would be a lie here: adding to the home screen on iOS 15
    // does not produce notifications.
    expect(readiness).not.toMatchObject({ kind: 'needs-install' })
  })

  it('assumes an unrecognised iOS is new enough', () => {
    // Refusing on an unfamiliar user agent would break the app on every iOS
    // released after this code was written.
    expect(pushReadiness(environment({ iosVersion: null })).kind).toBe('ready')
  })

  it('is ready when installed and not yet asked', () => {
    expect(pushReadiness(environment())).toEqual({ kind: 'ready' })
  })

  it('reports a granted permission without asking again', () => {
    expect(pushReadiness(environment({ permission: 'granted' }))).toEqual({ kind: 'granted' })
  })

  it('treats a refusal as final', () => {
    // Browsers ignore a second request after a denial, so re-prompting would be
    // a button that visibly does nothing.
    expect(pushReadiness(environment({ permission: 'denied' }))).toEqual({ kind: 'blocked' })
  })

  it('reports a browser with no push support as unsupported', () => {
    expect(
      pushReadiness(environment({ platform: 'desktop', iosVersion: null, hasPushManager: false }))
        .kind,
    ).toBe('unsupported')
  })

  it('does not demand installation on Android, where a tab can receive push', () => {
    expect(pushReadiness(environment({ platform: 'android', iosVersion: null, standalone: false }))).toEqual({
      kind: 'ready',
    })
  })
})

describe('installGuide', () => {
  it('names the Share sheet on iOS, which is where the option hides', () => {
    const guide = installGuide('ios')

    expect(guide.steps.some((step) => /share/i.test(step))).toBe(true)
    expect(guide.steps.some((step) => /add to home screen/i.test(step))).toBe(true)
  })

  it('does not tell an Android user to look in a Safari toolbar', () => {
    expect(installGuide('android').steps.some((step) => /safari/i.test(step))).toBe(false)
  })
})
