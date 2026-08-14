import { attach, expect, expectScreen, ptySize, screen, test, type } from './fixtures'
import type { APIRequestContext, Page } from '@playwright/test'

/** The width the app says it is showing, read out of the session header. */
async function geometry(page: Page): Promise<{ cols: number; rows: number }> {
  const text = await page.getByText(/desk · \d+×\d+/).innerText()
  const [, cols, rows] = /(\d+)×(\d+)/.exec(text) ?? []

  return { cols: Number(cols), rows: Number(rows) }
}

/**
 * Waits until the desk has caught up with the phone, and returns the shape they agree on.
 *
 * The app measures itself and reports the result after a 120 ms debounce, so there is
 * always a window in which the header and the pseudoconsole disagree. Every assertion
 * below is about what the *program* believes, and asking it during that window is a
 * race that fails perhaps one run in three — which is worse than a test that never
 * passes, because it teaches you to re-run it.
 */
async function settled(
  page: Page,
  request: APIRequestContext,
  sessionId: string,
): Promise<{ cols: number; rows: number }> {
  await expect
    .poll(async () => JSON.stringify(await ptySize(request, sessionId)), {
      message: 'the pseudoconsole to adopt the phone\u2019s geometry',
    })
    .toBe(JSON.stringify(await geometry(page)))

  return await geometry(page)
}

/**
 * Resizing, which on a phone is not a thing anybody does deliberately — it is what
 * happens when the device is rotated or the on-screen keyboard appears.
 *
 * The policy is that the phone wins: whatever is attached decides the geometry, and the
 * pseudoconsole is reshaped to match. That is only worth anything if it reaches the
 * program, so these tests ask the program.
 */
test.describe('resizing', () => {
  test('reshapes the pseudoconsole to the phone\u2019s screen', async ({ app, desk, request }) => {
    const session = await desk('resize')
    await attach(app, 'resize')
    await expectScreen(app, 'E2E-READY')

    // The session was started at the desk's 80×24 and the phone is nothing like that
    // shape, so this also checks the adoption happened at all rather than checking that
    // two numbers which were equal all along are still equal.
    const phone = await settled(app, request, session.sessionId)
    expect(phone.cols).not.toBe(80)

    // `w` makes the program print the width the operating system is telling it about,
    // which is the only number here that the browser did not choose.
    await type(app, 'w')
    await expectScreen(app, `E2E-WIDTH ${phone.cols}`)
  })

  test('follows the screen when the device is rotated', async ({ app, desk, request }) => {
    const session = await desk('rotate')
    await attach(app, 'rotate')
    await expectScreen(app, 'E2E-READY')

    const portrait = await settled(app, request, session.sessionId)

    await app.setViewportSize({ width: 915, height: 412 })

    // The assertion is on the number changing rather than on a particular value: the
    // exact column count is a consequence of the font metrics the browser chose, and
    // pinning it would make this a test of Chromium's text rendering.
    await expect
      .poll(async () => (await geometry(app)).cols, { message: 'the terminal to widen' })
      .toBeGreaterThan(portrait.cols)

    const landscape = await settled(app, request, session.sessionId)

    await type(app, 'w')

    // The load-bearing assertion: the program at the desk agrees. A resize that
    // reflowed the browser's copy of the screen without reaching the pseudoconsole
    // would look identical up to this line.
    await expectScreen(app, `E2E-WIDTH ${landscape.cols}`)
  })

  test('keeps what was on the screen through a resize', async ({ app, desk }) => {
    await desk('reflow')
    await attach(app, 'reflow')
    await expectScreen(app, 'E2E-READY')

    await app.setViewportSize({ width: 915, height: 412 })

    await expect
      .poll(async () => await screen(app), { message: 'the banner to survive the reflow' })
      .toContain('1RemoteCLI end-to-end script')

    // Still usable afterwards, which is the part a reflow bug tends to break: the
    // screen looks right and the cursor is somewhere else entirely.
    await type(app, 'y')
    await expectScreen(app, 'E2E-PROCEEDING')
  })
})
