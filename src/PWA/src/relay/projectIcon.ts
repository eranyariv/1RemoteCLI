import { useEffect, useState } from 'react'

import { auth } from '../auth/impl'
import { resolveHubUrl } from './endpoint'

/**
 * Fetching and serving one project's icon — the plain-HTTP half of Projects.
 *
 * Icons are files on disk, not inline in a `ProjectInfo`, and are never sent over
 * SignalR: pushing a binary blob through MessagePack on the one payload every
 * client refreshes on every connect would bloat it for everyone, for a picture
 * most screens do not even show at full size. So they live behind two small
 * authenticated HTTP endpoints next to the hub's existing `/push/vapid`.
 */

/** Where the hub serves one project's icon. Sibling of the hub path, not under it, like `/push/vapid`. */
function iconPath(projectId: string): string {
  const hub = new URL(resolveHubUrl())
  return `${hub.origin}/projects/${encodeURIComponent(projectId)}/icon`
}

/**
 * The URL for an `<img>` tag to load directly.
 *
 * The endpoint is authenticated, and a plain `<img>` cannot attach an
 * Authorization header, so the access token travels in the query string
 * instead — the same accommodation the hub already makes for the SignalR
 * handshake, extended to this one other path. `iconVersion` cache-busts: a
 * re-upload changes the URL, so the browser's HTTP cache can never serve last
 * week's icon under this week's name.
 *
 * Null when there is no custom icon (`iconVersion` is zero) or no token could be
 * obtained — both mean "show the app's own default icon instead".
 */
export async function projectIconUrl(
  projectId: string,
  iconVersion: number,
): Promise<string | null> {
  if (iconVersion <= 0) return null

  const token = await auth.getAccessToken()
  if (!token) return null

  const url = new URL(iconPath(projectId))
  url.searchParams.set('access_token', token)
  url.searchParams.set('v', String(iconVersion))
  return url.toString()
}

/** Resolves a project's icon URL for rendering, re-resolving whenever the version changes. */
export function useProjectIconUrl(projectId: string, iconVersion: number): string | null {
  const [url, setUrl] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    setUrl(null)
    void projectIconUrl(projectId, iconVersion).then((resolved) => {
      if (!cancelled) setUrl(resolved)
    })

    return () => {
      cancelled = true
    }
  }, [projectId, iconVersion])

  return url
}

/**
 * Uploads a new icon. The caller downscales to a reasonable square first — the
 * hub only enforces content type and a byte cap, not dimensions.
 *
 * Returns the bumped icon version on success, which is also what the
 * `ProjectUpdated` broadcast carries — this return exists only so the picker can
 * show the new picture immediately, without waiting on that round trip.
 */
export async function uploadProjectIcon(projectId: string, file: File): Promise<number | null> {
  const token = await auth.getAccessToken()
  if (!token) return null

  const response = await fetch(iconPath(projectId), {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': file.type,
    },
    body: file,
  })

  if (!response.ok) return null

  const body = (await response.json()) as { iconVersion?: unknown }
  return typeof body.iconVersion === 'number' ? body.iconVersion : null
}

/** Clears a project's custom icon, reverting it to the app's own default. */
export async function deleteProjectIcon(projectId: string): Promise<boolean> {
  const token = await auth.getAccessToken()
  if (!token) return false

  const response = await fetch(iconPath(projectId), {
    method: 'DELETE',
    headers: { Authorization: `Bearer ${token}` },
  })

  return response.ok
}
