import { useCallback, useEffect, useRef } from 'react'

import type { RelayClient } from '../relay/client'
import { fetchVapidKey, pushSupported, subscribe } from './register'
import type { PushPreferences } from './subscription'

/**
 * Keeps the hub's idea of where to reach this phone up to date.
 *
 * Runs whenever the socket comes up, not once at start-up. Subscriptions live
 * in the hub's memory, so a hub restart forgets every one of them; re-offering
 * on each connection is what makes that recover by itself instead of leaving
 * the user with notifications that quietly stopped and no way to tell.
 *
 * Silent throughout. There is nothing useful to say to someone who has not
 * asked for notifications, and nothing they could do about a push service being
 * unreachable — the subscription is offered again on the next connection anyway.
 *
 * Returns the same registration as a callback so the onboarding card can fire it
 * the moment permission is granted. Waiting for the next reconnect would mean the
 * user turns notifications on and then, plausibly for hours, gets none.
 */
export function usePushRegistration(
  client: RelayClient,
  connected: boolean,
  preferences: PushPreferences,
): () => void {
  const { awaitingInput, sessionFinished, announcements } = preferences
  const preferencesRef = useRef(preferences)
  preferencesRef.current = preferences
  const registrationQueue = useRef(Promise.resolve())
  // Survives re-renders so an unmount mid-flight cannot leave a half-finished
  // registration writing through a client the app has moved on from.
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  const register = useCallback(() => {
    registrationQueue.current = registrationQueue.current.then(async () => {
      if (!pushSupported()) return
      if (Notification.permission !== 'granted') return

      const key = await fetchVapidKey()
      if (!key || !alive.current) return

      const registration = await navigator.serviceWorker.ready
      if (!alive.current) return

      const subscription = await subscribe(registration, key)
      if (!subscription || !alive.current) return

      await client.registerPush(subscription, preferencesRef.current)
    })

    return registrationQueue.current
  }, [client])

  useEffect(() => {
    if (!connected) return
    void register()
  }, [announcements, awaitingInput, connected, register, sessionFinished])

  return useCallback(() => void register(), [register])
}
