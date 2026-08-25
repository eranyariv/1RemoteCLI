import { describe, expect, it } from 'vitest'

import type { MachineInfo, SessionInfo } from '../protocol/wire'
import {
  findSession,
  machineOffline,
  machineOnline,
  pinnedSessions,
  replaceAll,
  sessionAwaitingInput,
  sessionClosed,
  sessionLabel,
  sessionOpened,
  totalSessions,
  type Machines,
} from './machines'

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

describe('the machine list', () => {
  it('puts machines you can use above machines you cannot', () => {
    const machines = replaceAll([
      machine('a', { displayName: 'Asleep', online: false }),
      machine('z', { displayName: 'Zebra', online: true }),
    ])

    expect(machines.map((m) => m.displayName)).toEqual(['Zebra', 'Asleep'])
  })

  it('sorts machines by name within the same state, ignoring case', () => {
    const machines = replaceAll([
      machine('1', { displayName: 'desk' }),
      machine('2', { displayName: 'Attic' }),
      machine('3', { displayName: 'Basement' }),
    ])

    expect(machines.map((m) => m.displayName)).toEqual(['Attic', 'Basement', 'desk'])
  })

  it('lists sessions oldest first, the order they were started in', () => {
    const machines = replaceAll([
      machine('a', {
        sessions: [
          session('second', { startedAt: new Date('2024-05-17T11:00:00Z') }),
          session('first', { startedAt: new Date('2024-05-17T09:00:00Z') }),
        ],
      }),
    ])

    expect(machines[0].sessions.map((s) => s.sessionId)).toEqual(['first', 'second'])
  })
})

describe('a machine coming online', () => {
  it('adds one we have not seen before', () => {
    const machines = machineOnline([], machine('new'))

    expect(machines).toHaveLength(1)
    expect(machines[0].machineId).toBe('new')
  })

  it('replaces the stale copy rather than duplicating it', () => {
    const before: Machines = replaceAll([
      machine('a', { displayName: 'Desk', online: false, agentVersion: '0.9.0' }),
    ])

    const after = machineOnline(before, machine('a', { displayName: 'Desk', agentVersion: '1.0.0' }))

    expect(after).toHaveLength(1)
    expect(after[0].online).toBe(true)
    expect(after[0].agentVersion).toBe('1.0.0')
  })
})

describe('a machine going offline', () => {
  const before = replaceAll([machine('a', { sessions: [session('s1'), session('s2')] })])

  it('keeps the machine, so "asleep" stays distinguishable from "gone"', () => {
    const after = machineOffline(before, 'a')

    expect(after).toHaveLength(1)
    expect(after[0].online).toBe(false)
  })

  it('drops its sessions, because none of them still exist', () => {
    // A session cannot outlive the wrapper that owns its pseudoconsole, so
    // keeping them listed would offer a tap guaranteed to fail.
    expect(machineOffline(before, 'a')[0].sessions).toEqual([])
  })

  it('ignores a machine it has never heard of', () => {
    expect(machineOffline(before, 'unknown')).toBe(before)
  })
})

describe('sessions', () => {
  const before = replaceAll([machine('a'), machine('b')])

  it('appears on the machine that reported it, and nowhere else', () => {
    const after = sessionOpened(before, 'a', session('s1'))

    expect(after.find((m) => m.machineId === 'a')!.sessions).toHaveLength(1)
    expect(after.find((m) => m.machineId === 'b')!.sessions).toHaveLength(0)
  })

  it('is not duplicated when the same session is reported twice', () => {
    // The agent republishes its whole session list on every reconnect, so a
    // repeat is the normal path rather than an error.
    const once = sessionOpened(before, 'a', session('s1'))
    const twice = sessionOpened(once, 'a', session('s1', { cols: 120 }))

    const sessions = twice.find((m) => m.machineId === 'a')!.sessions
    expect(sessions).toHaveLength(1)
    expect(sessions[0].cols).toBe(120)
  })

  it('is dropped for a machine we do not know, rather than inventing one', () => {
    expect(sessionOpened(before, 'ghost', session('s1'))).toBe(before)
  })

  it('disappears when it closes', () => {
    const opened = sessionOpened(before, 'a', session('s1'))
    const closed = sessionClosed(opened, 'a', 's1')

    expect(closed.find((m) => m.machineId === 'a')!.sessions).toEqual([])
  })

  it('survives a close for a session that is already gone', () => {
    expect(() => sessionClosed(before, 'a', 'never-existed')).not.toThrow()
  })
})

describe('the waiting-for-input flag', () => {
  const before = sessionOpened(replaceAll([machine('a')]), 'a', session('s1'))

  it('is set on exactly the session named', () => {
    const withTwo = sessionOpened(before, 'a', session('s2'))
    const after = sessionAwaitingInput(withTwo, 'a', 's1', true)

    expect(findSession(after, 'a', 's1')!.awaitingInput).toBe(true)
    expect(findSession(after, 'a', 's2')!.awaitingInput).toBe(false)
  })

  it('can be cleared again', () => {
    const set = sessionAwaitingInput(before, 'a', 's1', true)

    expect(findSession(sessionAwaitingInput(set, 'a', 's1', false), 'a', 's1')!.awaitingInput).toBe(
      false,
    )
  })
})

describe('counting', () => {
  it('totals sessions across every machine', () => {
    const machines = replaceAll([
      machine('a', { sessions: [session('s1'), session('s2')] }),
      machine('b', { sessions: [session('s3')] }),
    ])

    expect(totalSessions(machines)).toBe(3)
  })
})

describe('pinning', () => {
  it('gathers pinned sessions from every machine', () => {
    const machines = replaceAll([
      machine('a', { displayName: 'Desk', sessions: [session('s1', { pinned: true }), session('s2')] }),
      machine('b', { displayName: 'Attic', sessions: [session('s3', { pinned: true })] }),
    ])

    expect(pinnedSessions(machines).map((entry) => entry.session.sessionId).sort()).toEqual([
      's1',
      's3',
    ])
  })

  it('carries the machine along, because the row is shown away from it', () => {
    const machines = replaceAll([
      machine('a', { displayName: 'Desk', sessions: [session('s1', { pinned: true })] }),
    ])

    expect(pinnedSessions(machines)[0]).toMatchObject({ machineId: 'a', machineName: 'Desk' })
  })

  it('orders pinned sessions oldest first, like every other list', () => {
    const machines = replaceAll([
      machine('a', {
        sessions: [session('late', { pinned: true, startedAt: new Date('2024-05-17T11:00:00Z') })],
      }),
      machine('b', {
        sessions: [session('early', { pinned: true, startedAt: new Date('2024-05-17T09:00:00Z') })],
      }),
    ])

    expect(pinnedSessions(machines).map((entry) => entry.session.sessionId)).toEqual([
      'early',
      'late',
    ])
  })

  it('has nothing to show when nothing is pinned', () => {
    expect(pinnedSessions(replaceAll([machine('a', { sessions: [session('s1')] })]))).toEqual([])
  })
})

describe('what a session is called', () => {
  it('prefers the name the user typed', () => {
    expect(sessionLabel(session('s1', { customName: 'The deploy' }))).toBe('The deploy')
  })

  it('falls back to the agent name once the user clears theirs', () => {
    expect(sessionLabel(session('s1', { customName: null, displayName: 'PowerShell' }))).toBe(
      'PowerShell',
    )
  })

  it('falls back to the program when there is no name at all', () => {
    expect(sessionLabel(session('s1', { customName: null, displayName: '', program: 'pwsh' }))).toBe(
      'pwsh',
    )
  })
})
