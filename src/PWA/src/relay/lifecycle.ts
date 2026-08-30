import { detectPlatform } from '../install/standalone'

export interface RelayLifecycleClient {
  readonly connected: boolean
  start(): Promise<void>
  stop(): Promise<void>
  restart(): Promise<void>
}

export interface RelayLifecycleEnvironment {
  document: Document
  window: Window
  userAgent: string
  maxTouchPoints: number
}

function browserEnvironment(): RelayLifecycleEnvironment {
  return {
    document,
    window,
    userAgent: window.navigator.userAgent,
    maxTouchPoints: window.navigator.maxTouchPoints,
  }
}

/**
 * Turns iOS suspension into an ordinary disconnect that the terminal resume path
 * can recover from. iOS can freeze JavaScript while SignalR still considers its
 * socket connected; leaving that half-open socket alive makes the hub discard its
 * two-second delivery backlog and request a repaint instead of replaying the
 * daemon's retained frames.
 */
export function watchRelayLifecycle(
  client: RelayLifecycleClient,
  onSuspending: () => void,
  environment: RelayLifecycleEnvironment = browserEnvironment(),
): () => void {
  const { document: page, window: browser } = environment
  const isIos =
    detectPlatform(environment.userAgent, environment.maxTouchPoints) === 'ios'

  let active = true
  let suspended = false
  let resumePending = false
  let transition = Promise.resolve()

  const enqueue = (operation: () => Promise<void>): Promise<void> => {
    transition = transition.then(operation)
    return transition
  }

  const suspend = () => {
    if (suspended) return

    suspended = true
    onSuspending()
    void enqueue(() => client.stop())
  }

  const resume = (force: boolean) => {
    if (resumePending || (!suspended && !force)) return

    suspended = false
    resumePending = true
    onSuspending()

    void enqueue(async () => {
      if (active && !suspended && page.visibilityState === 'visible') {
        // Restart even when SignalR claims it is connected. That claim can describe
        // the half-open socket iOS froze rather than a usable transport.
        await client.restart()
      }
    }).finally(() => {
      resumePending = false
    })
  }

  const visibilityChanged = () => {
    if (!isIos) {
      if (page.visibilityState === 'visible' && !client.connected) void client.start()
      return
    }

    if (page.visibilityState === 'hidden') {
      suspend()
    } else {
      resume(false)
    }
  }

  const pageShown = () => {
    if (isIos) resume(true)
    else if (!client.connected) void client.start()
  }

  const pageHidden = () => {
    if (isIos) suspend()
  }

  const online = () => {
    if (page.visibilityState === 'visible' && !client.connected) void client.start()
  }

  page.addEventListener('visibilitychange', visibilityChanged)
  browser.addEventListener('pagehide', pageHidden)
  browser.addEventListener('pageshow', pageShown)
  browser.addEventListener('online', online)

  if (isIos && page.visibilityState === 'hidden') suspend()

  return () => {
    active = false
    page.removeEventListener('visibilitychange', visibilityChanged)
    browser.removeEventListener('pagehide', pageHidden)
    browser.removeEventListener('pageshow', pageShown)
    browser.removeEventListener('online', online)
  }
}
