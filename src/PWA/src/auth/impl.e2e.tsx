import { useSyncExternalStore, type ReactNode } from 'react'

import type { AuthAdapter, AuthSession } from './adapter'

/**
 * The stand-in used by the end-to-end tests, and only by them.
 *
 * `vite.config.ts` aliases `./auth/impl` to this file when `VITE_E2E=1`, so a
 * production build never contains it. `authBundle.test.ts` asserts that rather
 * than assuming it.
 *
 * The token it hands out is not a JWT and is not signed. It does not have to be:
 * the host these tests run against mounts the real `RelayHub` behind a test
 * authentication scheme that reads the identity from a header, exactly as the
 * hub's own test suite does. Signature checking has its own tests, and putting a
 * signing key in a browser bundle to satisfy a test would be a worse idea than
 * anything it could catch.
 *
 * The identity is taken from the URL, so one build can be two different people:
 * `?e2e-user=alice` in one browser context and `?e2e-user=bob` in another is how
 * the isolation scenarios are driven.
 */
const KEY = '1remote-e2e-user'

function readUser(): string | null {
  const fromUrl = new URLSearchParams(window.location.search).get('e2e-user')

  if (fromUrl) {
    // Persisted, because the app strips its own query string on start-up and a
    // reload is one of the scenarios under test.
    window.sessionStorage.setItem(KEY, fromUrl)
    return fromUrl
  }

  return window.sessionStorage.getItem(KEY)
}

const listeners = new Set<() => void>()

function announce() {
  for (const listener of listeners) listener()
}

export const auth: AuthAdapter = {
  initialise: () => Promise.resolve(),

  Provider: ({ children }: { children: ReactNode }) => children,

  useSession: (): AuthSession => {
    const user = useSyncExternalStore(
      (listener) => {
        listeners.add(listener)
        return () => listeners.delete(listener)
      },
      readUser,
      () => null,
    )

    return { signedIn: user !== null, busy: false, username: user ?? undefined }
  },

  signIn: () => {
    // No redirect, no identity provider: the tests click this button and expect
    // to be signed in on the next frame. Defaulting to a name rather than
    // demanding one keeps the simple scenarios simple.
    window.sessionStorage.setItem(KEY, window.sessionStorage.getItem(KEY) ?? 'alice')
    announce()
    return Promise.resolve()
  },

  signOut: () => {
    window.sessionStorage.removeItem(KEY)
    announce()
    return Promise.resolve()
  },

  getAccessToken: () => Promise.resolve(readUser()),
}
