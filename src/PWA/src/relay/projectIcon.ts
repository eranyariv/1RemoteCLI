import { useEffect, useState } from 'react'

import { auth } from '../auth/impl'
import { resolveHubUrl } from './endpoint'

/**
 * The hub stores project icons as authenticated files rather than embedding binary
 * data in the project list sent to every connected client.
 */
function iconPath(projectId: string): string {
  const hub = new URL(resolveHubUrl())
  return `${hub.origin}/projects/${encodeURIComponent(projectId)}/icon`
}

function bearerHeaders(token: string): { Authorization: string } {
  return { Authorization: ['Bearer', token].join(' ') }
}

/**
 * Fetches an icon with an authorization header and exposes only a local object URL,
 * keeping access tokens out of browser and proxy logs.
 */
export async function projectIconUrl(
  projectId: string,
  iconVersion: number,
): Promise<string | null> {
  if (iconVersion <= 0) return null

  const token = await auth.getAccessToken()
  if (!token) return null

  const url = new URL(iconPath(projectId))
  url.searchParams.set('v', String(iconVersion))

  const response = await fetch(url, { headers: bearerHeaders(token) })
  if (!response.ok) return null

  return URL.createObjectURL(await response.blob())
}

/** Resolves a project's icon URL for rendering, revoking it when it is replaced. */
export function useProjectIconUrl(projectId: string, iconVersion: number): string | null {
  const [url, setUrl] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    let objectUrl: string | null = null

    setUrl(null)
    void projectIconUrl(projectId, iconVersion).then((resolved) => {
      if (cancelled) {
        if (resolved) URL.revokeObjectURL(resolved)
        return
      }

      objectUrl = resolved
      setUrl(resolved)
    })

    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [projectId, iconVersion])

  return url
}

/**
 * Uploads a caller-downscaled icon. The hub independently checks its content type,
 * signature, and byte limit.
 */
export async function uploadProjectIcon(projectId: string, file: File): Promise<number | null> {
  const token = await auth.getAccessToken()
  if (!token) return null

  const response = await fetch(iconPath(projectId), {
    method: 'POST',
    headers: {
      ...bearerHeaders(token),
      'Content-Type': file.type,
    },
    body: file,
  })

  if (!response.ok) return null

  const body = (await response.json()) as { iconVersion?: unknown }
  return typeof body.iconVersion === 'number' ? body.iconVersion : null
}

/** Clears a project's custom icon, reverting it to the project's built-in default. */
export async function deleteProjectIcon(projectId: string): Promise<boolean> {
  const token = await auth.getAccessToken()
  if (!token) return false

  const response = await fetch(iconPath(projectId), {
    method: 'DELETE',
    headers: bearerHeaders(token),
  })

  return response.ok
}
