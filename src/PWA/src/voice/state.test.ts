import { describe, expect, it } from 'vitest'

import { initialVoiceState, loadVoiceLocation, saveVoiceLocation, voiceReducer } from './state'

describe('voice state machine', () => {
  it('navigates down and back without forwarding stale confirmation state', () => {
    let state = voiceReducer(initialVoiceState, { type: 'start' })
    state = voiceReducer(state, { type: 'select-project', projectId: 'project-1' })
    state = voiceReducer(state, {
      type: 'select-session',
      machineId: 'machine-1',
      sessionId: 'session-1',
    })
    state = voiceReducer(state, {
      type: 'confirm-terminal',
      text: 'git reset --hard',
      reason: 'it discards changes',
    })

    state = voiceReducer(state, { type: 'back-to-sessions' })
    expect(state).toMatchObject({
      active: true,
      level: 'sessions',
      projectId: 'project-1',
      machineId: null,
      sessionId: null,
      confirmation: null,
    })

    state = voiceReducer(state, { type: 'back-to-projects' })
    expect(state).toMatchObject({ level: 'projects', projectId: null })

    state = voiceReducer(state, { type: 'stop' })
    expect(state).toEqual(initialVoiceState)
  })

  it('restores only a valid persisted location', () => {
    const values = new Map<string, string>()
    const storage = {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    }

    saveVoiceLocation(storage, {
      projectId: 'project-1',
      machineId: 'machine-1',
      sessionId: 'session-1',
    })
    expect(loadVoiceLocation(storage)).toEqual({
      projectId: 'project-1',
      machineId: 'machine-1',
      sessionId: 'session-1',
    })

    values.set('1remote.voice.location.v1', '{"projectId":42}')
    expect(loadVoiceLocation(storage)).toBeNull()
  })
})
