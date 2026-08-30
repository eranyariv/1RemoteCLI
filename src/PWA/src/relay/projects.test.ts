import { describe, expect, it } from 'vitest'

import type { MachineInfo, ProjectInfo, SessionInfo } from '../protocol/wire'
import { replaceAll as replaceAllMachines } from './machines'
import {
  filterByProject,
  findProject,
  GENERAL_PROJECT_ID,
  projectStats,
  remove,
  replaceAll,
  suggestedProject,
  upsert,
  type Projects,
} from './projects'

function project(id: string, overrides: Partial<ProjectInfo> = {}): ProjectInfo {
  return {
    projectId: id,
    name: id,
    description: null,
    siteUrl: null,
    repoUrl: null,
    isGeneral: false,
    iconVersion: 0,
    createdAt: new Date('2024-05-17T09:00:00Z'),
    ...overrides,
  }
}

function session(id: string, overrides: Partial<SessionInfo> = {}): SessionInfo {
  return {
    sessionId: id,
    program: 'pwsh',
    args: [],
    cwd: 'C:\\Work',
    cols: 80,
    rows: 24,
    startedAt: new Date('2024-05-17T09:00:00Z'),
    displayName: 'PowerShell',
    awaitingInput: false,
    cliType: 'PowerShell',
    customName: null,
    pinned: false,
    kind: 'Terminal',
    projectId: null,
    chatCapabilities: null,
    suggestedProjectId: null,
    suggestedProjectMoves: 0,
    ...overrides,
  }
}

function machine(id: string, overrides: Partial<MachineInfo> = {}): MachineInfo {
  return {
    machineId: id,
    displayName: id,
    os: 'Microsoft Windows 10.0.26100',
    agentVersion: '1.0.0',
    online: true,
    sessions: [],
    ...overrides,
  }
}

describe('the project list', () => {
  it('puts General first regardless of name', () => {
    const projects = replaceAll([
      project('b', { name: 'Zebra' }),
      project(GENERAL_PROJECT_ID, { name: 'Aardvark', isGeneral: true }),
    ])

    expect(projects.map((p) => p.projectId)).toEqual([GENERAL_PROJECT_ID, 'b'])
  })

  it('sorts the rest by name, ignoring case', () => {
    const projects = replaceAll([
      project('1', { name: 'desk' }),
      project('2', { name: 'Attic' }),
      project('3', { name: 'Basement' }),
    ])

    expect(projects.map((p) => p.name)).toEqual(['Attic', 'Basement', 'desk'])
  })
})

describe('upsert', () => {
  it('adds a project not seen before', () => {
    const projects = upsert([], project('a'))

    expect(projects).toHaveLength(1)
    expect(projects[0].projectId).toBe('a')
  })

  it('replaces the stale copy rather than duplicating it', () => {
    const before: Projects = replaceAll([project('a', { name: 'Old name' })])
    const after = upsert(before, project('a', { name: 'New name' }))

    expect(after).toHaveLength(1)
    expect(after[0].name).toBe('New name')
  })
})

describe('remove', () => {
  it('drops the named project and leaves the rest', () => {
    const before = replaceAll([project('a'), project('b')])
    const after = remove(before, 'a')

    expect(after.map((p) => p.projectId)).toEqual(['b'])
  })

  it('ignores a project it has never heard of', () => {
    const before = replaceAll([project('a')])
    expect(remove(before, 'ghost')).toEqual(before)
  })
})

describe('findProject', () => {
  it('finds a project by id', () => {
    const projects = replaceAll([project('a'), project('b')])
    expect(findProject(projects, 'b')!.projectId).toBe('b')
  })

  it('treats null as General', () => {
    const projects = replaceAll([project(GENERAL_PROJECT_ID, { isGeneral: true }), project('a')])
    expect(findProject(projects, null)!.projectId).toBe(GENERAL_PROJECT_ID)
  })
})

