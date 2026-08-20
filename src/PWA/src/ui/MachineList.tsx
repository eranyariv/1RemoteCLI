import { useEffect, useRef, useState } from 'react'

import type { MachineInfo, SessionInfo } from '../protocol/wire'
import { pinnedSessions, sessionLabel, type Machines } from '../relay/machines'
import { GENERAL_PROJECT_ID, suggestedProject, type Projects } from '../relay/projects'
import { labelFor } from '../terminal/catalog'
import { shortOs, shortPath, uptime } from './format'
import { Empty } from './Chrome'
import { moveId, orderByPreference, useProjectLayout } from './preferences'
import { sortableStyle, useSortable } from './sortableItem'
import { SortableGrip, SortableList } from './sorting'

/** What the list can do to a session besides open it. */
export interface SessionActions {
  onRename(machineId: string, sessionId: string, name: string | null): void
  onPin(machineId: string, sessionId: string, pinned: boolean): void
  onMove(machineId: string, sessionId: string, projectId: string | null): void
}

/**
 * Rename and pin, opened from a row.
 *
 * A panel rather than two more controls on the row itself. The row is a single tap
 * target on purpose — it is tapped while walking — and a text field wedged into it
 * would turn every attempt to open a session into a coin flip.
 */
function SessionEditor({
  machineId,
  session,
  projects,
  actions,
  onClose,
}: {
  machineId: string
  session: SessionInfo
  projects: Projects
  actions: SessionActions
  onClose(): void
}) {
  const [draft, setDraft] = useState(session.customName ?? '')
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => {
    input.current?.focus()
    input.current?.select()
  }, [])

  // Trimmed to null, which is how the hub is told to clear the name rather than to
  // set an empty one -- the difference between showing the agent's name again and
  // showing nothing at all.
  const commit = () => {
    const next = draft.trim()
    const wanted = next.length > 0 ? next : null

    if (wanted !== (session.customName ?? null)) {
      actions.onRename(machineId, session.sessionId, wanted)
    }

    onClose()
  }

  return (
    <div className="mx-1.5 mb-1.5 rounded-lg bg-slate-800/50 p-2.5">
      <div className="flex items-center gap-2">
        <input
          ref={input}
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') commit()
            if (event.key === 'Escape') onClose()
          }}
          // The hub truncates to the same length; stopping here is friendlier than
          // silently storing something shorter than what was typed.
          maxLength={60}
          placeholder={session.displayName || session.program}
          aria-label="Session name"
          className="min-h-10 min-w-0 flex-1 rounded-lg border border-slate-700 bg-slate-900 px-2.5 text-[15px] text-slate-100 placeholder:text-slate-600 focus:border-slate-500 focus:outline-none"
        />

        <button
          type="button"
          onClick={commit}
          className="min-h-10 shrink-0 rounded-lg bg-slate-700 px-3 text-sm font-medium text-slate-100 transition active:bg-slate-600"
        >
          Save
        </button>
      </div>

      <div className="mt-2 flex items-center gap-2">
        <button
          type="button"
          onClick={() => {
            actions.onPin(machineId, session.sessionId, !session.pinned)
            onClose()
          }}
          className="min-h-10 rounded-lg px-2.5 text-sm text-slate-300 transition active:bg-slate-700"
        >
          {session.pinned ? 'Unpin' : 'Pin to top'}
        </button>

        {session.customName ? (
          <button
            type="button"
            onClick={() => {
              actions.onRename(machineId, session.sessionId, null)
              onClose()
            }}
            className="min-h-10 rounded-lg px-2.5 text-sm text-slate-400 transition active:bg-slate-700"
          >
            Reset name
          </button>
        ) : null}

        <span className="flex-1" />

        <button
          type="button"
          onClick={onClose}
          className="min-h-10 rounded-lg px-2.5 text-sm text-slate-400 transition active:bg-slate-700"
        >
          Cancel
        </button>
      </div>

      {/* Moving only makes sense once there is somewhere else to go. */}
      {projects.length > 1 ? (
        <label className="mt-2 flex items-center gap-2">
          <span className="shrink-0 text-sm text-slate-400">Project</span>
          <select
            value={session.projectId ?? GENERAL_PROJECT_ID}
            onChange={(event) => {
              const next = event.target.value
              actions.onMove(machineId, session.sessionId, next === GENERAL_PROJECT_ID ? null : next)
              onClose()
            }}
            aria-label="Move to project"
            className="min-h-10 min-w-0 flex-1 rounded-lg border border-slate-700 bg-slate-900 px-2.5 text-sm text-slate-100 focus:border-slate-500 focus:outline-none"
          >
            {projects.map((project) => (
              <option key={project.projectId} value={project.projectId}>
                {project.name}
              </option>
            ))}
          </select>
        </label>
      ) : null}
    </div>
  )
}

