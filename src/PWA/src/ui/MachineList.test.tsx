import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { MachineInfo, ProjectInfo, SessionInfo } from '../protocol/wire'
import { MachineList, type SessionActions } from './MachineList'

const project: ProjectInfo = {
  projectId: 'project-a',
  name: 'Project A',
  description: null,
  siteUrl: null,
  repoUrl: null,
  isGeneral: false,
  iconVersion: 0,
  createdAt: new Date('2026-08-20T06:00:00Z'),
}

function session(id: string): SessionInfo {
  return {
    sessionId: id,
    program: 'pwsh',
    args: [],
    cwd: 'C:\\Work',
    cols: 80,
    rows: 24,
    startedAt: new Date('2026-08-20T06:00:00Z'),
    displayName: id,
    awaitingInput: false,
    cliType: 'PowerShell',
    customName: null,
    pinned: false,
    kind: 'Terminal',
    projectId: project.projectId,
  }
}

function machine(id: string, sessions: SessionInfo[] = []): MachineInfo {
  return {
    machineId: id,
    displayName: id,
    os: 'Microsoft Windows 11',
    agentVersion: '1.0.0',
    online: true,
    sessions,
  }
}

const actions: SessionActions = {
  onRename: vi.fn(),
  onPin: vi.fn(),
  onMove: vi.fn(),
}

describe('MachineList project layout', () => {
  beforeEach(() => window.localStorage.clear())
  afterEach(cleanup)

  it('lists active machines first and collapses empty machines by default', () => {
    render(
      <MachineList
        projectId={project.projectId}
        machines={[machine('Idle'), machine('Active', [session('session-a')])]}
        projects={[project]}
        actions={actions}
        onOpenSession={vi.fn()}
      />,
    )

    expect(screen.getAllByRole('heading', { level: 2 }).map((heading) => heading.textContent)).toEqual([
      'Active',
      'Idle',
    ])
    expect(screen.getByRole('button', { name: 'Collapse Active' })).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Expand Idle' })).toBeTruthy()
  })

  it('persists an explicit expansion across remounts', async () => {
    const props = {
      projectId: project.projectId,
      machines: [machine('Idle')],
      projects: [project],
      actions,
      onOpenSession: vi.fn(),
    }
    const first = render(<MachineList {...props} />)

    fireEvent.click(screen.getByRole('button', { name: 'Expand Idle' }))
    await waitFor(() =>
      expect(window.localStorage.getItem('1remote.project-layout.v1:project-a')).toContain(
        '"Idle":false',
      ),
    )

    first.unmount()
    render(<MachineList {...props} />)
    expect(screen.getByRole('button', { name: 'Collapse Idle' })).toBeTruthy()
  })
})
