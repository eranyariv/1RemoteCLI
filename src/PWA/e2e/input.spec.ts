import { attach, expect, expectScreen, screen, test, type } from './fixtures'

/**
 * The half of the product that is not "read what is happening": answering it.
 *
 * Every test here asserts on something the scripted CLI printed *in response*, not on
 * something the browser echoed locally. A terminal that shows your keystrokes without
 * sending them looks identical from the outside and is completely useless, so an
 * assertion on the echo alone would pass on a broken build.
 */
test.describe('answering a session', () => {
  test('sends a keystroke and the program acts on it', async ({ app, desk }) => {
    await desk('prompt')
    await attach(app, 'prompt')
    await expectScreen(app, 'Continue? (y/n)')

    await type(app, 'y')

    // Printed by the program at the desk, only ever after it read the character.
    await expectScreen(app, 'E2E-PROCEEDING')
  })

  test('sends the other answer too', async ({ app, desk }) => {
    await desk('prompt')
    await attach(app, 'prompt')
    await expectScreen(app, 'Continue? (y/n)')

    await type(app, 'n')

    await expectScreen(app, 'E2E-ABORTED')

    // The two answers have to be distinguishable, or the test above would pass on a
    // build that sent a fixed byte regardless of which key was pressed.
    expect(await screen(app)).not.toContain('E2E-PROCEEDING')
  })

  test('sends a key from the accessory bar', async ({ app, desk }) => {
    await desk('keybar')
    await attach(app, 'keybar')
    await expectScreen(app, 'Continue? (y/n)')

    // The bar exists because a phone keyboard has no Escape, no Tab and no arrows.
    // Whether those keys reach the desk is a different question from whether the
    // on-screen keyboard works, and needs its own answer.
    await app.getByRole('button', { name: 'Return' }).click()
    await type(app, 'y')

    await expectScreen(app, 'E2E-PROCEEDING')
  })

  /**
   * The most time-critical thing anybody does with this product: stopping something.
   *
   * The interrupt does not travel as a byte — it is a hub method of its own, because a
   * session wedged badly enough to need interrupting may have stopped reading its
   * input. This is the test that the whole of that path works, from a thumb on a phone
   * to a console control event at the desk.
   */
  test('interrupts the program at the desk', async ({ app, desk }) => {
    await desk('runaway')
    await attach(app, 'runaway')
    await expectScreen(app, 'E2E-READY')

    await app.getByRole('button', { name: 'Ctrl+C — interrupt' }).click()

    await expectScreen(app, 'E2E-INTERRUPTED')
  })

  test('leaves the program running after an interrupt', async ({ app, desk }) => {
    await desk('runaway')
    await attach(app, 'runaway')
    await expectScreen(app, 'E2E-READY')

    await app.getByRole('button', { name: 'Ctrl+C — interrupt' }).click()
    await expectScreen(app, 'E2E-INTERRUPTED')

    // Interrupting is not killing. A session that died on Ctrl+C would satisfy the
    // test above and be the wrong behaviour entirely — the point is to stop what the
    // program is doing and get the prompt back.
    await type(app, 'y')
    await expectScreen(app, 'E2E-PROCEEDING')
  })
})
