import type { MachineInfo, ProjectInfo, SessionInfo } from '../protocol/wire'
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

const GENERIC_PATH_NAMES = new Set([
  'code',
  'home',
  'project',
  'projects',
  'repo',
  'repos',
  'source',
  'src',
  'work',
])

function pathPattern(value: string): RegExp | null {
  const chunks = value.match(/[a-z0-9]+/gi)
  if (!chunks || chunks.join('').length < 4) return null
  if (chunks.length === 1 && GENERIC_PATH_NAMES.has(chunks[0].toLowerCase())) return null

  const expression = chunks
    .map((chunk) => chunk.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    .join('[\\s._-]*')

  return new RegExp(`(?:^|[^a-z0-9])${expression}(?:$|[^a-z0-9])`, 'i')
}

function repoName(repoUrl: string | null): string | null {
  if (!repoUrl) return null

  try {
    const segments = new URL(repoUrl).pathname.split('/').filter(Boolean)
    return segments.at(-1)?.replace(/\.git$/i, '') ?? null
  } catch {
    return null
  }
}

/**
 * Suggests one unambiguous project whose name or repository name appears as a
 * complete component in an unmapped session's working/program path.
 */
export function suggestedProject(
  session: SessionInfo,
  projects: Projects,
): ProjectInfo | undefined {
  if (session.projectId !== null && session.projectId !== GENERAL_PROJECT_ID) return undefined

  if (session.suggestedProjectId) {
    const learned = projects.find(
      (project) => !project.isGeneral && project.projectId === session.suggestedProjectId,
    )
    if (learned) return learned
  }

  const paths = [session.cwd, session.program, ...session.args].join('\n')
  const matches = projects
    .filter((project) => !project.isGeneral)
    .map((project) => {
      const repository = repoName(project.repoUrl)
      const repoMatches = repository ? (pathPattern(repository)?.test(paths) ?? false) : false
      const nameMatches = pathPattern(project.name)?.test(paths) ?? false
      return { project, score: (repoMatches ? 2 : 0) + (nameMatches ? 1 : 0) }
    })
    .filter((match) => match.score > 0)
    .sort((a, b) => b.score - a.score)

  if (matches.length === 0 || matches[0].score === matches[1]?.score) return undefined
  return matches[0].project
}

/** What a project tile shows besides its name — computed here, not on the hub. */
export interface ProjectStats {
  sessionCount: number
  machineCount: number
  machineName: string | null
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

  const machineName =
    machineIds.size === 1
      ? (machines.find((machine) => machineIds.has(machine.machineId))?.displayName ?? null)
      : null

  return { sessionCount, machineCount: machineIds.size, machineName, awaitingInputCount }
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
