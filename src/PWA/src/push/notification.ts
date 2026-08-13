/**
 * What to show when a push arrives.
 *
 * Kept out of `sw.ts` so it can be tested. A service worker is the hardest place
 * in the app to observe: it has no UI, its failures are silent, and reproducing
 * one means getting a real push service to send a real message to a real phone.
 * The decisions are worth having somewhere a test can reach.
 */

export interface NotificationPlan {
  title: string
  body: string
  url: string
  tag: string
}

interface RawPayload {
  title?: unknown
  body?: unknown
  url?: unknown
  tag?: unknown
}

const Fallback: NotificationPlan = {
  title: '1RemoteCLI',
  body: 'A session needs your attention.',
  url: '/',
  tag: '1remotecli',
}

/**
 * Reads a payload into something showable.
 *
 * Never returns null, and that is the whole point. On iOS a push that arrives
 * without a notification being shown counts against the app and eventually gets
 * its push permission revoked by the system — so a malformed payload must still
 * produce *something*. A vague notification is survivable; a silently dropped
 * one costs the user every future notification too.
 */
export function readPushPayload(text: string | undefined | null): NotificationPlan {
  if (!text) return Fallback

  let raw: RawPayload
  try {
    raw = JSON.parse(text) as RawPayload
  } catch {
    return Fallback
  }

  if (typeof raw !== 'object' || raw === null) return Fallback

  const url = sameOriginPath(raw.url)

  return {
    title: text_(raw.title) ?? Fallback.title,
    body: text_(raw.body) ?? Fallback.body,
    url,
    // Falls back to the URL rather than to a constant: without it every session
    // would share one tag, and a second waiting session would silently replace
    // the notification about the first. When there is no usable URL either,
    // collapsing back onto the one constant is the right answer — several
    // identical "something needs you" notifications help nobody.
    tag: text_(raw.tag) ?? (url === Fallback.url ? Fallback.tag : url),
  }
}

function text_(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null
}

/**
 * A same-origin path, whatever arrived.
 *
 * The payload is signed by nothing the browser checks — anyone who obtains the
 * subscription endpoint and keys can send one. Following an absolute URL out of
 * it would let that turn a notification tap into a redirect to any site at all,
 * wearing this app's name and icon. Only the path is ever kept.
 *
 * The leading-slash test is not decoration. `new URL` accepts `javascript:` and
 * `data:` against any base and hands back an *opaque* path — `alert(1)`, with no
 * slash — which would sail through as a relative URL. Requiring the slash is what
 * makes "only the path is kept" true rather than nearly true.
 */
function sameOriginPath(value: unknown): string {
  const raw = text_(value)
  if (!raw) return Fallback.url

  try {
    const url = new URL(raw, 'https://placeholder.invalid')
    const path = `${url.pathname}${url.search}`

    return path.startsWith('/') ? path : Fallback.url
  } catch {
    return Fallback.url
  }
}
