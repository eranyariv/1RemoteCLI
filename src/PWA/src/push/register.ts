import { resolveHubUrl } from '../relay/endpoint'
import { describeSubscription, applicationServerKey, type PushRegistration } from './subscription'

/**
 * Getting this browser subscribed, and keeping the hub's copy current.
 *
 * The impure half of push registration: talking to the push service, the
 * service worker, and the hub. The decisions it makes are in `subscription.ts`
 * where they can be tested; what is left here is sequencing and failure
 * handling.
 *
 * Every failure is soft. Notifications are the second-best thing this app does;
 * the first is attaching to a session, and nothing here is allowed to interfere
 * with that. A phone with no subscription is a phone that has to be opened
 * manually — annoying, not broken.
 */

/** Where the hub publishes its VAPID public key. Sibling of the hub path, not under it. */
function vapidUrl(): string {
  const hub = new URL(resolveHubUrl())
  return `${hub.origin}/push/vapid`
}

/**
 * The server's public key, or null if push is not configured on the hub.
 *
 * A 404 is the documented answer for "this hub has no keypair", which is the
 * normal state of a development hub. It is not an error and must not be
 * reported as one.
 */
export async function fetchVapidKey(fetcher: typeof fetch = fetch): Promise<string | null> {
  try {
    const response = await fetcher(vapidUrl(), { cache: 'no-store' })
    if (!response.ok) return null

    const body = (await response.json()) as { key?: unknown }
    return typeof body.key === 'string' && body.key.length > 0 ? body.key : null
  } catch {
    return null
  }
}

/**
 * Subscribes this browser if it is not already, and returns what the hub needs.
 *
 * Reuses an existing subscription rather than replacing it. Re-subscribing
 * would mint a new endpoint and orphan the old one, and the hub would go on
 * pushing to both — so the phone would buzz twice for one prompt, which is a
 * quicker route to the user disabling notifications than never buzzing at all.
 */
export async function subscribe(
  registration: ServiceWorkerRegistration,
  vapidKey: string,
): Promise<PushRegistration | null> {
  try {
    const existing = await registration.pushManager.getSubscription()
    if (existing) {
      const described = describeSubscription(existing)
      if (described) return described

      // Keyless, which should not happen. Left in place rather than unsubscribed:
      // a subscription we cannot describe is still one the push service may be
      // holding, and removing it is not obviously better than ignoring it.
      return null
    }

    const created = await registration.pushManager.subscribe({
      // Required by Chrome, and honest anyway: every notification this app sends
      // is shown to the user.
      userVisibleOnly: true,
      applicationServerKey: applicationServerKey(vapidKey),
    })

    return describeSubscription(created)
  } catch {
    // Permission revoked between the check and the call, a key the browser
    // rejects, or a push service that is simply down.
    return null
  }
}

/** Whether push can be attempted at all in this browser, before any permission is asked for. */
export function pushSupported(target: Window = window): boolean {
  return (
    'serviceWorker' in target.navigator &&
    'PushManager' in target &&
    'Notification' in target
  )
}
