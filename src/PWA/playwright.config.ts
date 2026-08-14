import { defineConfig, devices } from '@playwright/test'

/**
 * The end-to-end suite: a real browser, on a phone-sized screen, against the real hub,
 * the real agent and a real pseudoconsole.
 *
 * <p>
 * What it is standing up is described in `tests/E2E.Host` — one process holding all of
 * it, serving this app from the same origin so there is one address to talk to and no
 * CORS story. The single substitution is the signature check on the access token, and
 * that is checked properly in `tests/Hub.Tests` where it can be, rather than through a
 * browser where it cannot.
 * </p>
 */
export default defineConfig({
  testDir: './e2e',

  // Each spec brings up its own session and then asserts against it, so they are
  // independent. They share one host process, though, and a pseudoconsole is a real
  // operating-system object — running them all at once turns a slow machine into a
  // flaky one for no benefit on a suite this size.
  fullyParallel: false,
  workers: 1,

  // A retry hides a flake, and a flaky end-to-end test is worse than no test: it
  // trains everyone to rerun rather than look. If one of these fails it is either a
  // real regression or a test that needs fixing, and both want to be visible.
  retries: 0,

  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],

  timeout: 60_000,
  expect: { timeout: 20_000 },

  use: {
    baseURL: 'http://127.0.0.1:5199',
    trace: 'retain-on-failure',
    video: 'off',

    // The app installs a service worker that caches its own shell, which is the right
    // thing on a phone and the wrong thing in a suite where each test wants the build
    // that was just made. Offline behaviour and the caching rules have their own tests
    // in `src/install`, where they can be asserted directly instead of inferred from a
    // terminal that failed to appear.
    serviceWorkers: 'block',
  },

  projects: [
    {
      // A phone, because that is the only device this product is for. A desktop
      // viewport would pass while the accessory key bar was off the bottom of every
      // real screen.
      name: 'phone',
      use: { ...devices['Pixel 7'] },
    },
  ],

  webServer: {
    // Builds if it has to, which means a checkout can run the suite without knowing
    // that a .NET project is involved.
    command: 'node e2e/serve.mjs',
    url: 'http://127.0.0.1:5199/e2e/ready',
    reuseExistingServer: !process.env.CI,
    timeout: 240_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
})
