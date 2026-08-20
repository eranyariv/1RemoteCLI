import { useEffect, useState } from 'react'

const ProjectOrderKey = '1remote.project-order.v1'
const ProjectLayoutPrefix = '1remote.project-layout.v1:'

export interface ProjectLayout {
  machineOrder: string[]
  sessionOrder: Record<string, string[]>
  collapsedMachines: Record<string, boolean>
}

const EmptyProjectLayout: ProjectLayout = {
  machineOrder: [],
  sessionOrder: {},
  collapsedMachines: {},
}

function stringArray(value: unknown): string[] | null {
  if (!Array.isArray(value) || !value.every((entry) => typeof entry === 'string')) return null
  return value
}

function readJson(key: string): unknown {
  try {
    const value = window.localStorage.getItem(key)
    return value === null ? null : JSON.parse(value)
  } catch {
    return null
  }
}

function writeJson(key: string, value: unknown): void {
  try {
    window.localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // Storage can be unavailable in private browsing. The in-memory preference still works.
  }
}

function readProjectOrder(): string[] {
  return stringArray(readJson(ProjectOrderKey)) ?? []
}

function projectLayoutKey(projectId: string): string {
  return `${ProjectLayoutPrefix}${projectId}`
}

function readProjectLayout(projectId: string): ProjectLayout {
  const value = readJson(projectLayoutKey(projectId))
  if (!value || typeof value !== 'object' || Array.isArray(value)) return EmptyProjectLayout

  const candidate = value as Record<string, unknown>
  const machineOrder = stringArray(candidate.machineOrder)
  const sessionOrderValue = candidate.sessionOrder
  const collapsedValue = candidate.collapsedMachines

  if (
    !machineOrder ||
    !sessionOrderValue ||
    typeof sessionOrderValue !== 'object' ||
    Array.isArray(sessionOrderValue) ||
    !collapsedValue ||
    typeof collapsedValue !== 'object' ||
    Array.isArray(collapsedValue)
  ) {
    return EmptyProjectLayout
  }

  const sessionOrder: Record<string, string[]> = {}
  for (const [machineId, order] of Object.entries(sessionOrderValue)) {
    const parsed = stringArray(order)
    if (parsed) sessionOrder[machineId] = parsed
  }

  const collapsedMachines: Record<string, boolean> = {}
  for (const [machineId, collapsed] of Object.entries(collapsedValue)) {
    if (typeof collapsed === 'boolean') collapsedMachines[machineId] = collapsed
  }

  return { machineOrder, sessionOrder, collapsedMachines }
}

/** Keeps known preferences, drops missing ids from the view, and appends new ids. */
export function reconcileOrder(preferred: readonly string[], available: readonly string[]): string[] {
  const availableIds = new Set(available)
  const seen = new Set<string>()
  const result: string[] = []

  for (const id of preferred) {
    if (!availableIds.has(id) || seen.has(id)) continue
    seen.add(id)
    result.push(id)
  }

  for (const id of available) {
    if (seen.has(id)) continue
    seen.add(id)
    result.push(id)
  }

  return result
}

export function moveId(order: readonly string[], activeId: string, overId: string): string[] {
  const from = order.indexOf(activeId)
  const to = order.indexOf(overId)
  if (from < 0 || to < 0 || from === to) return [...order]

  const next = [...order]
  const [moved] = next.splice(from, 1)
  next.splice(to, 0, moved)
  return next
}

export function orderByPreference<T>(
  items: readonly T[],
  preferred: readonly string[],
  idFor: (item: T) => string,
): T[] {
  const byId = new Map(items.map((item) => [idFor(item), item]))
  return reconcileOrder(preferred, items.map(idFor)).flatMap((id) => {
    const item = byId.get(id)
    return item === undefined ? [] : [item]
  })
}

export function useProjectOrder(projectIds: readonly string[]) {
  const [preferred, setPreferred] = useState(readProjectOrder)
  const order = reconcileOrder(preferred, projectIds)

  useEffect(() => writeJson(ProjectOrderKey, preferred), [preferred])

  return {
    order,
    move(activeId: string, overId: string) {
      setPreferred(moveId(order, activeId, overId))
    },
  }
}

export function useProjectLayout(projectId: string) {
  const [layout, setLayout] = useState<ProjectLayout>(() => readProjectLayout(projectId))

  useEffect(() => writeJson(projectLayoutKey(projectId), layout), [layout, projectId])

  return [layout, setLayout] as const
}
