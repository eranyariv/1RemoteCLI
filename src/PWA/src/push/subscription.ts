/**
 * Turning a browser's push subscription into something the hub can use, and a
 * notification's URL back into a session to open.
 *
 * Pure on purpose. Everything here is a small transformation that is impossible
 * to observe without a real device and a real push service — base64url with the
 * wrong padding produces a subscription the browser rejects, and a deep link
 * that parses to the wrong session sends the user to somebody else's terminal.
 * Both are cheap to test here and expensive to find on a phone.
 */

/** What the hub's `RegisterPush` wants. */
export interface PushRegistration {
  endpoint: string
  keys: { p256dh: string; auth: string }
}

export interface PushPreferences {
  awaitingInput: boolean
  sessionFinished: boolean
  announcements: boolean
}

/**
 * The VAPID public key, as `pushManager.subscribe` wants it.
 *
 * The key travels as base64url — no padding, `-` and `_` — and the browser
 * insists on raw bytes. `atob` understands neither, so both have to be undone
 * by hand.
 */
export function applicationServerKey(base64url: string): Uint8Array<ArrayBuffer> {
  const normalised = base64url.trim().replace(/-/g, '+').replace(/_/g, '/')
  const padded = normalised.padEnd(normalised.length + ((4 - (normalised.length % 4)) % 4), '=')

  const binary = atob(padded)
  const bytes = new Uint8Array(new ArrayBuffer(binary.length))
  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i)

  return bytes
}

/** The inverse, for the two keys the browser hands back as raw buffers. */
export function toBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)

  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

/**
 * Reads a browser subscription into the shape the hub stores.
 *
 * Returns null rather than throwing on a subscription missing its keys. That
 * should not happen, but a browser that produced one would otherwise take down
 * the app on start-up, and notifications are not worth that.
 */
export function describeSubscription(subscription: PushSubscription): PushRegistration | null {
  const p256dh = subscription.getKey('p256dh')
  const auth = subscription.getKey('auth')
  if (!p256dh || !auth) return null

  return {
    endpoint: subscription.endpoint,
    keys: { p256dh: toBase64Url(p256dh), auth: toBase64Url(auth) },
  }
}

/** Where a session lives, as carried by a notification's URL. */
export interface DeepLink {
  machineId: string
  sessionId: string
}

/**
 * The session a notification wants opened, if the URL names one.
 *
 * A query string rather than a path because the app is a single page served by
 * a static host: a path would need either a rewrite rule or a router, and both
 * are more machinery than one pair of ids justifies.
 */
export function readDeepLink(search: string): DeepLink | null {
  let params: URLSearchParams
  try {
    params = new URLSearchParams(search)
  } catch {
    return null
  }

  const machineId = params.get('machine')
  const sessionId = params.get('session')

  return machineId && sessionId ? { machineId, sessionId } : null
}

/**
 * The same URL with the deep link taken out.
 *
 * Consumed once. Left in place, closing the session and reloading — which is
 * what a phone does on its own after a while — would reopen the terminal the
 * user had deliberately left, and there would be no way back to the machine
 * list without editing the address bar.
 */
export function withoutDeepLink(href: string): string {
  const url = new URL(href, 'https://placeholder.invalid')
  url.searchParams.delete('machine')
  url.searchParams.delete('session')

  return `${url.pathname}${url.search}${url.hash}`
}
