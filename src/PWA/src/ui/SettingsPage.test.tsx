import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { SettingsPage } from './SettingsPage'

describe('SettingsPage', () => {
  afterEach(cleanup)

  it('shows every density and previews the current selection', () => {
    render(
      <SettingsPage settings={{ cliDensity: 'compact' }} onDensityChange={vi.fn()} />,
    )

    expect(screen.getByRole('radio', { name: /Comfortable/ })).toBeTruthy()
    expect(screen.getByRole('radio', { name: /Compact/ }).hasAttribute('checked')).toBe(true)
    expect(screen.getByRole('radio', { name: /Dense/ })).toBeTruthy()
    expect(screen.getByText('Compact', { selector: 'span.text-xs' })).toBeTruthy()
  })

  it('selects a density from its label', () => {
    const onDensityChange = vi.fn()
    render(
      <SettingsPage settings={{ cliDensity: 'compact' }} onDensityChange={onDensityChange} />,
    )

    fireEvent.click(screen.getByRole('radio', { name: /Dense/ }))
    expect(onDensityChange).toHaveBeenCalledWith('dense')
  })
})
