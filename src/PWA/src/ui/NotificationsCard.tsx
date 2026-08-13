import { useCallback, useEffect, useState } from 'react'

import {
  installGuide,
  pushReadiness,
  readPushEnvironment,
  type PushReadiness,
} from '../install/standalone'

const DismissedKey = '1remotecli.notifications.dismissed'

/**
 * The one piece of onboarding this app has.
 *
 * Notifications are the difference between "check your phone occasionally" and
 * "your phone will tell you", and on iOS they are unreachable without a step the
 * user has to perform by hand and would never guess: Share, then Add to Home
 * Screen. Nothing in the browser can do it for them and nothing can prompt for
 * it, so the app has to ask.
 *
 * The card is careful never to show a permission button that cannot work.
 * Prompting inside an iOS tab yields a granted permission and total silence,
 * which is worse than not offering at all - the user believes it is set up.
 */
export function NotificationsCard({ onGranted }: { onGranted?: () => void }) {
  const [readiness, setReadiness] = useState<PushReadiness | null>(null)
  const [dismissed, setDismissed] = useState(() => {
    try {
      return window.localStorage.getItem(DismissedKey) === 'true'
    } catch {
      // Private mode can throw on localStorage. Showing the card is the safer
      // side of that failure.
      return false
    }
  })
  const [busy, setBusy] = useState(false)
  const [sent, setSent] = useState(false)

  // Re-read on visibility rather than once: the iOS path leaves the app, adds it
  // to the home screen and comes back, and the card must not still be showing
  // install instructions when it does.
  useEffect(() => {
    const read = () => setReadiness(pushReadiness(readPushEnvironment(window)))
    read()

    document.addEventListener('visibilitychange', read)
    return () => document.removeEventListener('visibilitychange', read)
  }, [])

  const dismiss = useCallback(() => {
    setDismissed(true)
    try {
      window.localStorage.setItem(DismissedKey, 'true')
    } catch {
      // Not being able to remember the dismissal is survivable; failing to
      // dismiss is not.
    }
  }, [])

  const enable = useCallback(async () => {
    setBusy(true)
    try {
      // Must be inside the tap. Safari refuses a request that is not obviously
      // caused by a gesture, and an await before this line is enough to lose it.
      const permission = await Notification.requestPermission()
      setReadiness(pushReadiness({ ...readPushEnvironment(window), permission }))

      // Subscribe now rather than on the next reconnect. The user has just been
      // told this will notify them; a gap of hours before it actually can is
      // indistinguishable from it not working.
      if (permission === 'granted') onGranted?.()
    } finally {
      setBusy(false)
    }
  }, [onGranted])

  /**
   * The end-to-end check the user can actually perform: if this arrives on the
   * lock screen, the plumbing works. It is a local notification rather than a
   * pushed one, so it proves the install and the permission, not the hub - which
   * is exactly the half that is hard to get right on iOS.
   */
  const test = useCallback(async () => {
    setBusy(true)
    try {
      const registration = await navigator.serviceWorker.ready
      await registration.showNotification('1RemoteCLI', {
        body: 'Notifications are working. This is what a waiting session will look like.',
        icon: '/icon-192.png',
        badge: '/icon-192.png',
        tag: 'test',
      })
      setSent(true)
    } finally {
      setBusy(false)
    }
  }, [])

  if (!readiness || dismissed) return null
  // Nothing to say once it is set up and proven.
  if (readiness.kind === 'unsupported') return null

  const button =
    'min-h-10 rounded-lg border border-slate-600 px-4 text-sm text-slate-200 transition active:bg-slate-800'

  if (readiness.kind === 'needs-install') {
    const guide = installGuide(readiness.platform)

    return (
      <section className="rounded-xl border border-slate-700 bg-slate-900/60 px-4 py-3 text-sm text-slate-300">
        <p className="font-medium text-slate-100">{guide.title}</p>
        <p className="mt-1 text-[13px] text-slate-400">
          Installed, 1RemoteCLI can tell you when a session is waiting on an answer. In a browser
          tab it cannot - iOS delivers notifications only to apps on the Home Screen.
        </p>
        <ol className="mt-3 list-decimal space-y-1 pl-5 text-[13px] text-slate-400">
          {guide.steps.map((step) => (
            <li key={step}>{step}</li>
          ))}
        </ol>
        <div className="mt-3">
          <button type="button" onClick={dismiss} className={button}>
            Not now
          </button>
        </div>
      </section>
    )
  }

  if (readiness.kind === 'blocked') {
    return (
      <section className="rounded-xl border border-slate-700 bg-slate-900/60 px-4 py-3 text-sm text-slate-300">
        <p className="font-medium text-slate-100">Notifications are turned off</p>
        <p className="mt-1 text-[13px] text-slate-400">
          Turn them back on in Settings, under 1RemoteCLI. The browser will not ask again.
        </p>
        <div className="mt-3">
          <button type="button" onClick={dismiss} className={button}>
            Dismiss
          </button>
        </div>
      </section>
    )
  }

  if (readiness.kind === 'granted') {
    if (sent) return null

    return (
      <section className="rounded-xl border border-slate-700 bg-slate-900/60 px-4 py-3 text-sm text-slate-300">
        <p className="font-medium text-slate-100">Notifications are on</p>
        <p className="mt-1 text-[13px] text-slate-400">
          Send yourself one to be sure it reaches your lock screen.
        </p>
        <div className="mt-3 flex gap-2">
          <button type="button" onClick={() => void test()} disabled={busy} className={button}>
            Send a test
          </button>
          <button type="button" onClick={dismiss} className={button}>
            Dismiss
          </button>
        </div>
      </section>
    )
  }

  return (
    <section className="rounded-xl border border-slate-700 bg-slate-900/60 px-4 py-3 text-sm text-slate-300">
      <p className="font-medium text-slate-100">Get told when a session needs you</p>
      <p className="mt-1 text-[13px] text-slate-400">
        A notification when an agent stops and asks a question, so you do not have to keep opening
        the app to find out.
      </p>
      <div className="mt-3 flex gap-2">
        <button type="button" onClick={() => void enable()} disabled={busy} className={button}>
          Turn on notifications
        </button>
        <button type="button" onClick={dismiss} className={button}>
          Not now
        </button>
      </div>
    </section>
  )
}
