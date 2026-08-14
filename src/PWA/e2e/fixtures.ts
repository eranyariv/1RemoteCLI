import { expect, test as base, type Page, type WebSocketRoute } from '@playwright/test'

/**
 * The pieces every scenario needs: a signed-in app, a session running at the desk, and
 * a way to read what the terminal is showing.
 */

const HOST = 'http://127.0.0.1:5199'

export interface DeskSession {
  sessionId: string
  machineId: string
}

export interface Network {
  /** Drops the live connection, the way a lift or a tunnel does. */
  drop(): void

  /** Lets the app connect again. It has to notice on its own. */
  restore(): void
}

interface Fixtures {
  /** Starts the scripted CLI at the desk and tears it down afterwards. */
  desk: (name?: string) => Promise<DeskSession>

  /** Control over the phone's connection to the hub. */
  network: Network

  /** The app, signed in as Alice, with the machine list on screen. */
  app: Page
}

export const test = base.extend<Fixtures>({
  desk: async ({ request }, use) => {
    const started: string[] = []

    await use(async (name = 'e2e script') => {
      const response = await request.post(`${HOST}/e2e/sessions?name=${encodeURIComponent(name)}`)
      expect(response.ok()).toBeTruthy()

      const session = (await response.json()) as DeskSession
      started.push(session.sessionId)

      return session
    })

    // Sessions are real processes. One left behind would show up in the next test's
    // machine list and make a passing suite depend on the order it ran in.
    for (const id of started) {
      await request.delete(`${HOST}/e2e/sessions/${id}`).catch(() => undefined)
    }
  },

  // Not `context.setOffline`. That emulates network conditions for *new* requests and
  // leaves an established WebSocket connected, so the app carries on talking down a
  // socket the test believes it has cut — which is the one thing this scenario must not
  // do. Closing the socket is also closer to the truth: a phone losing signal does not
  // gently stop making requests, it has its connection dropped underneath it.
  network: async ({ context }, use) => {
    let dropped = false
    const live = new Set<WebSocketRoute>()

    await context.routeWebSocket(/.*/, (ws) => {
      if (dropped) {
        ws.close()
        return
      }

      ws.connectToServer()
      live.add(ws)
      ws.onClose(() => live.delete(ws))
    })

    await use({
      drop: () => {
        dropped = true
        for (const ws of live) ws.close()
        live.clear()
      },
      restore: () => {
        dropped = false
      },
    })
  },

  // Depends on `network` so the route is installed before the first navigation; a
  // handler added after the socket is open would never see it.
  app: async ({ page, network }, use) => {
    void network
    await signIn(page)
    await use(page)
  },
})

export { expect }

/**
 * Opens the app as one of the two test users.
 *
 * There is no identity provider in the loop — see `src/auth/impl.e2e.tsx` for why, and
 * for what is given up by leaving it out. From the app's point of view this is an
 * ordinary sign-in: the same façade, the same state transition, the same first render.
 */
export async function signIn(page: Page, user: 'alice' | 'bob' = 'alice'): Promise<void> {
  await page.goto(`/?e2e-user=${user}`)

  // The header only exists once the app believes somebody is signed in, so waiting for
  // it is waiting for the thing the test actually depends on rather than for a delay.
  await expect(page.getByRole('heading', { name: 'Machines' })).toBeVisible()
}

/**
 * Opens a session from the machine list.
 *
 * By its display name, the way a person would, rather than by index: a test that taps
 * "the first card" passes whichever session it happens to open, which is exactly the
 * bug worth catching when a machine has two.
 */
export async function attach(page: Page, displayName: string): Promise<void> {
  await page.getByRole('button', { name: new RegExp(displayName) }).first().click()
  await expect(page.getByRole('button', { name: '‹ Back' })).toBeVisible()
}

/**
 * Everything the terminal is currently showing, as one string.
 *
 * Read out of xterm's rendered rows rather than from a canvas, because a canvas is
 * pixels and the rendered rows are what the emulator decided should be there.
 * Whitespace is collapsed: a terminal pads every line to its full width, and a test
 * that cared about that would be asserting on the column count by accident.
 */
export async function screen(page: Page): Promise<string> {
  const rows = await page.locator('.xterm-rows').first().innerText()
  return rows.replace(/[ \u00a0]+/g, ' ')
}

/** Waits until the terminal shows something, and says what it was waiting for if it never does. */
export async function expectScreen(page: Page, text: string, timeout = 30_000): Promise<void> {
  await expect
    .poll(async () => await screen(page), {
      message: `waiting for the terminal to show ${JSON.stringify(text)}`,
      timeout,
    })
    .toContain(text)
}

/** Types into the terminal itself, as opposed to tapping the accessory key bar. */
export async function type(page: Page, text: string): Promise<void> {
  await page.locator('.xterm-helper-textarea').first().focus()
  await page.keyboard.type(text)
}

/**
 * The shape of the real pseudoconsole at the desk.
 *
 * Used to wait, not to assert. The app reports its geometry the moment it measures it,
 * but the far end only hears about it after a debounce and two network hops, so a test
 * that asks the program how wide it is immediately after reading the header is asking
 * before the answer can have changed. Waiting on the truth removes the race without
 * weakening the assertion, which is still made against what the program itself believes.
 */
export async function ptySize(
  request: import('@playwright/test').APIRequestContext,
  sessionId: string,
): Promise<{ cols: number; rows: number }> {
  const response = await request.get(`${HOST}/e2e/sessions/${sessionId}/size`)
  expect(response.ok()).toBeTruthy()

  return (await response.json()) as { cols: number; rows: number }
}
