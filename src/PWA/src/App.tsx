import { Suspense, lazy, useCallback, useState } from 'react'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'

import { signOut } from './auth/msal'
import { describeError } from './protocol/errors'
import type { MachineInfo, SessionInfo } from './protocol/wire'
import { useRelay } from './relay/useRelay'
import { Banner, StatusPill } from './ui/Chrome'
import { MachineList } from './ui/MachineList'
import { NotificationsCard } from './ui/NotificationsCard'
import { SignInScreen } from './ui/SignInScreen'

/**
 * The terminal — xterm and its addons — is over half the bundle and is not needed
 * until something is tapped. On the cellular link this app is designed for, making
 * the first screen wait for it is the difference between "it opened" and "it is
 * loading". Splitting it means the machine list arrives immediately and the terminal
 * downloads while the user is deciding which session they want.
 */
const TerminalView = lazy(() =>
  import('./ui/TerminalView').then((module) => ({ default: module.TerminalView })),
)

export default function App() {
  const isAuthenticated = useIsAuthenticated()
  const { inProgress, accounts } = useMsal()
  const relay = useRelay(isAuthenticated)
  const [opened, setOpened] = useState<{ machineId: string; sessionId: string } | null>(null)

  // Held as ids, not objects: the machine list is replaced wholesale on every
  // refresh, and a captured object would freeze the terminal's idea of the session
  // at the moment it was opened — including whether the machine is still online.
  const openSession = useCallback((machine: MachineInfo, session: SessionInfo) => {
    setOpened({ machineId: machine.machineId, sessionId: session.sessionId })
  }, [])

  const closeSession = useCallback(() => setOpened(null), [])

  if (!isAuthenticated) {
    return <SignInScreen busy={inProgress !== 'none'} />
  }

  const machine = opened ? relay.machines.find((m) => m.machineId === opened.machineId) : undefined
  const session = machine?.sessions.find((s) => s.sessionId === opened?.sessionId)

  return (
    <div className="mx-auto flex min-h-dvh w-full max-w-xl flex-col">
      <header className="sticky top-0 z-10 flex items-center gap-1 border-b border-slate-800 bg-slate-950/80 px-4 py-3 backdrop-blur">
        <div className="min-w-0 flex-1">
          <h1 className="text-[15px] font-semibold text-slate-100">Machines</h1>
          <StatusPill status={relay.status} />
        </div>

        <button
          type="button"
          onClick={() => void relay.refresh()}
          className="min-h-10 rounded-lg px-3 text-sm text-slate-400 transition active:bg-slate-800"
        >
          Refresh
        </button>

        <button
          type="button"
          onClick={() => void signOut()}
          className="min-h-10 rounded-lg px-3 text-sm text-slate-400 transition active:bg-slate-800"
          title={accounts[0]?.username}
        >
          Sign out
        </button>
      </header>

      <main className="flex flex-1 flex-col gap-4 px-4 py-4 pb-[max(1rem,env(safe-area-inset-bottom))]">
        {relay.status === 'rejected' ? (
          <Banner tone="error" title="This account cannot use the hub">
            {relay.detail}
          </Banner>
        ) : null}

        {relay.status === 'offline' ? (
          <Banner
            tone="warning"
            title="Not connected to the hub"
            action={
              <button
                type="button"
                onClick={() => void relay.refresh()}
                className="min-h-10 rounded-lg border border-amber-400/40 px-4 text-sm"
              >
                Try again
              </button>
            }
          >
            {relay.detail ?? 'Your machines are unreachable until the connection comes back.'}
          </Banner>
        ) : null}

        {relay.lastError && relay.status !== 'rejected' ? (
          <Banner
            tone="error"
            title={describeError(relay.lastError.code, relay.lastError.message)}
            action={
              <button
                type="button"
                onClick={relay.dismissError}
                className="min-h-10 rounded-lg border border-rose-500/40 px-4 text-sm"
              >
                Dismiss
              </button>
            }
          />
        ) : null}

        <MachineList machines={relay.machines} onOpenSession={openSession} />

        <NotificationsCard />

        {/*
          A session that vanished while its terminal was open — the program exited
          and the list was refreshed — is worth saying out loud. Silently returning
          to the list looks like a mis-tap.
        */}
        {opened && !session ? (
          <Banner
            tone="info"
            title="That session is no longer running"
            action={
              <button
                type="button"
                onClick={closeSession}
                className="min-h-10 rounded-lg border border-sky-500/40 px-4 text-sm"
              >
                Dismiss
              </button>
            }
          >
            It ended, or its machine went offline.
          </Banner>
        ) : null}
      </main>

      {machine && session ? (
        <Suspense
          fallback={
            <div className="fixed inset-0 z-20 flex items-center justify-center bg-slate-950 text-sm text-slate-500">
              Opening {session.displayName}…
            </div>
          }
        >
          <TerminalView
            client={relay.client}
            connected={relay.status === 'connected'}
            machine={machine}
            session={session}
            onClose={closeSession}
          />
        </Suspense>
      ) : null}
    </div>
  )
}
