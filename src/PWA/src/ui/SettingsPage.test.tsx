import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { DefaultUserSettings } from '../settings/userSettings'
import { SettingsPage } from './SettingsPage'

describe('SettingsPage', () => {
  afterEach(cleanup)

  it('shows every density and previews the current selection', () => {
    render(
      <SettingsPage settings={DefaultUserSettings} onChange={vi.fn()} />,
    )

    expect(screen.getByRole('radio', { name: /Comfortable/ })).toBeTruthy()
    expect(screen.getByRole('radio', { name: /Compact/ }).hasAttribute('checked')).toBe(true)
    expect(screen.getByRole('radio', { name: /Dense/ })).toBeTruthy()
    expect(screen.getByText('Compact', { selector: 'span.text-xs' })).toBeTruthy()
  })

  // iOS rubber-bands a clipped scroller sideways even where its scroll width is
  // correct, which drags this screen off the left edge and crops it. The shared
  // .vertical-list-surface guard is what stops it; overflow-x: hidden alone does not.
  it('pins the screen against sideways panning', () => {
    const { container } = render(
      <SettingsPage settings={DefaultUserSettings} onChange={vi.fn()} />,
    )

    const screenRoot = container.querySelector('section[aria-label="User settings"]')
    expect(screenRoot?.classList.contains('vertical-list-surface')).toBe(true)
  })

  it('selects a density from its label', () => {
    const onChange = vi.fn()
    render(
      <SettingsPage settings={DefaultUserSettings} onChange={onChange} />,
    )

    fireEvent.click(screen.getByRole('radio', { name: /Dense/ }))
    expect(onChange).toHaveBeenCalledWith({ cliDensity: 'dense' })
  })

  it('edits terminal, voice, auto-listen, and notification preferences', () => {
    const onChange = vi.fn()
    render(<SettingsPage settings={DefaultUserSettings} onChange={onChange} />)

    fireEvent.click(screen.getByRole('checkbox', { name: /On-screen key bar/ }))
    fireEvent.click(screen.getByRole('checkbox', { name: /Latency line/ }))
    fireEvent.change(screen.getByRole('combobox', { name: 'Spoken language' }), {
      target: { value: 'he-IL' },
    })
    fireEvent.change(screen.getByRole('combobox', { name: 'Reply voice' }), {
      target: { value: 'en-US-AndrewMultilingualNeural' },
    })
    fireEvent.click(screen.getByRole('checkbox', { name: /Auto-listen/ }))
    fireEvent.click(screen.getByRole('checkbox', { name: /Waiting for input/ }))

    expect(onChange.mock.calls).toEqual([
      [{ showKeyBar: false }],
      [{ showLatency: false }],
      [{ speechLanguage: 'he-IL' }],
      [{ speechVoice: 'en-US-AndrewMultilingualNeural' }],
      [{ autoListen: false }],
      [{ notifyAwaitingInput: false }],
    ])
  })
})
