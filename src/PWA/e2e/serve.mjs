// Brings up everything the browser tests talk to, in the right order, with one
// command Playwright can start and kill.
//
// The ordering matters and is easy to get wrong: the .NET host refuses to start
// without a built copy of the app, and the app has to be built against the stand-in
// identity provider and pointed at this host rather than at the deployed hub. Doing
// that in `package.json` would mean setting environment variables in a way that works
// on every shell, and doing it in Playwright's `globalSetup` would race the web server
// it is supposed to precede. So it is a script, and there is one place to look.

import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

const pwa = dirname(dirname(fileURLToPath(import.meta.url)))
const port = process.env.E2E_PORT ?? '5199'

const env = {
  ...process.env,
  // Selects `src/auth/impl.e2e.tsx` — see `vite.config.ts`.
  VITE_E2E: '1',
  // Without this the app would look for the deployed hub, and the failure would be a
  // sign-in screen that never becomes a machine list rather than anything readable.
  VITE_HUB_URL: `http://127.0.0.1:${port}`,
}

/** Runs a command to completion, failing this script if it fails. */
function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: pwa,
      env,
      stdio: 'inherit',
      shell: process.platform === 'win32',
      ...options,
    })

    child.on('error', reject)
    child.on('exit', (code) =>
      code === 0 ? resolve() : reject(new Error(`${command} exited with ${code}`)),
    )
  })
}

await run('npx', ['vite', 'build', '--outDir', 'dist-e2e', '--emptyOutDir'])

// Replaces this process rather than sitting above it, so that when Playwright stops
// the web server the host actually goes away instead of being orphaned behind a
// wrapper that swallowed the signal.
const host = spawn(
  'dotnet',
  [
    'run',
    '--project',
    join(pwa, '..', '..', 'tests', 'E2E.Host'),
    '-c',
    'Release',
    '--',
    '--pwa',
    join(pwa, 'dist-e2e'),
    '--port',
    port,
  ],
  { cwd: pwa, env, stdio: 'inherit', shell: process.platform === 'win32' },
)

const stop = () => {
  host.kill()
  process.exit(0)
}

process.on('SIGINT', stop)
process.on('SIGTERM', stop)

host.on('exit', (code) => process.exit(code ?? 0))
