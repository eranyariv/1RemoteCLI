import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { PortraitGuard } from './PortraitGuard'

describe('PortraitGuard', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('makes the app inert in landscape and restores focus in portrait', async () => {
    let landscape = false
    vi.stubGlobal(
      'matchMedia',
      vi.fn(() => ({ matches: landscape }) as MediaQueryList),
    )

    render(
      <PortraitGuard>
        <button type="button">App action</button>
      </PortraitGuard>,
    )

    const appAction = screen.getByRole('button', { name: 'App action' })
    appAction.focus()

    landscape = true
    fireEvent(window, new Event('resize'))

    expect(appAction.parentElement?.hasAttribute('inert')).toBe(true)
    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('dialog')))

    landscape = false
    fireEvent(window, new Event('resize'))

    expect(screen.queryByRole('dialog')).toBeNull()
    expect(appAction.parentElement?.hasAttribute('inert')).toBe(false)
    expect(document.activeElement).toBe(appAction)
  })
})
