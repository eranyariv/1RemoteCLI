import { useProjectIconUrl } from '../relay/projectIcon'
import { projectStats, type Projects } from '../relay/projects'
import type { Machines } from '../relay/machines'
import type { ProjectInfo } from '../protocol/wire'
import { useProjectOrder } from './preferences'
import { sortableStyle, useSortable } from './sortableItem'
import { SortableGrip, SortableList } from './sorting'

/** The app's own icon, shown for every project that has not uploaded a custom one. */
const DEFAULT_ICON = '/icon-192.png'

export function ProjectIcon({
  project,
  className = 'size-12 rounded-xl',
}: {
  project: ProjectInfo
  className?: string
}) {
  const url = useProjectIconUrl(project.projectId, project.iconVersion)

  return (
    <img
      src={url ?? DEFAULT_ICON}
      alt=""
      aria-hidden
      className={`${className} shrink-0 bg-slate-800 object-cover`}
    />
  )
}

/**
 * One project, as a large tile rather than a row.
 *
 * The home screen's whole job is "which of my projects needs me right now", and
 * that is answered by the icon and the waiting count before a single line of
 * text is read — the same reason a phone's home screen is icons, not a menu.
 */
function ProjectTile({
  project,
  machines,
  onOpen,
  onEdit,
}: {
  project: ProjectInfo
  machines: Machines
  onOpen(projectId: string): void
  onEdit(project: ProjectInfo): void
}) {
  const stats = projectStats(machines, project.projectId)
  const sortable = useSortable({ id: project.projectId })

  return (
    <div
      ref={sortable.setNodeRef}
      style={sortableStyle(sortable)}
      className="flex items-center rounded-2xl border border-slate-800 bg-slate-900/60"
    >
      <SortableGrip sortable={sortable} label={`Reorder ${project.name}`} />

      <button
        type="button"
        onClick={() => onOpen(project.projectId)}
        className="flex min-h-20 min-w-0 flex-1 items-center gap-3 rounded-2xl px-4 py-3 text-left transition active:bg-slate-800/70"
      >
        <ProjectIcon project={project} />

        <span className="min-w-0 flex-1">
          <span className="flex items-baseline gap-2">
            <span className="truncate text-[15px] font-semibold text-slate-100">
              {project.name}
            </span>
            {stats.awaitingInputCount > 0 ? (
              <span className="shrink-0 rounded-full bg-amber-400/15 px-2 py-0.5 text-[11px] font-medium text-amber-300">
                {stats.awaitingInputCount} waiting
              </span>
            ) : null}
          </span>

          <span className="mt-0.5 block truncate text-xs text-slate-500">
            {stats.sessionCount === 0
              ? 'Nothing running'
              : `${stats.sessionCount} session${stats.sessionCount === 1 ? '' : 's'} on ${
                  stats.machineCount === 1 ? stats.machineName : `${stats.machineCount} machines`
                }`}
          </span>
        </span>
      </button>

      <button
        type="button"
        onClick={() => onEdit(project)}
        aria-label={`Edit ${project.name}`}
        className="min-h-20 w-11 shrink-0 rounded-2xl text-slate-500 transition hover:text-slate-300 active:bg-slate-800"
      >
        ⋯
      </button>
    </div>
  )
}

/**
 * The home screen: every project as a tile, in the user's saved order.
 *
 * Loading and "no projects yet" are not distinguished here — every user always
 * has General, seeded by the hub the moment their account is first seen, so an
 * empty list before the first refresh looks no different from a slow connection
 * and needs no separate empty state of its own.
 */
export function ProjectTiles({
  projects,
  machines,
  onOpen,
  onEdit,
  onCreate,
}: {
  projects: Projects
  machines: Machines
  onOpen(projectId: string): void
  onEdit(project: ProjectInfo): void
  onCreate(): void
}) {
  const projectIds = projects.map((project) => project.projectId)
  const preference = useProjectOrder(projectIds)
  const byId = new Map(projects.map((project) => [project.projectId, project]))
  const ordered = preference.order.flatMap((id) => {
    const project = byId.get(id)
    return project ? [project] : []
  })

  return (
    <div className="flex flex-col gap-2">
      <SortableList ids={preference.order} onMove={preference.move}>
        <div className="flex flex-col gap-2">
          {ordered.map((project) => (
            <ProjectTile
              key={project.projectId}
              project={project}
              machines={machines}
              onOpen={onOpen}
              onEdit={onEdit}
            />
          ))}
        </div>
      </SortableList>

      <button
        type="button"
        onClick={onCreate}
        className="flex min-h-16 items-center justify-center gap-2 rounded-2xl border border-dashed border-slate-700 text-sm font-medium text-slate-400 transition active:bg-slate-800/60"
      >
        + New project
      </button>
    </div>
  )
}
