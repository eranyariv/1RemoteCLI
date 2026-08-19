import type { MachineInfo, ProjectInfo } from '../protocol/wire'
import type { Machines } from './machines'

/**
 * The project list, and the rules for keeping it true as notifications arrive.
 *
 * Mirrors `machines.ts`: pure functions over plain data, kept separate from the
 * connection and from React so the rules that decide what a project tile shows
 * are worth testing directly.
 */

export type Projects = readonly ProjectInfo[]

/** Must match `ProjectStore.GeneralProjectId` on the hub. */
export const GENERAL_PROJECT_ID = 'general'

/** General first, then the rest by name — the same "the fixed thing anchors the list" rule as pinned sessions. */
function ordered(projects: ProjectInfo[]): ProjectInfo[] {
  return [...projects].sort((a, b) => {
    if (a.isGeneral !== b.isGeneral) return a.isGeneral ? -1 : 1
    return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
  })
}

export function replaceAll(projects: ProjectInfo[]): Projects {
  return ordered(projects)
}

export function upsert(projects: Projects, project: ProjectInfo): Projects {
  const known = projects.some((p) => p.projectId === project.projectId)

  return ordered(
    known
      ? projects.map((p) => (p.projectId === project.projectId ? project : p))
      : [...projects, project],
  )
}

export function remove(projects: Projects, projectId: string): Projects {
  return projects.filter((p) => p.projectId !== projectId)
}

export function findProject(projects: Projects, projectId: string | null): ProjectInfo | undefined {
  return projects.find((p) => p.projectId === (projectId ?? GENERAL_PROJECT_ID))
}

/** What a project tile shows besides its name — computed here, not on the hub. */
export interface ProjectStats {
  sessionCount: number
  machineCount: number
  awaitingInputCount: number
}

/**
 * Folds the current machine list into per-project counts.
 *
 * Computed client-side rather than pushed by the hub: the PWA already holds the
 * full machine/session list to render the existing screen, so this is always
 * exactly as fresh as that list already is, with no extra fan-out message and no
 * new thing on the hub that can disagree with what the list itself shows.
 */
export function projectStats(machines: Machines, projectId: string): ProjectStats {
  const machineIds = new Set<string>()
  let sessionCount = 0
  let awaitingInputCount = 0

  for (const machine of machines) {
    for (const session of machine.sessions) {
      if ((session.projectId ?? GENERAL_PROJECT_ID) !== projectId) continue

      sessionCount += 1
      machineIds.add(machine.machineId)
      if (session.awaitingInput) awaitingInputCount += 1
    }
  }

  return { sessionCount, machineCount: machineIds.size, awaitingInputCount }
}

/**
 * The existing machine list, scoped to one project's sessions.
 *
 * Machines are kept even when none of their sessions match — an offline machine
 * still belongs on screen for the same reason it does on the unfiltered list —
 * only the sessions shown under each are narrowed.
 */
export function filterByProject(machines: Machines, projectId: string): Machines {
  return machines.map((machine): MachineInfo => ({
    ...machine,
    sessions: machine.sessions.filter(
      (session) => (session.projectId ?? GENERAL_PROJECT_ID) === projectId,
    ),
  }))
}
