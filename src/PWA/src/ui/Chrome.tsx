import type { RelayStatus } from '../relay/client'

const LABELS: Record<RelayStatus, string> = {
  'signed-out': 'Signed out',
  connecting: 'Connecting…',
  connected: 'Connected',
  reconnecting: 'Reconnecting…',
  rejected: 'Not allowed',
  offline: 'No connection',
}

const TONES: Record<RelayStatus, string> = {
  'signed-out': 'bg-slate-500',
  connecting: 'bg-amber-400 animate-pulse',
  connected: 'bg-emerald-400',
  reconnecting: 'bg-amber-400 animate-pulse',
  rejected: 'bg-rose-500',
  offline: 'bg-rose-500',
}

/**
 * The connection, stated plainly and permanently.
 *
 * A remote terminal that has quietly lost its connection looks exactly like one
 * where nothing is happening, so the state of the link is never hidden behind a
 * transient toast.
 */
export function StatusPill({ status }: { status: RelayStatus }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-slate-400">
      <span className={`size-2 rounded-full ${TONES[status]}`} aria-hidden />
      {LABELS[status]}
    </span>
  )
}

export function Banner({
  tone,
  title,
  children,
  action,
}: {
  tone: 'error' | 'warning' | 'info'
  title: string
  children?: React.ReactNode
  action?: React.ReactNode
}) {
  const tones = {
    error: 'border-rose-500/40 bg-rose-500/10 text-rose-200',
    warning: 'border-amber-400/40 bg-amber-400/10 text-amber-100',
    info: 'border-slate-600 bg-slate-800/60 text-slate-300',
  }

  return (
    <div className={`rounded-xl border px-4 py-3 text-sm ${tones[tone]}`} role="status">
      <p className="font-medium">{title}</p>
      {children ? <p className="mt-1 text-[13px] opacity-90">{children}</p> : null}
      {action ? <div className="mt-3">{action}</div> : null}
    </div>
  )
}

export function Empty({ title, children }: { title: string; children?: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-dashed border-slate-700 px-5 py-10 text-center">
      <p className="text-sm font-medium text-slate-300">{title}</p>
      {children ? <p className="mt-2 text-[13px] text-slate-500">{children}</p> : null}
    </div>
  )
}
