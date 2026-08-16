/**
 * Where the app looks for the relay.
 *
 * The hub serves this app, so by default the hub is whoever served it. Overridable
 * at build time for development, where the Vite server and the hub really are two
 * different origins. Mirrors `src/Daemon/Hub/HubEndpoint.cs`, including the
 * tolerance for a URL that already names the hub path — both forms are things a
 * person reasonably types.
 */

/**
 * Only used where there is no page to ask — unit tests, and any future non-browser
 * caller. A real browser uses its own origin, so adding a domain never means
 * rebuilding this. See `docs/azure-setup.md`.
 */
export const DEFAULT_HUB = 'https://1remotecli-hub.azurewebsites.net'

/** Must match the path the hub maps `RelayHub` on. */
const HUB_PATH = 'hub'

/**
 * The origin this app was served from, or null when there is no page.
 *
 * Deliberately the default rather than a fallback after a compiled-in host: in
 * production the hub is what served the bundle, so anything else is a guess that
 * happens to be right for one hostname. Guessing wrong is a cross-origin request to
 * a hub that configures no CORS, which fails as an opaque `TypeError: Load failed`
 * long after sign-in has succeeded — so the app looks healthy and simply never
 * connects.
 */
function servingOrigin(): string | null {
  if (typeof window === 'undefined') {
    return null
  }

  const origin = window.location?.origin

  // "null" is what a document loaded from a file or an opaque origin reports, and
  // it is not a URL.
  return typeof origin === 'string' && origin.length > 0 && origin !== 'null' ? origin : null
}

export function resolveHubUrl(configured?: string): string {
  const raw = (configured ?? import.meta.env.VITE_HUB_URL ?? '').trim()

  let base: URL
  try {
    base = new URL(raw.length > 0 ? raw : (servingOrigin() ?? DEFAULT_HUB))
  } catch {
    // A mistyped override should not leave the app pointing at nothing. Falling
    // back is more useful than failing, because the default is right for everyone
    // who did not set the variable in the first place.
    base = new URL(servingOrigin() ?? DEFAULT_HUB)
  }

  const path = base.pathname.replace(/\/+$/, '')

  if (path.toLowerCase().endsWith(`/${HUB_PATH}`)) {
    return `${base.origin}${path}`
  }

  return `${base.origin}${path}/${HUB_PATH}`
}
