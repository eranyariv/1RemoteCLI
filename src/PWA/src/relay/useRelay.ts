import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

import { RelayClient, type RelayStatus } from './client'
import type { HubError } from '../protocol/wire'
import {
  machineOffline,
  machineOnline,
  replaceAll,
  sessionAwaitingInput,
  sessionClosed,
  sessionOpened,
  type Machines,
} from './machines'
import {
  remove as removeProject,
  replaceAll as replaceAllProjects,
  upsert as upsertProject,
  type Projects,
} from './projects'

export interface Relay {
  status: RelayStatus
  /** Set when the status needs explaining — a refusal, or why we are offline. */
  detail: string | null
  machines: Machines
  projects: Projects
  /**
   * Whether the hub has ever told us what machines exist.
   *
   * Distinct from "there are no machines". A deep link tapped from a locked
   * phone arrives before the socket is up, and without this the app would
   * announce that the session no longer exists during the second it takes to
   * find out — on precisely the path the notification feature exists to serve.
   */
  loaded: boolean
  lastError: HubError | null
  client: RelayClient
  refresh(): Promise<void>
  dismissError(): void
}

/**
 * Owns one relay connection for the lifetime of the app and keeps the machine
 * list in sync with it.
 *
 * The client is created once with `useRef` rather than on each render, and React's
 * StrictMode double-effect is handled by starting idempotently: `RelayClient.start`
 * returns the in-flight attempt rather than opening a second socket.
 */
export function useRelay(signedIn: boolean): Relay {
  const clientRef = useRef<RelayClient | null>(null)
  clientRef.current ??= new RelayClient()
  const client = clientRef.current

  const [status, setStatus] = useState<RelayStatus>('connecting')
  const [detail, setDetail] = useState<string | null>(null)
  const [machines, setMachines] = useState<Machines>([])
  const [projects, setProjects] = useState<Projects>([])
  const [loaded, setLoaded] = useState(false)
  const [lastError, setLastError] = useState<HubError | null>(null)

  useEffect(() => {
    const off = [
      client.on('status', (next, why) => {
        setStatus(next)
        setDetail(why ?? null)
      }),

      client.on('machines', (list) => {
        setMachines(replaceAll(list))
        setLoaded(true)
      }),
      client.on('machineOnline', (machine) => setMachines((m) => machineOnline(m, machine))),
      client.on('machineOffline', (machineId) => setMachines((m) => machineOffline(m, machineId))),

      client.on('sessionOpened', (machineId, session) =>
        setMachines((m) => sessionOpened(m, machineId, session)),
      ),

      // The same upsert as an open. A session that is not on the list cannot be
      // updated onto it, because `sessionOpened` only touches a machine it knows.
      client.on('sessionUpdated', (machineId, session) =>
        setMachines((m) => sessionOpened(m, machineId, session)),
      ),

      client.on('sessionClosed', (machineId, sessionId) =>
        setMachines((m) => sessionClosed(m, machineId, sessionId)),
      ),

      client.on('awaitingInput', (machineId, sessionId) =>
        setMachines((m) => sessionAwaitingInput(m, machineId, sessionId, true)),
      ),

      // Output clears the flag: a session that just wrote something is, by
      // definition, not sitting waiting for you.
      client.on('terminalOutput', (output) =>
        setMachines((m) =>
          m.map((machine) => ({
            ...machine,
            sessions: machine.sessions.map((session) =>
              session.sessionId === output.sessionId && session.awaitingInput
                ? { ...session, awaitingInput: false }
                : session,
            ),
          })),
        ),
      ),

      client.on('error', setLastError),

      client.on('projects', (list) => setProjects(replaceAllProjects(list))),
      client.on('projectCreated', (project) => setProjects((p) => upsertProject(p, project))),
      client.on('projectUpdated', (project) => setProjects((p) => upsertProject(p, project))),
      client.on('projectDeleted', (projectId) => setProjects((p) => removeProject(p, projectId))),
    ]

    return () => {
      for (const unsubscribe of off) unsubscribe()
    }
  }, [client])

  useEffect(() => {
    if (!signedIn) {
      setStatus('signed-out')
      setMachines([])
      setProjects([])
      setLoaded(false)
      void client.stop()
      return
    }

    void client.start()

    return () => {
      void client.stop()
    }
  }, [client, signedIn])

  // A phone suspends the tab when it locks. SignalR notices eventually, but the
  // user is looking at the screen now, so nudge it the moment they come back.
  useEffect(() => {
    if (!signedIn) return

    const wake = () => {
      if (document.visibilityState === 'visible' && !client.connected) {
        void client.start()
      }
    }

    document.addEventListener('visibilitychange', wake)
    window.addEventListener('online', wake)

    return () => {
      document.removeEventListener('visibilitychange', wake)
      window.removeEventListener('online', wake)
    }
  }, [client, signedIn])

  const refresh = useCallback(async () => {
    if (client.connected) {
      await client.refreshMachines()
      await client.refreshProjects()
    } else {
      await client.start()
    }
  }, [client])

  const dismissError = useCallback(() => setLastError(null), [])

  return useMemo(
    () => ({ status, detail, machines, projects, loaded, lastError, client, refresh, dismissError }),
    [status, detail, machines, projects, loaded, lastError, client, refresh, dismissError],
  )
}
