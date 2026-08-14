import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import App from './App.tsx'
import { auth } from './auth/impl.tsx'
import { registerServiceWorker } from './install/serviceWorker.ts'
import './index.css'

/*
 * Identity is initialised before the first render rather than inside an effect.
 *
 * Rendering first would show the signed-out screen for a frame on every load and
 * on every return from the identity provider, which on a phone reads as the app
 * having forgotten you.
 */
await auth.initialise()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <auth.Provider>
      <App />
    </auth.Provider>
  </StrictMode>,
)

/*
 * Registered after the first render, not before it.
 *
 * The service worker earns nothing on the first load - it has nothing cached yet
 * - and registering it costs the browser work at exactly the moment the user is
 * waiting for the app to appear. It matters on the second load and afterwards,
 * where it is what makes the app installable, and installable is what makes
 * notifications possible on iOS.
 */
registerServiceWorker((activate) => {
  // Not a prompt. Sitting on a stale build is the worse failure for a client
  // that talks a versioned protocol to a hub that moves, and there is no unsaved
  // work in this app to lose - the session lives on the machine, not the phone.
  activate()
})