/**
 * One session.
 *
 * The row answers the only three questions worth asking from a phone: what is
 * running, where, and is it waiting for me. Everything else is a tap away.
 */
function SessionRow({
  machineId,
  session,
  projects,
  disabled,
  subtitle,
  actions,
  onOpen,
  dragHandle,
}: {
  machineId: string
  session: SessionInfo
  projects: Projects
  disabled: boolean
  /** Shown instead of the working directory, for rows lifted away from their machine. */
  subtitle?: string
  actions: SessionActions
  onOpen(session: SessionInfo): void
  dragHandle?: React.ReactNode
}) {
  // Re-rendered on a timer so "2m" does not sit there saying "2m" for an hour.
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(timer)
  }, [])

  const [editing, setEditing] = useState(false)
  const suggestion = suggestedProject(session, projects)
  const chatSource =
    session.kind === 'AgentChat'
      ? `${session.program.trim() || labelFor(session.cliType)} chat`
      : null

  return (
    <>
      <div className="flex items-center">
        {dragHandle}

        <button
          type="button"
          disabled={disabled}
          onClick={() => onOpen(session)}
          // min-h-14 keeps every row a comfortable thumb target; a list of terminals
          // is exactly the sort of thing people tap while walking.
          className="flex min-h-14 min-w-0 flex-1 items-center gap-3 rounded-lg px-3 py-2.5 text-left transition enabled:hover:bg-slate-800/70 enabled:active:bg-slate-800 disabled:opacity-40"
        >
          <span
            className={`size-2 shrink-0 rounded-full ${
              session.awaitingInput ? 'bg-amber-400' : 'bg-slate-600'
            }`}
            aria-hidden
          />

          <span className="min-w-0 flex-1">
            <span className="flex items-baseline gap-2">
              <span className="truncate text-[15px] font-medium text-slate-100">
                {sessionLabel(session)}
              </span>
              {session.awaitingInput ? (
                <span className="shrink-0 rounded-full bg-amber-400/15 px-2 py-0.5 text-[11px] font-medium text-amber-300">
                  waiting
                </span>
              ) : null}
            </span>
            <span className="mt-0.5 flex items-baseline gap-2">
              {session.kind === 'AgentChat' ? (
                <span className="shrink-0 rounded bg-violet-500/15 px-1.5 py-0.5 text-[11px] font-medium text-violet-300">
                  {chatSource}
                </span>
              ) : session.cliType === 'Generic' ? null : (
                <span className="shrink-0 rounded bg-slate-700/60 px-1.5 py-0.5 text-[11px] font-medium text-slate-300">
                  {labelFor(session.cliType)}
                </span>
              )}
              <span className="min-w-0 truncate font-mono text-xs text-slate-500">
                {subtitle ?? shortPath(session.cwd)}
              </span>
            </span>
          </span>

          <span className="shrink-0 text-xs tabular-nums text-slate-500">
            {session.kind === 'AgentChat' ? 'recent' : uptime(session.startedAt, now)}
          </span>
        </button>

        <button
          type="button"
          onClick={() => setEditing((open) => !open)}
          aria-label={`Rename or pin ${sessionLabel(session)}`}
          aria-expanded={editing}
          className="min-h-14 w-10 shrink-0 rounded-lg text-slate-500 transition hover:text-slate-300 active:bg-slate-800"
        >
          ⋯
        </button>
      </div>

      {suggestion ? (
        <div className="px-3 pb-2 pl-8">
          <button
            type="button"
            onClick={() =>
              actions.onMove(machineId, session.sessionId, suggestion.projectId)
            }
            className="min-h-8 rounded-full border border-sky-500/30 bg-sky-500/10 px-3 text-xs font-medium text-sky-300 transition hover:bg-sky-500/15 active:bg-sky-500/20"
          >
            Move to project {suggestion.name}
          </button>
        </div>
      ) : null}

      {editing ? (
        <SessionEditor
          machineId={machineId}
          session={session}
          projects={projects}
          actions={actions}
          onClose={() => setEditing(false)}
        />
      ) : null}
    </>
  )
}

function SortableSessionRow({
  machine,
  session,
  projects,
  actions,
  onOpenSession,
}: {
  machine: MachineInfo
  session: SessionInfo
  projects: Projects
  actions: SessionActions
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
}) {
  const sortable = useSortable({ id: session.sessionId })

  return (
    <div ref={sortable.setNodeRef} style={sortableStyle(sortable)}>
      <SessionRow
        machineId={machine.machineId}
        session={session}
        projects={projects}
        disabled={false}
        actions={actions}
        onOpen={(selected) => onOpenSession(machine, selected)}
        dragHandle={<SortableGrip sortable={sortable} label={`Reorder ${sessionLabel(session)}`} />}
      />
    </div>
  )
}

