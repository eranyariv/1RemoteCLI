import { attach, expect, expectScreen, test, type } from './fixtures'

/**
 * The two ways a session stops answering, which are different in one way that matters:
 * one of them comes back.
 *
 * A phone loses its connection constantly — a lift, a tunnel, the screen locking. The
 * app has to tell those apart from a session that has genuinely ended, because the user
 * acts on the difference: one is worth waiting out and the other is worth walking away
 * from. Getting it wrong in either direction is bad. Reporting a dead session as
 * "reconnecting" leaves someone staring at a spinner forever; reporting a dropped
 * connection as "ended" makes them close a session that is still running.
 */
test.describe('losing the connection', () => {
  test('says so, and recovers when the network comes back', async ({ app, desk, network }) => {
    await desk('offline')
    await attach(app, 'offline')
    await expectScreen(app, 'E2E-READY')

    network.drop()

    // The banner is the whole point: what is on screen is stale, and the user needs to
    // know that before they act on it.
    await expect(app.getByText('Reconnecting', { exact: true })).toBeVisible({ timeout: 30_000 })

    network.restore()

    await expect(app.getByText('Reconnecting', { exact: true })).toBeHidden({ timeout: 60_000 })

    // Recovery is not the banner going away. It is the session being usable again, so
    // the assertion is that a keystroke reaches the program and it answers — the same
    // thing the user will try the moment they see the banner clear.
    await type(app, 'y')
    await expectScreen(app, 'E2E-PROCEEDING')
  })

  test('does not lose what was on the screen', async ({ app, desk, network }) => {
    await desk('recover')
    await attach(app, 'recover')
    await expectScreen(app, 'E2E-READY')

    network.drop()
    await expect(app.getByText('Reconnecting', { exact: true })).toBeVisible({ timeout: 30_000 })
    network.restore()
    await expect(app.getByText('Reconnecting', { exact: true })).toBeHidden({ timeout: 60_000 })

    // The banner printed at start-up is far enough up the scrollback that a re-attach
    // which quietly started from a blank screen would drop it. A terminal that comes
    // back empty is a terminal you cannot trust.
    //
    // Polled, not read once. The banner clearing means the socket is back; the screen
    // is redrawn from the snapshot that follows, so there is a window in which the
    // connection is up and the terminal is still empty. Asserting inside that window
    // is a race that fails on a loaded machine and nowhere else.
    await expectScreen(app, '1RemoteCLI end-to-end script')
  })
})

/**
 * The session ending underneath the phone.
 *
 * Nothing about this is an error — a session ends because the program at the desk
 * finished, which is the normal outcome. What the app must not do is leave the last
 * screen up as though it were live, because the user will type into it.
 */
test.describe('a session that ends', () => {
  test('says so while the phone is watching', async ({ app, desk }) => {
    await desk('exit')
    await attach(app, 'exit')
    await expectScreen(app, 'E2E-READY')

    // `q` makes the script say goodbye and exit of its own accord, which is the
    // ordinary way a session ends: the program finished, nobody killed it.
    await type(app, 'q')

    await expect(app.getByText('This session has ended')).toBeVisible({ timeout: 30_000 })

    // The last screen stays. It is usually the reason the session was being watched at
    // all — the error message, the test summary, the thing you went to look at.
    //
    // Polled for the same reason as the re-attach above: the banner is driven by the
    // close notification, and the frame carrying the program's final output is a
    // separate message. Reading the screen once, the instant the banner appears, is
    // asserting that two independent messages arrived in a particular order.
    await expectScreen(app, 'E2E-BYE')
  })

  test('drops it from the machine list afterwards', async ({ app, desk }) => {
    await desk('vanish')
    await attach(app, 'vanish')
    await expectScreen(app, 'E2E-READY')

    await type(app, 'q')
    await expect(app.getByText('This session has ended')).toBeVisible({ timeout: 30_000 })

    await app.getByRole('button', { name: '‹ Back' }).click()

    // A session that has ended but is still offered is worse than one that disappears:
    // tapping it is a dead end with no explanation.
    await expect(app.getByRole('button', { name: /vanish/ })).toHaveCount(0, { timeout: 30_000 })
  })
})
