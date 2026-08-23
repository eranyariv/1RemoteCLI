import { expect, sessionCard, test } from './fixtures'

async function expectNoHorizontalPageScroll(page: import('@playwright/test').Page) {
  const metrics = await page.evaluate(() => {
    window.scrollTo(100, window.scrollY)
    const root = document.scrollingElement
    const surface = document.querySelector<HTMLElement>('.vertical-list-surface')
    const bounds = surface?.getBoundingClientRect()

    return {
      scrollLeft: root?.scrollLeft ?? 0,
      scrollWidth: root?.scrollWidth ?? 0,
      clientWidth: root?.clientWidth ?? 0,
      listLeft: bounds?.left ?? -1,
      listRight: bounds?.right ?? Number.POSITIVE_INFINITY,
      listScrollWidth: surface?.scrollWidth ?? Number.POSITIVE_INFINITY,
      listClientWidth: surface?.clientWidth ?? 0,
      listTouchAction: surface ? getComputedStyle(surface).touchAction : '',
      viewportWidth: window.innerWidth,
    }
  })

  expect(metrics.scrollLeft).toBe(0)
  expect(metrics.scrollWidth).toBeLessThanOrEqual(metrics.clientWidth)
  expect(metrics.listLeft).toBeGreaterThanOrEqual(0)
  expect(metrics.listRight).toBeLessThanOrEqual(metrics.viewportWidth)
  expect(metrics.listScrollWidth).toBeLessThanOrEqual(metrics.listClientWidth)
  expect(metrics.listTouchAction).toBe('pan-y')
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