describe('suggestedProject', () => {
  const general = project(GENERAL_PROJECT_ID, { name: 'General', isGeneral: true })

  it('matches an ACP working directory against a repository URL', () => {
    const candidate = project('remote', {
      name: 'Remote sessions',
      repoUrl: 'https://github.com/eranyariv/1RemoteCLI.git',
    })

    expect(
      suggestedProject(
        session('chat', { cwd: 'C:\\Users\\me\\.copilot\\repos\\1RemoteCLI', kind: 'AgentChat' }),
        [general, candidate],
      ),
    ).toBe(candidate)
  })

  it('matches a shell program path against a project name', () => {
    const candidate = project('terminal-tools', { name: 'Terminal Tools' })

    expect(
      suggestedProject(
        session('shell', { program: 'C:\\Tools\\terminal-tools\\pwsh.exe', cwd: 'C:\\' }),
        [general, candidate],
      ),
    ).toBe(candidate)
  })

  it('prefers a learned destination over an inferred path match', () => {
    const learned = project('learned', { name: 'Learned destination' })
    const inferred = project('inferred', { name: 'my-project' })

    expect(
      suggestedProject(
        session('shell', {
          cwd: 'C:\\Work\\my-project',
          suggestedProjectId: learned.projectId,
        }),
        [general, learned, inferred],
      ),
    ).toBe(learned)
  })

  it('does not suggest anything for an already mapped session', () => {
    const candidate = project('remote', { name: '1RemoteCLI' })

    expect(
      suggestedProject(
        session('mapped', { cwd: 'C:\\Work\\1RemoteCLI', projectId: 'somewhere' }),
        [general, candidate],
      ),
    ).toBeUndefined()
  })

  it('does not guess when two projects match equally well', () => {
    const first = project('first', { name: 'Remote CLI' })
    const second = project('second', { name: 'Remote-CLI' })

    expect(
      suggestedProject(session('ambiguous', { cwd: 'C:\\Work\\remote-cli' }), [
        general,
        first,
        second,
      ]),
    ).toBeUndefined()
  })

  it('requires a complete path component rather than a substring', () => {
    const candidate = project('remote', { name: 'RemoteCLI' })

    expect(
      suggestedProject(session('other', { cwd: 'C:\\Work\\MyRemoteCLIApp' }), [
        general,
        candidate,
      ]),
    ).toBeUndefined()
  })
})

describe('projectStats', () => {
  it('counts sessions and distinct machines for one project', () => {
    const machines = replaceAllMachines([
      machine('a', {
        sessions: [session('s1', { projectId: 'work' }), session('s2', { projectId: 'work' })],
      }),
      machine('b', { sessions: [session('s3', { projectId: 'work' })] }),
      machine('c', { sessions: [session('s4', { projectId: 'other' })] }),
    ])

    expect(projectStats(machines, 'work')).toEqual({
      sessionCount: 3,
      machineCount: 2,
      machineName: null,
      awaitingInputCount: 0,
    })
  })

  it('treats a null projectId as General', () => {
    const machines = replaceAllMachines([machine('a', { sessions: [session('s1', { projectId: null })] })])

    expect(projectStats(machines, GENERAL_PROJECT_ID).sessionCount).toBe(1)
  })

  it('counts sessions waiting for input', () => {
    const machines = replaceAllMachines([
      machine('a', {
        sessions: [
          session('s1', { projectId: 'work', awaitingInput: true }),
          session('s2', { projectId: 'work', awaitingInput: false }),
        ],
      }),
    ])

    expect(projectStats(machines, 'work').awaitingInputCount).toBe(1)
  })

  it('is all zero for a project with nothing running', () => {
    const machines = replaceAllMachines([machine('a', { sessions: [session('s1', { projectId: 'work' })] })])

    expect(projectStats(machines, 'idle')).toEqual({
      sessionCount: 0,
      machineCount: 0,
      machineName: null,
      awaitingInputCount: 0,
    })
  })

  it('names the machine when every session is on one machine', () => {
    const machines = replaceAllMachines([
      machine('desk', {
        displayName: 'Office desktop',
        sessions: [session('s1', { projectId: 'work' }), session('s2', { projectId: 'work' })],
      }),
    ])

    expect(projectStats(machines, 'work').machineName).toBe('Office desktop')
  })
})

describe('filterByProject', () => {
  it('narrows each machine to only the sessions in the selected project', () => {
    const machines = replaceAllMachines([
      machine('a', {
        sessions: [session('s1', { projectId: 'work' }), session('s2', { projectId: 'other' })],
      }),
    ])

    const scoped = filterByProject(machines, 'work')

    expect(scoped[0].sessions.map((s) => s.sessionId)).toEqual(['s1'])
  })

  it('keeps a machine with zero matching sessions, rather than dropping it', () => {
    const machines = replaceAllMachines([
      machine('a', { sessions: [session('s1', { projectId: 'other' })] }),
      machine('b', { online: false }),
    ])

    const scoped = filterByProject(machines, 'work')

    expect(scoped.map((m) => m.machineId)).toEqual(['a', 'b'])
    expect(scoped.find((m) => m.machineId === 'a')!.sessions).toEqual([])
  })
})
