import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
  type Configuration,
} from '@azure/msal-browser'

/**
 * The Entra application this app signs in as. Mirrors `src/Daemon/Auth/AuthConfig.cs`
 * because the agent and the PWA share one registration on purpose: both sides
 * present a token from the same application, which turns "the phone and the machine
 * belong to the same person" into something the hub can verify rather than infer.
 *
 * None of this is secret. There is no client secret anywhere in the project — the
 * agent is a public client on a loopback redirect and this is an SPA on auth code
 * with PKCE. That is a design constraint, not an oversight: nothing here ever needs
 * to be kept out of git.
 */
export const CLIENT_ID = '3db435ae-5e69-483c-a044-d6e8b6262fc6'

/**
 * `common`, not a tenant id. This has to work for a personal Microsoft account and
 * for any work account, and a user should not have to know which kind theirs is.
 */
export const AUTHORITY = 'https://login.microsoftonline.com/common'

/** Our own API, not Graph. The hub checks for exactly this. */
export const API_SCOPE = `api://${CLIENT_ID}/Session.Access`

export const SCOPES = [API_SCOPE]

const configuration: Configuration = {
  auth: {
    clientId: CLIENT_ID,
    authority: AUTHORITY,
    knownAuthorities: ['login.microsoftonline.com'],
    // Whatever origin the app is served from, which keeps one build working across
    // the dev server, the preview server and production without a per-environment
    // constant to forget to change.
    redirectUri: `${window.location.origin}/`,
    postLogoutRedirectUri: `${window.location.origin}/`,
  },
  cache: {
    // Session storage, not local: a token that survives closing the tab is a token
    // sitting on a phone that may be handed to someone else. Pure in-memory would
    // be stricter still, but it forces a full interactive sign-in on every reload
    // and every time iOS reclaims a backgrounded tab — which is most of the time on
    // the device this product is for.
    cacheLocation: 'sessionStorage',
  },
}

export const msal = new PublicClientApplication(configuration)

let initialised: Promise<void> | null = null

/** MSAL v5 requires an explicit initialize before anything else touches it. */
export function initialiseMsal(): Promise<void> {
  initialised ??= msal.initialize().then(async () => {
    // Completes a redirect we are returning from. Doing this before React renders
    // avoids a frame of "signed out" on every sign-in.
    const result = await msal.handleRedirectPromise()

    if (result?.account) {
      msal.setActiveAccount(result.account)
    } else if (!msal.getActiveAccount()) {
      const [first] = msal.getAllAccounts()
      if (first) msal.setActiveAccount(first)
    }
  })

  return initialised
}

/**
 * Starts a sign-in.
 *
 * Redirect rather than popup: iOS Safari blocks popups opened outside a direct
 * user gesture and renders the ones it allows as a tab switch anyway, so the popup
 * flow trades a worse experience for no benefit on the device that matters here.
 */
export function signIn(): Promise<void> {
  return msal.loginRedirect({ scopes: SCOPES, prompt: 'select_account' })
}

export function signOut(): Promise<void> {
  return msal.logoutRedirect({ account: msal.getActiveAccount() ?? undefined })
}

/**
 * An access token for the hub, or null when nobody is signed in.
 *
 * Silent first, because this runs on every SignalR connect and reconnect and a
 * redirect in the middle of a reconnect would throw away whatever the user was
 * looking at. A silent failure that genuinely needs a human is surfaced by
 * returning null, so the caller can show a sign-in prompt instead of a spinner.
 */
export async function getAccessToken(): Promise<string | null> {
  await initialiseMsal()

  const account: AccountInfo | null = msal.getActiveAccount()
  if (!account) return null

  try {
    const result = await msal.acquireTokenSilent({ account, scopes: SCOPES })
    return result.accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      return null
    }

    // Anything else — a network blip, a throttled token endpoint — is worth one
    // retry from the caller rather than a sign-out.
    throw error
  }
}

/** Prompts for the interaction a silent renewal said it needed. */
export function reauthenticate(): Promise<void> {
  return msal.acquireTokenRedirect({
    scopes: SCOPES,
    account: msal.getActiveAccount() ?? undefined,
  })
}
