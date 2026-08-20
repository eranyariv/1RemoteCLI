import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ProjectInfo } from '../protocol/wire'
import type { RelayClient } from '../relay/client'
import { ProjectEditor } from './ProjectEditor'

const iconMocks = vi.hoisted(() => ({
  deleteProjectIcon: vi.fn(),
  downscaleToSquare: vi.fn(),
  uploadProjectIcon: vi.fn(),
  useProjectIconUrl: vi.fn(() => null),
}))

vi.mock('../relay/projectIcon', () => ({
  deleteProjectIcon: iconMocks.deleteProjectIcon,
  uploadProjectIcon: iconMocks.uploadProjectIcon,
  useProjectIconUrl: iconMocks.useProjectIconUrl,
}))

vi.mock('./icon', () => ({
  downscaleToSquare: iconMocks.downscaleToSquare,
}))

const createdProject: ProjectInfo = {
  projectId: 'project-1',
  name: 'New project',
  description: null,
  siteUrl: null,
  repoUrl: null,
  isGeneral: false,
  iconVersion: 0,
  createdAt: new Date('2026-08-20T06:00:00Z'),
}

describe('ProjectEditor', () => {
  beforeEach(() => {
    iconMocks.downscaleToSquare.mockReset()
    iconMocks.uploadProjectIcon.mockReset()
    iconMocks.useProjectIconUrl.mockReturnValue(null)
    Object.defineProperty(URL, 'createObjectURL', {
      configurable: true,
      value: vi.fn(() => 'blob:project-icon'),
    })
    Object.defineProperty(URL, 'revokeObjectURL', {
      configurable: true,
      value: vi.fn(),
    })
  })

  afterEach(cleanup)

  it('shows the dedicated fallback icon when editing General', () => {
    const generalProject = { ...createdProject, name: 'General', isGeneral: true }
    const { container } = render(
      <ProjectEditor client={{} as RelayClient} project={generalProject} onClose={vi.fn()} />,
    )

    expect(container.querySelector('img')?.getAttribute('src')).toBe('/general-project.png')
  })

  it('uploads an icon selected while creating after the project has an id', async () => {
    const processedIcon = new File(['processed'], 'project.webp', { type: 'image/webp' })
    iconMocks.downscaleToSquare.mockResolvedValue(processedIcon)
    iconMocks.uploadProjectIcon.mockResolvedValue(1)

    const createProject = vi.fn(async () => ({ project: createdProject, error: null }))
    const onClose = vi.fn()
    const client = { createProject } as unknown as RelayClient

    render(<ProjectEditor client={client} onClose={onClose} />)

    expect(screen.getByRole('button', { name: 'Choose icon' })).toBeTruthy()

    const sourceIcon = new File(['source'], 'source.png', { type: 'image/png' })
    fireEvent.change(screen.getByLabelText('Project icon'), { target: { files: [sourceIcon] } })

    await waitFor(() => expect(iconMocks.downscaleToSquare).toHaveBeenCalledWith(sourceIcon))
    expect(iconMocks.uploadProjectIcon).not.toHaveBeenCalled()

    fireEvent.change(screen.getByPlaceholderText('Project name'), {
      target: { value: 'New project' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(onClose).toHaveBeenCalledOnce())
    expect(createProject).toHaveBeenCalledWith('New project', null, null, null)
    expect(iconMocks.uploadProjectIcon).toHaveBeenCalledWith('project-1', processedIcon)
    expect(createProject.mock.invocationCallOrder[0]).toBeLessThan(
      iconMocks.uploadProjectIcon.mock.invocationCallOrder[0],
    )
  })

  it.each([
    ['Site URL', 'example.com', 'Site URL must be a complete http:// or https:// address.'],
    [
      'GitHub repo URL',
      'github.com/o/r',
      'GitHub repo URL must be a complete http:// or https:// address.',
    ],
  ])('explains an invalid %s before calling the hub', async (label, value, message) => {
    const createProject = vi.fn()
    const client = { createProject } as unknown as RelayClient

    render(<ProjectEditor client={client} onClose={vi.fn()} />)

    fireEvent.change(screen.getByPlaceholderText('Project name'), {
      target: { value: 'New project' },
    })
    fireEvent.change(screen.getByLabelText(label), { target: { value } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByText(message)).toBeTruthy()
    expect(createProject).not.toHaveBeenCalled()
  })

  it('renders a field-specific validation error returned by the hub', async () => {
    const client = {
      createProject: vi.fn(async () => ({
        project: null,
        error: 'invalid_project_repo_url',
      })),
    } as unknown as RelayClient

    render(<ProjectEditor client={client} onClose={vi.fn()} />)

    fireEvent.change(screen.getByPlaceholderText('Project name'), {
      target: { value: 'New project' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(
      await screen.findByText('GitHub repo URL must be a complete http:// or https:// address.'),
    ).toBeTruthy()
  })
})
