import { act, cleanup, renderHook } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { DefaultUserSettings, readUserSettings, useUserSettings } from './userSettings'

describe('per-user settings', () => {
  beforeEach(() => window.localStorage.clear())
  afterEach(cleanup)

  it('defaults CLI density to compact', () => {
    expect(readUserSettings('person@example.test')).toEqual(DefaultUserSettings)
  })

  it('persists the selected density across remounts', () => {
    const first = renderHook(() => useUserSettings('person@example.test'))
    act(() => first.result.current.updateSettings({ cliDensity: 'dense' }))
    first.unmount()

    const second = renderHook(() => useUserSettings('person@example.test'))
    expect(second.result.current.settings.cliDensity).toBe('dense')
  })

  it('keeps settings separate for different signed-in users', () => {
    const first = renderHook(() => useUserSettings('first@example.test'))
    act(() => first.result.current.updateSettings({ cliDensity: 'comfortable' }))
    first.unmount()

    expect(readUserSettings('first@example.test').cliDensity).toBe('comfortable')
    expect(readUserSettings('second@example.test').cliDensity).toBe('compact')
  })

  it('falls back safely when stored settings are invalid', () => {
    window.localStorage.setItem(
      '1remote.user-settings.v1:person%40example.test',
      JSON.stringify({ cliDensity: 'microscopic' }),
    )

    expect(readUserSettings('Person@example.test')).toEqual(DefaultUserSettings)
  })

  it('fills newly added settings when reading an older saved value', () => {
    window.localStorage.setItem(
      '1remote.user-settings.v1:person%40example.test',
      JSON.stringify({ cliDensity: 'dense' }),
    )

    expect(readUserSettings('person@example.test')).toEqual({
      ...DefaultUserSettings,
      cliDensity: 'dense',
    })
  })
})
