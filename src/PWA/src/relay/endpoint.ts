/**
 * Where the app looks for the relay.
 *
 * One compiled-in default, overridable at build time. Mirrors
 * `src/Daemon/Hub/HubEndpoint.cs`, including the tolerance for a URL that already
 * names the hub path — both forms are things a person reasonably types.
 */

/** The deployed hub. See `docs/azure-setup.md`. */
export const DEFAULT_HUB = 'https://1remotecli-hub.azurewebsites.net'

/** Must match the path the hub maps `RelayHub` on. */
const HUB_PATH = 'hub'

export function resolveHubUrl(configured?: string): string {
  const raw = (configured ?? import.meta.env.VITE_HUB_URL ?? '').trim()

  let base: URL
  try {
    base = new URL(raw.length > 0 ? raw : DEFAULT_HUB)
  } catch {
    // A mistyped override should not leave the app pointing at nothing. Falling
    // back is more useful than failing, because the default is right for everyone
    // who did not set the variable in the first place.
    base = new URL(DEFAULT_HUB)
  }

  const path = base.pathname.replace(/\/+$/, '')

  if (path.toLowerCase().endsWith(`/${HUB_PATH}`)) {
    return `${base.origin}${path}`
  }

  return `${base.origin}${path}/${HUB_PATH}`
}
