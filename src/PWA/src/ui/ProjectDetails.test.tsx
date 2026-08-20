import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import type { ProjectInfo } from '../protocol/wire'
import { ProjectDetails } from './ProjectDetails'

vi.mock('../relay/projectIcon', () => ({
  useProjectIconUrl: vi.fn(() => null),
}))

const project: ProjectInfo = {
  projectId: 'project-a',
  name: 'Project A',
  description: 'A useful project.',
  siteUrl: 'https://example.test/project-a',
  repoUrl: 'https://github.com/example/project-a',
  isGeneral: false,
  iconVersion: 0,
  createdAt: new Date('2026-08-20T06:00:00Z'),
}

describe('ProjectDetails', () => {
  afterEach(cleanup)

  it('shows project metadata and external links', () => {
    render(<ProjectDetails project={project} />)

    expect(screen.getByRole('heading', { name: 'Project A' })).toBeTruthy()
    expect(screen.getByText('A useful project.')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Project page' }).getAttribute('href')).toBe(
      project.siteUrl,
    )
    expect(screen.getByRole('link', { name: 'Repository' }).getAttribute('href')).toBe(
      project.repoUrl,
    )
  })
})
