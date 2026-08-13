import { Suspense, lazy, useCallback, useEffect, useState } from 'react'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'

import { signOut } from './auth/msal'
import { describeError } from './protocol/errors'
import type { MachineInfo, SessionInfo } from './protocol/wire'
import { readDeepLink, withoutDeepLink, type DeepLink } from './push/subscription'
import { usePushRegistration } from './push/usePush'
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

/**
 * The session named in the URL, taken out of the address bar as it is read.
 *
 * Consumed once, at start-up, so that closing the terminal and reloading — which
 * is what a backgrounded phone eventually does on its own — returns to the
 * machine list rather than reopening the session the user deliberately left.
 */
function takeDeepLink(): DeepLink | null {
  const link = readDeepLink(window.location.search)
  if (link) {
    window.history.replaceState(null, '', withoutDeepLink(window.location.href))
  }

  return link
}

export default function App() {
  const isAuthenticated = useIsAuthenticated()
  const { inProgress, accounts } = useMsal()
  const relay = useRelay(isAuthenticated)
  const [opened, setOpened] = useState<DeepLink | null>(takeDeepLink)

  const registerPush = usePushRegistration(relay.client, relay.status === 'connected')

  // A notification tapped while the app is already running. The service worker
  // sends a message rather than navigating, because navigating would drop the
  // live socket and make the user wait through a reconnect to answer a question
  // that is sitting on the screen right now.
  useEffect(() => {
    if (!('serviceWorker' in navigator)) return

    const onMessage = (event: MessageEvent) => {
      const data = event.data as { type?: string; url?: string } | undefined
      if (data?.type !== 'OPEN_SESSION' || typeof data.url !== 'string') return

      const link = readDeepLink(new URL(data.url, window.location.origin).search)
      if (link) setOpened(link)
    }

    navigator.serviceWorker.addEventListener('message', onMessage)
    return () => navigator.serviceWorker.removeEventListener('message', onMessage)
  }, [])

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

        <NotificationsCard onGranted={registerPush} />

        {/*
          A session that vanished while its terminal was open — the program exited
          and the list was refreshed — is worth saying out loud. Silently returning
          to the list looks like a mis-tap. Held back until the machine list has
          actually arrived: a notification tapped from a locked phone opens the app
          before the socket is up, and announcing that the session is gone while we
          are still finding out would be wrong on the one path that matters most.
        */}
        {opened && !session && relay.loaded ? (
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
