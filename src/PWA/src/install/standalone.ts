/**
 * Whether this browser can ever deliver a notification, and if not, what the
 * user would have to do about it.
 *
 * This exists because iOS makes web push conditional on things the app cannot
 * change at runtime and cannot discover by asking. `Notification.requestPermission`
 * will happily resolve `granted` in a Safari tab and then never deliver
 * anything, so a permission prompt is not a test of whether push works. The
 * three conditions are:
 *
 *   - iOS 16.4 or later. Earlier versions have no web push at all.
 *   - The app installed to the home screen. A tab can never receive push,
 *     however the permission ends up.
 *   - Permission requested from a user gesture.
 *
 * Only the first two are knowable up front, so they are what this module
 * answers. The third is the caller's job: never call `requestPermission`
 * outside a tap handler.
 */

export type Platform = 'ios' | 'android' | 'desktop'

/**
 * Everything the decision depends on, passed in rather than read from globals,
 * because the interesting cases are all devices the test runner is not.
 */
export interface PushEnvironment {
  standalone: boolean
  platform: Platform
  /** Null when the user agent did not say, which is not the same as "old". */
  iosVersion: number | null
  hasServiceWorker: boolean
  hasPushManager: boolean
  hasNotification: boolean
  permission: NotificationPermission
}

export type PushReadiness =
  /** Installed, supported, and already allowed to notify. */
  | { kind: 'granted' }
  /** Everything is in place; all that is missing is the tap. */
  | { kind: 'ready' }
  /** The user said no. Only Settings can undo this, so do not ask again. */
  | { kind: 'blocked' }
  /** Push is possible on this device, but only from the home screen. */
  | { kind: 'needs-install'; platform: Platform }
  /** No route to notifications here at all. */
  | { kind: 'unsupported'; reason: string }

/** The first iOS release with web push. */
export const MinimumIosVersion = 16.4

/**
 * iPadOS 13 and later claim to be a Macintosh. The touch points are the giveaway:
 * no real Mac reports more than one, and every iPad reports five. Getting this
 * wrong would show a desktop user iPhone instructions, or worse, hide the
 * install step from an iPad that needs it.
 */
export function detectPlatform(userAgent: string, maxTouchPoints: number): Platform {
  if (/iphone|ipad|ipod/i.test(userAgent)) return 'ios'
  if (/macintosh/i.test(userAgent) && maxTouchPoints > 1) return 'ios'
  if (/android/i.test(userAgent)) return 'android'
  return 'desktop'
}

/**
 * Reads the iOS version out of the user agent, where it appears as `OS 16_4` or
 * `Version/17.2`. Returns null when it cannot be read, and callers treat null as
 * "assume it works": refusing to offer notifications because of an unfamiliar
 * user agent string would be a worse failure than offering them and having the
 * subscription fail.
 */
export function iosVersion(userAgent: string): number | null {
  const os = /OS (\d+)[._](\d+)/.exec(userAgent)
  if (os) return Number(`${os[1]}.${os[2]}`)

  const version = /Version\/(\d+)\.(\d+)/.exec(userAgent)
  if (version) return Number(`${version[1]}.${version[2]}`)

  return null
}

/**
 * True when the app is running from the home screen rather than in a tab.
 *
 * Safari does not implement the `display-mode` media query the rest of the world
 * uses, and instead sets a non-standard `navigator.standalone`. Both are checked,
 * because between them they cover every browser this app cares about.
 */
export function isStandalone(window: Window): boolean {
  const legacy = (window.navigator as Navigator & { standalone?: boolean }).standalone
  if (legacy === true) return true

  if (typeof window.matchMedia !== 'function') return false
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    window.matchMedia('(display-mode: fullscreen)').matches
  )
}

export function readPushEnvironment(window: Window): PushEnvironment {
  const navigator = window.navigator
  const userAgent = navigator.userAgent ?? ''
  const platform = detectPlatform(userAgent, navigator.maxTouchPoints ?? 0)
  const notification = (window as Window & { Notification?: typeof Notification }).Notification

  return {
    standalone: isStandalone(window),
    platform,
    iosVersion: platform === 'ios' ? iosVersion(userAgent) : null,
    hasServiceWorker: 'serviceWorker' in navigator,
    hasPushManager: 'PushManager' in window,
    hasNotification: notification !== undefined,
    permission: notification?.permission ?? 'default',
  }
}

export function pushReadiness(environment: PushEnvironment): PushReadiness {
  const { platform, standalone } = environment

  // The version check comes first, or an iPhone on iOS 15 is told its browser
  // cannot do notifications when the truth is that the OS needs updating - and
  // no amount of adding to the home screen will help.
  if (
    platform === 'ios' &&
    environment.iosVersion !== null &&
    environment.iosVersion < MinimumIosVersion
  ) {
    return { kind: 'unsupported', reason: `Notifications need iOS ${MinimumIosVersion} or later.` }
  }

  // iOS hides PushManager from a tab entirely, so this has to be asked before the
  // capability checks below. Otherwise an uninstalled iPhone reports as
  // unsupported and the user is never told about the one step that would fix it.
  if (platform === 'ios' && !standalone) {
    return { kind: 'needs-install', platform }
  }

  if (!environment.hasServiceWorker || !environment.hasNotification || !environment.hasPushManager) {
    return { kind: 'unsupported', reason: 'This browser cannot deliver notifications.' }
  }

  if (environment.permission === 'denied') return { kind: 'blocked' }
  if (environment.permission === 'granted') return { kind: 'granted' }

  return { kind: 'ready' }
}

export interface InstallGuide {
  title: string
  steps: string[]
}

/**
 * How to install, per platform. iOS is the only one that matters for push, but
 * an Android user who lands here should not be told to tap a button Safari has
 * and Chrome does not.
 */
export function installGuide(platform: Platform): InstallGuide {
  if (platform === 'ios') {
    return {
      title: 'Add 1RemoteCLI to your Home Screen',
      steps: [
        'Tap the Share button in the Safari toolbar.',
        'Scroll down and tap Add to Home Screen.',
        'Tap Add, then open 1RemoteCLI from the Home Screen.',
      ],
    }
  }

  if (platform === 'android') {
    return {
      title: 'Install 1RemoteCLI',
      steps: ['Open the browser menu.', 'Tap Install app, or Add to Home screen.'],
    }
  }

  return {
    title: 'Install 1RemoteCLI',
    steps: ['Use the install icon in the address bar to install the app.'],
  }
}