/**
 * Everything the user pinned, above every machine.
 *
 * Pinned sessions are lifted out of their machine cards rather than merely sorted
 * to the top of them. On the screen this app is for, the fold arrives after about
 * four rows, and a session pinned on the third machine down would otherwise still
 * be below it.
 */
function PinnedCard({
  machines,
  projects,
  actions,
  onOpenSession,
}: {
  machines: Machines
  projects: Projects
  actions: SessionActions
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
}) {
  const pinned = pinnedSessions(machines)
  if (pinned.length === 0) return null

  return (
    <section className="overflow-hidden rounded-2xl border border-slate-700 bg-slate-900/60">
      <header className="flex items-center gap-3 border-b border-slate-800 px-4 py-3">
        <span className="text-xs font-semibold uppercase tracking-wide text-slate-400">Pinned</span>
      </header>

      <div className="p-1.5">
        {pinned.map((entry) => {
          const machine = machines.find((m) => m.machineId === entry.machineId)

          if (!machine) return null

          return (
            <SessionRow
              key={`${entry.machineId}:${entry.session.sessionId}`}
              machineId={entry.machineId}
              session={entry.session}
              projects={projects}
              // A pinned session on a machine that went offline is already gone --
              // going offline clears its sessions -- so the only reachable state here
              // is online. The guard costs nothing and outlives that argument.
              disabled={!entry.online}
              subtitle={entry.machineName}
              actions={actions}
              onOpen={(session) => onOpenSession(machine, session)}
            />
          )
        })}
      </div>
    </section>
  )
}

function MachineCard({
  machine,
  sessionOrder,
  collapsed,
  projects,
  actions,
  onToggle,
  onMoveSession,
  onOpenSession,
}: {
  machine: MachineInfo
  sessionOrder: readonly string[]
  collapsed: boolean
  projects: Projects
  actions: SessionActions
  onToggle(): void
  onMoveSession(activeId: string, overId: string): void
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
}) {
  // Shown above, so not shown twice. The count in the header still includes them:
  // it answers "what is running on that machine", which pinning does not change.
  const listed = orderByPreference(
    machine.sessions,
    sessionOrder,
    (session) => session.sessionId,
  ).filter((session) => !session.pinned)
  const sortable = useSortable({ id: machine.machineId })

  return (
    <section
      ref={sortable.setNodeRef}
      style={sortableStyle(sortable)}
      className="overflow-hidden rounded-2xl border border-slate-800 bg-slate-900/60"
    >
      <header
        className={`flex items-center gap-2 px-3 py-3 ${collapsed ? '' : 'border-b border-slate-800'}`}
      >
        <button
          type="button"
          onClick={onToggle}
          aria-label={`${collapsed ? 'Expand' : 'Collapse'} ${machine.displayName}`}
          aria-expanded={!collapsed}
          className="flex min-h-10 w-9 shrink-0 items-center justify-center rounded-lg text-slate-500 transition hover:text-slate-300 active:bg-slate-800"
        >
          <svg
            viewBox="0 0 20 20"
            className={`size-5 transition-transform ${collapsed ? '-rotate-90' : ''}`}
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            aria-hidden
          >
            <path d="m5 7 5 5 5-5" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>

        <span
          className={`size-2.5 shrink-0 rounded-full ${
            machine.online ? 'bg-emerald-400' : 'bg-slate-600'
          }`}
          aria-hidden
        />

        <div className="min-w-0 flex-1">
          <h2 className="truncate text-[15px] font-semibold text-slate-100">
            {machine.displayName}
          </h2>
          <p className="truncate text-xs text-slate-500">
            {shortOs(machine.os)}
            {machine.online ? '' : ' · offline'}
          </p>
        </div>

        <span className="shrink-0 text-xs tabular-nums text-slate-500">
          {machine.sessions.length === 0
            ? '—'
            : `${machine.sessions.length} session${machine.sessions.length === 1 ? '' : 's'}`}
        </span>

        <SortableGrip sortable={sortable} label={`Reorder ${machine.displayName}`} />
      </header>

      {collapsed ? null : (
        <div className="p-1.5">
          {machine.online ? (
            listed.length > 0 ? (
              <SortableList ids={listed.map((session) => session.sessionId)} onMove={onMoveSession}>
                {listed.map((session) => (
                  <SortableSessionRow
                    key={session.sessionId}
                    machine={machine}
                    session={session}
                    projects={projects}
                    actions={actions}
                    onOpenSession={onOpenSession}
                  />
                ))}
              </SortableList>
            ) : machine.sessions.length > 0 ? (
              // Everything it has is pinned, and so is showing above. Saying "nothing
              // running" here would be a lie the user can see through.
              <p className="px-3 py-4 text-[13px] text-slate-500">
                {machine.sessions.length === 1 ? 'Its session is' : 'All of its sessions are'}{' '}
                pinned above.
              </p>
            ) : (
              <p className="px-3 py-4 text-[13px] text-slate-500">
                Nothing running. Start one with{' '}
                <code className="rounded bg-slate-800 px-1.5 py-0.5 font-mono text-xs text-slate-300">
                  1remote pwsh
                </code>{' '}
                at the machine.
              </p>
            )
          ) : (
            // Not "no sessions": a session cannot outlive its wrapper, so an offline
            // machine has none by definition, and saying so as though it were news
            // would imply the machine is up and idle.
            <p className="px-3 py-4 text-[13px] text-slate-500">
              The agent on this machine is not connected. Sessions reappear when it comes back.
            </p>
          )}
        </div>
      )}
    </section>
  )
}

