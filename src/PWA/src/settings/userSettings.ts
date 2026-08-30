import { useCallback, useEffect, useState } from 'react'

export type CliDensity = 'comfortable' | 'compact' | 'dense'
export type SpeechLanguage = 'en-US' | 'en-GB' | 'he-IL'
export type SpeechVoice = 'en-US-AvaMultilingualNeural' | 'en-US-AndrewMultilingualNeural'

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

export const SpeechLanguages: Record<SpeechLanguage, string> = {
  'en-US': 'English (United States)',
  'en-GB': 'English (United Kingdom)',
  'he-IL': 'Hebrew (Israel)',
}

export const SpeechVoices: Record<SpeechVoice, string> = {
  'en-US-AvaMultilingualNeural': 'Ava',
  'en-US-AndrewMultilingualNeural': 'Andrew',
}

export interface UserSettings {
  cliDensity: CliDensity
  showKeyBar: boolean
  showLatency: boolean
  speechLanguage: SpeechLanguage
  speechVoice: SpeechVoice
  autoListen: boolean
  notifyAwaitingInput: boolean
  notifySessionFinished: boolean
  notifyAnnouncements: boolean
}

export const DefaultUserSettings: UserSettings = {
  cliDensity: 'compact',
  showKeyBar: true,
  showLatency: true,
  speechLanguage: 'en-US',
  speechVoice: 'en-US-AvaMultilingualNeural',
  autoListen: true,
  notifyAwaitingInput: true,
  notifySessionFinished: true,
  notifyAnnouncements: true,
}

const StoragePrefix = '1remote.user-settings.v1:'

function storageKey(username: string | undefined): string | null {
  const normalized = username?.trim().toLocaleLowerCase()
  return normalized ? `${StoragePrefix}${encodeURIComponent(normalized)}` : null
}

function isCliDensity(value: unknown): value is CliDensity {
  return value === 'comfortable' || value === 'compact' || value === 'dense'
}

function isSpeechLanguage(value: unknown): value is SpeechLanguage {
  return value === 'en-US' || value === 'en-GB' || value === 'he-IL'
}

function isSpeechVoice(value: unknown): value is SpeechVoice {
  return (
    value === 'en-US-AvaMultilingualNeural' ||
    value === 'en-US-AndrewMultilingualNeural'
  )
}

function boolean(value: unknown, fallback: boolean): boolean {
  return typeof value === 'boolean' ? value : fallback
}

export function readUserSettings(username: string | undefined): UserSettings {
  const key = storageKey(username)
  if (!key) return DefaultUserSettings

  try {
    const raw = window.localStorage.getItem(key)
    if (!raw) return DefaultUserSettings

    const value = JSON.parse(raw) as Record<string, unknown>
    return {
      cliDensity: isCliDensity(value.cliDensity)
        ? value.cliDensity
        : DefaultUserSettings.cliDensity,
      showKeyBar: boolean(value.showKeyBar, DefaultUserSettings.showKeyBar),
      showLatency: boolean(value.showLatency, DefaultUserSettings.showLatency),
      speechLanguage: isSpeechLanguage(value.speechLanguage)
        ? value.speechLanguage
        : DefaultUserSettings.speechLanguage,
      speechVoice: isSpeechVoice(value.speechVoice)
        ? value.speechVoice
        : DefaultUserSettings.speechVoice,
      autoListen: boolean(value.autoListen, DefaultUserSettings.autoListen),
      notifyAwaitingInput: boolean(
        value.notifyAwaitingInput,
        DefaultUserSettings.notifyAwaitingInput,
      ),
      notifySessionFinished: boolean(
        value.notifySessionFinished,
        DefaultUserSettings.notifySessionFinished,
      ),
      notifyAnnouncements: boolean(
        value.notifyAnnouncements,
        DefaultUserSettings.notifyAnnouncements,
      ),
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

  const updateSettings = useCallback(
    (changes: Partial<UserSettings>) => {
      const next = { ...settings, ...changes }
      writeUserSettings(username, next)
      setStored({ key, settings: next })
    },
    [key, settings, username],
  )

  return { settings, updateSettings }
}
