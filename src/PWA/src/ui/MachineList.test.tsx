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

const general: ProjectInfo = {
  ...project,
  projectId: 'general',
  name: 'General',
  isGeneral: true,
}

function session(id: string, overrides: Partial<SessionInfo> = {}): SessionInfo {
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
    chatCapabilities: null,
    suggestedProjectId: null,
    suggestedProjectMoves: 0,
    chatState: 'Unknown',
    ...overrides,
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
  beforeEach(() => {
    window.localStorage.clear()
    vi.clearAllMocks()
  })
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

  it('offers an inferred project move only for an unmapped General session', () => {
    const candidate = {
      ...project,
      projectId: 'remote-cli',
      name: '1RemoteCLI',
      repoUrl: 'https://github.com/eranyariv/1RemoteCLI',
    }
    const unmapped = session('session-a', {
      cwd: 'C:\\Users\\me\\.copilot\\repos\\1RemoteCLI',
      projectId: null,
    })

    render(
      <MachineList
        projectId={general.projectId}
        machines={[machine('Active', [unmapped])]}
        projects={[general, candidate]}
        actions={actions}
        onOpenSession={vi.fn()}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Move to project 1RemoteCLI' }))

    expect(actions.onMove).toHaveBeenCalledWith('Active', 'session-a', 'remote-cli', 'Suggested')
  })

  it('offers a learned manual destination even when the path does not imply it', () => {
    const learned = {
      ...project,
      projectId: 'learned',
      name: 'Learned destination',
    }
    const unmapped = session('session-a', {
      cwd: 'C:\\Unrelated',
      projectId: null,
      suggestedProjectId: learned.projectId,
    })

    render(
      <MachineList
        projectId={general.projectId}
        machines={[machine('Active', [unmapped])]}
        projects={[general, learned]}
        actions={actions}
        onOpenSession={vi.fn()}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Move to project Learned destination' }))
    expect(actions.onMove).toHaveBeenCalledWith('Active', 'session-a', 'learned', 'Suggested')
  })

  it('offers an automatic rule after more than three accepted matching suggestions', () => {
    const learned = {
      ...project,
      projectId: 'learned',
      name: 'Learned destination',
    }
    const unmapped = session('session-a', {
      projectId: null,
      suggestedProjectId: learned.projectId,
      suggestedProjectMoves: 4,
    })

    render(
      <MachineList
        projectId={general.projectId}
        machines={[machine('Active', [unmapped])]}
        projects={[general, learned]}
        actions={actions}
        onOpenSession={vi.fn()}
      />,
    )

    fireEvent.click(
      screen.getByRole('button', { name: 'Always move to project Learned destination' }),
    )
    expect(actions.onMove).toHaveBeenCalledWith('Active', 'session-a', 'learned', 'Always')
  })

  it('labels an agent chat with the same provider source as agent settings', () => {
    const chat = session('Release and deploy', {
      program: 'GitHub Copilot',
      cliType: 'CopilotCli',
      kind: 'AgentChat',
    })

    render(
      <MachineList
        projectId={project.projectId}
        machines={[machine('Active', [chat])]}
        projects={[project]}
        actions={actions}
        onOpenSession={vi.fn()}
      />,
    )

    expect(screen.getByText('GitHub Copilot chat')).toBeTruthy()
    expect(screen.queryByText('Chat')).toBeNull()
  })
})