export function MachineList({
  projectId,
  machines,
  projects,
  actions,
  onOpenSession,
}: {
  projectId: string
  machines: Machines
  projects: Projects
  actions: SessionActions
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
}) {
  const [layout, setLayout] = useProjectLayout(projectId)

  if (machines.length === 0) {
    return (
      <Empty title="No machines yet">
        Sign in on a Windows machine and run <code className="font-mono">1remote login</code>, then
        start a session with <code className="font-mono">1remote pwsh</code>.
      </Empty>
    )
  }

  const preferredMachines = orderByPreference(
    machines,
    layout.machineOrder,
    (machine) => machine.machineId,
  )
  const orderedMachines = [
    ...preferredMachines.filter((machine) => machine.sessions.length > 0),
    ...preferredMachines.filter((machine) => machine.sessions.length === 0),
  ]
  const activeMachines = orderedMachines.filter((machine) => machine.sessions.length > 0)
  const inactiveMachines = orderedMachines.filter((machine) => machine.sessions.length === 0)

  const moveMachine = (
    group: readonly MachineInfo[],
    activeId: string,
    overId: string,
  ) => {
    const groupOrder = moveId(
      group.map((machine) => machine.machineId),
      activeId,
      overId,
    )
    const activeOrder =
      group === activeMachines
        ? groupOrder
        : activeMachines.map((machine) => machine.machineId)
    const inactiveOrder =
      group === inactiveMachines
        ? groupOrder
        : inactiveMachines.map((machine) => machine.machineId)

    setLayout((current) => ({
      ...current,
      machineOrder: [...activeOrder, ...inactiveOrder],
    }))
  }

  const machineCard = (machine: MachineInfo) => (
    <MachineCard
      key={machine.machineId}
      machine={machine}
      sessionOrder={layout.sessionOrder[machine.machineId] ?? []}
      collapsed={layout.collapsedMachines[machine.machineId] ?? machine.sessions.length === 0}
      projects={projects}
      actions={actions}
      onToggle={() =>
        setLayout((current) => {
          const collapsed =
            current.collapsedMachines[machine.machineId] ?? machine.sessions.length === 0
          return {
            ...current,
            collapsedMachines: {
              ...current.collapsedMachines,
              [machine.machineId]: !collapsed,
            },
          }
        })
      }
      onMoveSession={(activeId, overId) =>
        setLayout((current) => {
          const listedIds = orderByPreference(
            machine.sessions.filter((session) => !session.pinned),
            current.sessionOrder[machine.machineId] ?? [],
            (session) => session.sessionId,
          ).map((session) => session.sessionId)

          return {
            ...current,
            sessionOrder: {
              ...current.sessionOrder,
              [machine.machineId]: moveId(listedIds, activeId, overId),
            },
          }
        })
      }
      onOpenSession={onOpenSession}
    />
  )

  return (
    <div className="flex flex-col gap-3">
      <PinnedCard machines={machines} projects={projects} actions={actions} onOpenSession={onOpenSession} />

      <SortableList
        ids={activeMachines.map((machine) => machine.machineId)}
        onMove={(activeId, overId) => moveMachine(activeMachines, activeId, overId)}
      >
        <div className="flex flex-col gap-3">
          {activeMachines.map(machineCard)}
        </div>
      </SortableList>

      <SortableList
        ids={inactiveMachines.map((machine) => machine.machineId)}
        onMove={(activeId, overId) => moveMachine(inactiveMachines, activeId, overId)}
      >
        <div className="flex flex-col gap-3">
          {inactiveMachines.map(machineCard)}
        </div>
      </SortableList>
    </div>
  )
}
