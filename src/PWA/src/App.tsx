import { Suspense, lazy, useCallback, useEffect, useRef, useState } from 'react'

import { auth } from './auth/impl'
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
  const { signedIn, busy, username } = auth.useSession()
  const relay = useRelay(signedIn)
  const [opened, setOpened] = useState<DeepLink | null>(takeDeepLink)

  // What the terminal was last given. See the note where it is used.
  const lastOpen = useRef<{ machine: MachineInfo; session: SessionInfo } | null>(null)

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

  if (!signedIn) {
    return <SignInScreen busy={busy} />
  }

  const machine = opened ? relay.machines.find((m) => m.machineId === opened.machineId) : undefined
  const session = machine?.sessions.find((s) => s.sessionId === opened?.sessionId)

  // Keep the terminal up after its session leaves the list.
  //
  // A session that ends is dropped from the next machine list, and the two things
  // arrive independently: the notification that says it ended, and the refreshed list
  // that no longer mentions it. If the list wins, the terminal is unmounted mid-sentence
  // and the last thing the program printed — the error, the test summary, the thing the
  // session was being watched for — is gone, replaced by a banner that explains nothing.
  //
  // So the view holds on to the description it was opened with and stays up until the
  // user leaves it deliberately. TerminalView already has its own, better account of a
  // session that has ended, and it keeps the screen.
  if (machine && session) {
    lastOpen.current = { machine, session }
  } else if (!opened || lastOpen.current?.session.sessionId !== opened.sessionId) {
    lastOpen.current = null
  }

  const showing = machine && session ? { machine, session } : lastOpen.current

  return (
    <div className="mx-auto flex min-h-dvh w-full max-w-xl flex-col">
      {/*
        Padding rather than a spacer above: the header is sticky with a blurred
        background, so the bar itself should extend under the status bar and only the
        text be pushed clear. A spacer would leave a transparent strip for content to
        scroll through.
      */}
      <header className="sticky top-0 z-10 flex items-center gap-1 border-b border-slate-800 bg-slate-950/80 pb-3 pl-[max(1rem,env(safe-area-inset-left))] pr-[max(1rem,env(safe-area-inset-right))] pt-[max(0.75rem,env(safe-area-inset-top))] backdrop-blur">
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
          onClick={() => void auth.signOut()}
          className="min-h-10 rounded-lg px-3 text-sm text-slate-400 transition active:bg-slate-800"
          title={username}
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
        {opened && !session && relay.loaded && !showing ? (
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

      {showing ? (
        <Suspense
          fallback={
            <div className="fixed inset-0 z-20 flex items-center justify-center bg-slate-950 text-sm text-slate-500">
              Opening {showing.session.displayName}…
            </div>
          }
        >
          <TerminalView
            client={relay.client}
            connected={relay.status === 'connected'}
            machine={showing.machine}
            session={showing.session}
            onClose={closeSession}
          />
        </Suspense>
      ) : null}
    </div>
  )
}
