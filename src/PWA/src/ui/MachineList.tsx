import { useEffect, useState } from 'react'

import type { MachineInfo, SessionInfo } from '../protocol/wire'
import { shortOs, shortPath, uptime } from './format'
import { Empty } from './Chrome'

/**
 * One session.
 *
 * The row answers the only three questions worth asking from a phone: what is
 * running, where, and is it waiting for me. Everything else is a tap away.
 */
function SessionRow({
  session,
  disabled,
  onOpen,
}: {
  session: SessionInfo
  disabled: boolean
  onOpen(session: SessionInfo): void
}) {
  // Re-rendered on a timer so "2m" does not sit there saying "2m" for an hour.
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(timer)
  }, [])

  return (
    <button
      type="button"
      disabled={disabled}
      onClick={() => onOpen(session)}
      // min-h-14 keeps every row a comfortable thumb target; a list of terminals
      // is exactly the sort of thing people tap while walking.
      className="flex min-h-14 w-full items-center gap-3 rounded-lg px-3 py-2.5 text-left transition enabled:hover:bg-slate-800/70 enabled:active:bg-slate-800 disabled:opacity-40"
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
            {session.displayName}
          </span>
          {session.awaitingInput ? (
            <span className="shrink-0 rounded-full bg-amber-400/15 px-2 py-0.5 text-[11px] font-medium text-amber-300">
              waiting
            </span>
          ) : null}
        </span>
        <span className="mt-0.5 block truncate font-mono text-xs text-slate-500">
          {shortPath(session.cwd)}
        </span>
      </span>

      <span className="shrink-0 text-xs tabular-nums text-slate-500">
        {uptime(session.startedAt, now)}
      </span>
    </button>
  )
}

function MachineCard({
  machine,
  onOpenSession,
}: {
  machine: MachineInfo
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
}) {
  return (
    <section className="overflow-hidden rounded-2xl border border-slate-800 bg-slate-900/60">
      <header className="flex items-center gap-3 border-b border-slate-800 px-4 py-3">
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
      </header>

      <div className="p-1.5">
        {machine.online ? (
          machine.sessions.length > 0 ? (
            machine.sessions.map((session) => (
              <SessionRow
                key={session.sessionId}
                session={session}
                disabled={false}
                onOpen={(s) => onOpenSession(machine, s)}
              />
            ))
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
    </section>
  )
}

export function MachineList({
  machines,
  onOpenSession,
}: {
  machines: readonly MachineInfo[]
  onOpenSession(machine: MachineInfo, session: SessionInfo): void
}) {
  if (machines.length === 0) {
    return (
      <Empty title="No machines yet">
        Sign in on a Windows machine and run <code className="font-mono">1remote login</code>, then
        start a session with <code className="font-mono">1remote pwsh</code>.
      </Empty>
    )
  }

  return (
    <div className="flex flex-col gap-3">
      {machines.map((machine) => (
        <MachineCard key={machine.machineId} machine={machine} onOpenSession={onOpenSession} />
      ))}
    </div>
  )
}
