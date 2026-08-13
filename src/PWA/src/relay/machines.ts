import type { MachineInfo, SessionInfo } from '../protocol/wire'

/**
 * The machine list, and the rules for keeping it true as notifications arrive.
 *
 * Kept as pure functions over plain data, separately from the connection and from
 * React, because these rules are where a remote-control app quietly lies to you:
 * showing a session that ended, or a machine that is really asleep. That is worth
 * testing directly rather than through a rendered component.
 */

export type Machines = readonly MachineInfo[]

/**
 * Online machines first, then by name.
 *
 * Not the order the hub happens to send. The list is the app's front door on a
 * phone screen that fits maybe four rows, and the machines you can actually use
 * belong above the ones you cannot.
 */
function ordered(machines: MachineInfo[]): MachineInfo[] {
  return [...machines].sort((a, b) => {
    if (a.online !== b.online) return a.online ? -1 : 1
    return a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' })
  })
}

/** Oldest first: the order a person started them in, which is the order they think in. */
function orderedSessions(sessions: SessionInfo[]): SessionInfo[] {
  return [...sessions].sort((a, b) => a.startedAt.getTime() - b.startedAt.getTime())
}

function withMachine(
  machines: Machines,
  machineId: string,
  update: (machine: MachineInfo) => MachineInfo,
): Machines {
  let changed = false

  const next = machines.map((machine) => {
    if (machine.machineId !== machineId) return machine
    changed = true
    return update(machine)
  })

  // A notification for a machine we have never heard of is dropped rather than
  // used to invent one. We would have no name, no OS and no online state for it,
  // and a row reading "unknown machine" is worse than a row that is not there;
  // the next ListMachines fills the gap anyway.
  return changed ? ordered(next) : machines
}

export function replaceAll(machines: MachineInfo[]): Machines {
  return ordered(
    machines.map((machine) => ({ ...machine, sessions: orderedSessions(machine.sessions) })),
  )
}

export function machineOnline(machines: Machines, machine: MachineInfo): Machines {
  const fresh = { ...machine, sessions: orderedSessions(machine.sessions) }
  const known = machines.some((m) => m.machineId === machine.machineId)

  return ordered(
    known ? machines.map((m) => (m.machineId === machine.machineId ? fresh : m)) : [...machines, fresh],
  )
}

/**
 * A machine whose agent dropped stays in the list, with nothing on it.
 *
 * Both halves matter. Keeping the row is how "your desk is asleep" stays
 * distinguishable from "you have no desk", which is the difference between waiting
 * and going to look for a bug. Dropping its sessions is not tidying up: a session
 * cannot outlive the wrapper that owns the pseudoconsole, so every one of them is
 * already gone, and leaving them listed would offer a tap that is guaranteed to
 * fail.
 */
export function machineOffline(machines: Machines, machineId: string): Machines {
  return withMachine(machines, machineId, (machine) => ({
    ...machine,
    online: false,
    sessions: [],
  }))
}

export function sessionOpened(
  machines: Machines,
  machineId: string,
  session: SessionInfo,
): Machines {
  return withMachine(machines, machineId, (machine) => {
    const others = machine.sessions.filter((s) => s.sessionId !== session.sessionId)

    return { ...machine, sessions: orderedSessions([...others, session]) }
  })
}

export function sessionClosed(machines: Machines, machineId: string, sessionId: string): Machines {
  return withMachine(machines, machineId, (machine) => ({
    ...machine,
    sessions: machine.sessions.filter((s) => s.sessionId !== sessionId),
  }))
}

/**
 * Flags a session as waiting on the user.
 *
 * The flag is the product: it is what turns "I should check on that build" into a
 * thing the phone tells you. It is cleared the moment the session writes anything,
 * which the hub reports as ordinary output.
 */
export function sessionAwaitingInput(
  machines: Machines,
  machineId: string,
  sessionId: string,
  awaiting: boolean,
): Machines {
  return withMachine(machines, machineId, (machine) => ({
    ...machine,
    sessions: machine.sessions.map((session) =>
      session.sessionId === sessionId ? { ...session, awaitingInput: awaiting } : session,
    ),
  }))
}

export function findSession(
  machines: Machines,
  machineId: string,
  sessionId: string,
): SessionInfo | undefined {
  return machines
    .find((machine) => machine.machineId === machineId)
    ?.sessions.find((session) => session.sessionId === sessionId)
}

export function totalSessions(machines: Machines): number {
  return machines.reduce((count, machine) => count + machine.sessions.length, 0)
}
