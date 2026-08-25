export type VoiceLevel = 'projects' | 'sessions' | 'conversation'

export type VoiceActivity =
  | 'idle'
  | 'listening'
  | 'thinking'
  | 'speaking'
  | 'muted'
  | 'disconnected'
  | 'error'

export interface PendingTerminalConfirmation {
  text: string
  reason: string
}

export interface VoiceState {
  active: boolean
  activity: VoiceActivity
  level: VoiceLevel
  projectId: string | null
  machineId: string | null
  sessionId: string | null
  confirmation: PendingTerminalConfirmation | null
  error: string | null
}

export const initialVoiceState: VoiceState = {
  active: false,
  activity: 'idle',
  level: 'projects',
  projectId: null,
  machineId: null,
  sessionId: null,
  confirmation: null,
  error: null,
}

export type VoiceAction =
  | {
      type: 'start'
      level?: VoiceLevel
      projectId?: string | null
      machineId?: string | null
      sessionId?: string | null
    }
  | { type: 'stop' }
  | { type: 'activity'; activity: VoiceActivity }
  | { type: 'select-project'; projectId: string }
  | { type: 'select-session'; machineId: string; sessionId: string }
  | { type: 'back-to-sessions' }
  | { type: 'back-to-projects' }
  | { type: 'confirm-terminal'; text: string; reason: string }
  | { type: 'clear-confirmation' }
  | { type: 'error'; message: string }

export function voiceReducer(state: VoiceState, action: VoiceAction): VoiceState {
  switch (action.type) {
    case 'start': {
      const level = action.level ?? 'projects'
      return {
        active: true,
        activity: 'thinking',
        level,
        projectId: level === 'projects' ? null : (action.projectId ?? null),
        machineId: level === 'conversation' ? (action.machineId ?? null) : null,
        sessionId: level === 'conversation' ? (action.sessionId ?? null) : null,
        confirmation: null,
        error: null,
      }
    }
    case 'stop':
      return { ...initialVoiceState }
    case 'activity':
      return { ...state, activity: action.activity, error: null }
    case 'select-project':
      return {
        ...state,
        level: 'sessions',
        projectId: action.projectId,
        machineId: null,
        sessionId: null,
        confirmation: null,
        error: null,
      }
    case 'select-session':
      return {
        ...state,
        level: 'conversation',
        machineId: action.machineId,
        sessionId: action.sessionId,
        confirmation: null,
        error: null,
      }
    case 'back-to-sessions':
      return {
        ...state,
        level: state.projectId ? 'sessions' : 'projects',
        machineId: null,
        sessionId: null,
        confirmation: null,
        error: null,
      }
    case 'back-to-projects':
      return {
        ...state,
        level: 'projects',
        projectId: null,
        machineId: null,
        sessionId: null,
        confirmation: null,
        error: null,
      }
    case 'confirm-terminal':
      return {
        ...state,
        confirmation: { text: action.text, reason: action.reason },
        error: null,
      }
    case 'clear-confirmation':
      return { ...state, confirmation: null, error: null }
    case 'error':
      return { ...state, activity: 'error', error: action.message }
  }
}

export interface StoredVoiceLocation {
  projectId: string
  machineId: string | null
  sessionId: string | null
}

const STORAGE_KEY = '1remote.voice.location.v1'

export function loadVoiceLocation(storage: Pick<Storage, 'getItem'>): StoredVoiceLocation | null {
  try {
    const value = JSON.parse(storage.getItem(STORAGE_KEY) ?? 'null') as Partial<StoredVoiceLocation> | null
    if (!value || typeof value.projectId !== 'string') return null

    return {
      projectId: value.projectId,
      machineId: typeof value.machineId === 'string' ? value.machineId : null,
      sessionId: typeof value.sessionId === 'string' ? value.sessionId : null,
    }
  } catch {
    return null
  }
}

export function saveVoiceLocation(
  storage: Pick<Storage, 'setItem' | 'removeItem'>,
  location: StoredVoiceLocation | null,
): void {
  if (!location) {
    storage.removeItem(STORAGE_KEY)
    return
  }

  storage.setItem(STORAGE_KEY, JSON.stringify(location))
}
