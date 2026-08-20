import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'

import { VersionLine } from './Chrome'

describe('VersionLine', () => {
  afterEach(cleanup)

  it('links to the public change history', () => {
    render(<VersionLine />)

    const link = screen.getByRole('link', { name: 'Change history' })
    expect(link.getAttribute('href')).toBe('/change-history.html')
    expect(link.getAttribute('target')).toBe('_blank')
  })
})
