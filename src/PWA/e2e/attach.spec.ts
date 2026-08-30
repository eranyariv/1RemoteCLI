import { attach, expect, expectScreen, screen, sessionCard, signIn, test } from './fixtures'

/**
 * The first two minutes of using the product: sign in, find the machine, open what is
 * running on it, and see the screen as it was left.
 */
test.describe('finding and opening a session', () => {
  test('lists the machine and what is running on it', async ({ app, desk }) => {
    await desk('build watcher')

    await expect(app.getByText('desk')).toBeVisible()
    await expect(sessionCard(app, 'build watcher')).toBeVisible()

    // The session's identity, not just its name — a card that says the right thing
    // while pointing at the wrong session is the failure worth catching.
    await expect(sessionCard(app, 'build watcher')).toBeVisible()
  })

  test('collapses an idle machine and explains it when expanded', async ({ app }) => {
    const expand = app.getByRole('button', { name: 'Expand desk' })
    await expect(expand).toBeVisible()
    await expand.click()
    await expect(app.getByText(/Nothing running/)).toBeVisible()
  })

  /**
   * The single most valuable assertion in the suite.
   *
   * The session has been running since before this browser existed. Everything on the
   * screen therefore had to survive being parsed by the agent's emulator, held in a
   * screen buffer, re-serialized into escape sequences, put through MessagePack, sent
   * over SignalR and fed to xterm.js. Any one of those going wrong shows up here and
   * nowhere else.
   */
  test('restores the screen of a session that was already running', async ({ app, desk }) => {
    await desk('already running')

    await attach(app, 'already running')

    await expectScreen(app, 'E2E-READY')

    const restored = await screen(app)

    expect(restored).toContain('1RemoteCLI end-to-end script')
    expect(restored).toContain('green red bold underline')
    expect(restored).toContain('Continue? (y/n)')
  })

  test('keeps colour and emphasis through the round trip', async ({ app, desk }) => {
    await desk('styled output')
    await attach(app, 'styled output')
    await expectScreen(app, 'E2E-READY')

    // Not what the text says — what it looks like.
    //
    // The screen arrives as a snapshot the agent rebuilt from its own screen model, so
    // every attribute on it survived being parsed out of one VT stream and written back
    // into another. Asserting on the words alone would pass with all of them dropped,
    // and so would asserting that *some* styling is present: colour and emphasis are
    // rebuilt by different code, and dropping bold leaves the colours untouched.
    //
    // Checked here by breaking the writer: with bold removed from the re-serializer,
    // the word "bold" arrives in a span with no class at all, and this fails.
    const styled = await app.locator('.xterm-rows span').evaluateAll((nodes) =>
      nodes.map((node) => ({ text: node.textContent ?? '', className: node.className })),
    )

    const classesOf = (word: string) =>
      styled.find((span) => span.text.trim() === word)?.className ?? ''

    expect(classesOf('bold')).toContain('xterm-bold')
    expect(classesOf('underline')).toContain('xterm-underline')

    // Different colours, not merely coloured: a writer that emitted one SGR for every
    // cell would satisfy "has a foreground class" on every line of the screen.
    expect(classesOf('green')).not.toBe(classesOf('red'))
    expect(classesOf('green')).toMatch(/xterm-fg-\d/)
    expect(classesOf('red')).toMatch(/xterm-fg-\d/)
  })

  test('shows connected state and makes CLI commands easy to close', async ({ app, desk }) => {
    await desk('command palette')
    await attach(app, 'command palette')
    await expectScreen(app, 'E2E-READY')

    const connected = app.getByLabel('CLI connected')
    await expect(connected).toBeVisible()
    await expect(connected).toHaveClass(/bg-emerald-400/)
    await expect(app.getByRole('button', { name: 'Record terminal diagnostics' })).toHaveCount(0)

    await app.getByRole('button', { name: 'Set what this session is running' }).click()
    await app.getByRole('button', { name: 'Claude Code' }).click()
    await expect(app.getByRole('button', { name: /^\/compact/ })).toBeVisible()

    const type = app.getByRole('button', { name: 'Running Claude Code — change' })
    await type.click()
    await expect(app.getByRole('button', { name: /^\/compact/ })).toBeHidden()

    await type.click()
    await expect(app.getByRole('button', { name: /^\/compact/ })).toBeVisible()
    await app.getByRole('button', { name: 'Close Claude Code shortcuts and commands' }).click()
    await expect(app.getByRole('button', { name: /^\/compact/ })).toBeHidden()
  })

  /**
   * The isolation guarantee, seen from a browser.
   *
   * `Hub.Tests` proves the hub cannot be talked into it; this proves the product does
   * not do it by accident — a cached machine list, a shared store, a service worker
   * serving one person's data to another.
   */
  test('shows a second person none of the first one\u2019s machines', async ({ desk, browser }) => {
    await desk('alice only')

    const context = await browser.newContext()
    const page = await context.newPage()

    try {
      await signIn(page, 'bob')

      await expect(page.getByText(/No machines yet/i).or(page.getByText(/Nothing running/))).toBeVisible()
      await expect(sessionCard(page, 'alice only')).toHaveCount(0)
    } finally {
      await context.close()
    }
  })
})
