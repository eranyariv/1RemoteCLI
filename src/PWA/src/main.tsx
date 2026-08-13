import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { MsalProvider } from '@azure/msal-react'

import App from './App.tsx'
import { initialiseMsal, msal } from './auth/msal.ts'
import './index.css'

/*
 * MSAL is initialised before the first render rather than inside an effect.
 *
 * Rendering first would show the signed-out screen for a frame on every load and
 * on every return from the identity provider, which on a phone reads as the app
 * having forgotten you.
 */
await initialiseMsal()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MsalProvider instance={msal}>
      <App />
    </MsalProvider>
  </StrictMode>,
)
