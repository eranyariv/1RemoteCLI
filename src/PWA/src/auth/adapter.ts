import type { ReactNode } from 'react'

/**
 * Everything the app needs to know about who is signed in.
 *
 * A façade over the identity provider, for two reasons. The smaller one is that
 * `App.tsx` should not know that identity comes from MSAL; the larger one is that
 * the end-to-end tests have to drive a signed-in app, and the alternatives are
 * worse. Automating a real Entra sign-in means a service account with a password
 * in CI and a flow Microsoft can change without warning; mocking the network under
 * MSAL means asserting against a fiction. This lets the whole app run unmodified
 * against a substitute that hands out a fixed token.
 *
 * The substitute is selected by an alias in `vite.config.ts` at build time and is
 * absent from a production bundle. That is checked by a test rather than trusted,
 * because "it should be tree-shaken" is not a security property anyone can see.
 */
export interface AuthAdapter {
  /**
   * Runs before the first render. MSAL needs it, and doing it inside an effect
   * would show the signed-out screen for a frame on every load — which on a phone
   * reads as the app having forgotten you.
   */
  initialise(): Promise<void>

  /** Wraps the tree in whatever context the implementation needs. */
  Provider(props: { children: ReactNode }): ReactNode

  /** Reactive: the app re-renders when a sign-in completes. */
  useSession(): AuthSession

  signIn(): Promise<void>

  signOut(): Promise<void>

  /**
   * A token for the hub, or null when nobody is signed in.
   *
   * Null rather than a throw, because this runs on every connect and reconnect
   * and the caller's answer to "nobody is signed in" is to show a sign-in prompt,
   * not to treat it as a fault.
   */
  getAccessToken(): Promise<string | null>
}

export interface AuthSession {
  signedIn: boolean

  /** A sign-in is under way; the button should say so rather than look dead. */
  busy: boolean

  username?: string
}
