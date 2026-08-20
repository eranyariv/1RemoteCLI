import { expect, sessionCard, test } from './fixtures'

async function expectNoHorizontalPageScroll(page: import('@playwright/test').Page) {
  const metrics = await page.evaluate(() => {
    window.scrollTo(100, window.scrollY)
    const root = document.scrollingElement

    return {
      scrollLeft: root?.scrollLeft ?? 0,
      scrollWidth: root?.scrollWidth ?? 0,
      clientWidth: root?.clientWidth ?? 0,
    }
  })

  expect(metrics.scrollLeft).toBe(0)
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
}

test('keeps project and session lists within the phone viewport', async ({ app, desk }) => {
  const longName = 'session-with-a-name-that-must-not-widen-the-phone-viewport'
  await desk(longName)
  await expect(sessionCard(app, longName)).toBeVisible()

  await expectNoHorizontalPageScroll(app)

  await app.getByRole('button', { name: 'Back to projects' }).click()
  await expect(app.getByRole('heading', { name: 'Projects' })).toBeVisible()
  await expectNoHorizontalPageScroll(app)
})
