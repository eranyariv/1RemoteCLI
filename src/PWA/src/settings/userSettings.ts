import { useCallback, useEffect, useState } from 'react'

export type CliDensity = 'comfortable' | 'compact' | 'dense'

export interface CliDensityDefinition {
  label: string
  description: string
  fontSize: number
  lineHeight: number
}

export const CliDensities: Record<CliDensity, CliDensityDefinition> = {
  comfortable: {
    label: 'Comfortable',
    description: 'Larger text with the most breathing room.',
    fontSize: 12,
    lineHeight: 1.2,
  },
  compact: {
    label: 'Compact',
    description: 'A balanced view with more terminal columns and rows.',
    fontSize: 11,
    lineHeight: 1.15,
  },
  dense: {
    label: 'Dense',
    description: 'The most terminal content, with smaller, tighter text.',
    fontSize: 10,
    lineHeight: 1.1,
  },
}

export interface UserSettings {
  cliDensity: CliDensity
}

export const DefaultUserSettings: UserSettings = {
  cliDensity: 'compact',
}

const StoragePrefix = '1remote.user-settings.v1:'

function storageKey(username: string | undefined): string | null {
  const normalized = username?.trim().toLocaleLowerCase()
  return normalized ? `${StoragePrefix}${encodeURIComponent(normalized)}` : null
}

function isCliDensity(value: unknown): value is CliDensity {
  return value === 'comfortable' || value === 'compact' || value === 'dense'
}

export function readUserSettings(username: string | undefined): UserSettings {
  const key = storageKey(username)
  if (!key) return DefaultUserSettings

  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return DefaultUserSettings

    const value = JSON.parse(raw) as { cliDensity?: unknown }
    return {
      cliDensity: isCliDensity(value.cliDensity)
        ? value.cliDensity
        : DefaultUserSettings.cliDensity,
    }
  } catch {
    return DefaultUserSettings
  }
}

function writeUserSettings(username: string | undefined, settings: UserSettings): void {
  const key = storageKey(username)
  if (!key) return

  try {
    window.localStorage.setItem(key, JSON.stringify(settings))
  } catch {
    // Storage can be unavailable in private browsing. The in-memory setting still works.
  }
}

export function useUserSettings(username: string | undefined) {
  const key = storageKey(username)
  const [stored, setStored] = useState(() => ({
    key,
    settings: readUserSettings(username),
  }))

  const settings = stored.key === key ? stored.settings : readUserSettings(username)

  useEffect(() => {
    setStored({ key, settings: readUserSettings(username) })
  }, [key, username])

  const setCliDensity = useCallback(
    (cliDensity: CliDensity) => {
      const next = { cliDensity }
      writeUserSettings(username, next)
      setStored({ key, settings: next })
    },
    [key, username],
  )

  return { settings, setCliDensity }
}
