import type { ReactNode } from 'react'
import { MsalProvider, useIsAuthenticated, useMsal } from '@azure/msal-react'

import type { AuthAdapter, AuthSession } from './adapter'
import { getAccessToken, initialiseMsal, msal, signIn, signOut } from './msal'

/** The real thing. See `msal.ts` for why each of its choices is what it is. */
export const auth: AuthAdapter = {
  initialise: initialiseMsal,

  Provider: ({ children }: { children: ReactNode }) => (
    <MsalProvider instance={msal}>{children}</MsalProvider>
  ),

  useSession: (): AuthSession => {
    const signedIn = useIsAuthenticated()
    const { inProgress, accounts } = useMsal()

    return { signedIn, busy: inProgress !== 'none', username: accounts[0]?.username }
  },

  signIn,
  signOut,
  getAccessToken,
}
