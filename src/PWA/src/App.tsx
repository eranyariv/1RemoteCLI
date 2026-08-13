import { useCallback, useState } from 'react'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'

import { signOut } from './auth/msal'
import { describeError } from './protocol/errors'
import type { MachineInfo, SessionInfo } from './protocol/wire'
import { useRelay } from './relay/useRelay'
import { Banner, StatusPill } from './ui/Chrome'
import { MachineList } from './ui/MachineList'
import { SignInScreen } from './ui/SignInScreen'

export default function App() {
  const isAuthenticated = useIsAuthenticated()
  const { inProgress, accounts } = useMsal()
  const relay = useRelay(isAuthenticated)
  const [opened, setOpened] = useState<{ machine: MachineInfo; session: SessionInfo } | null>(null)

  const openSession = useCallback((machine: MachineInfo, session: SessionInfo) => {
    // Task 1.10 replaces this with the terminal view. Until then it says so,
    // rather than silently doing nothing — which reads as a bug in a list whose
    // entire purpose is to be tapped.
    setOpened({ machine, session })
  }, [])

  if (!isAuthenticated) {
    return <SignInScreen busy={inProgress !== 'none'} />
  }

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

        {opened ? (
          <Banner
            tone="info"
            title={`${opened.session.displayName} on ${opened.machine.displayName}`}
          >
            The terminal view is not built yet. It arrives with the next task.
          </Banner>
        ) : null}
      </main>
    </div>
  )
}
