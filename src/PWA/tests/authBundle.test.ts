import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * The end-to-end suite runs the app against a stand-in identity provider that
 * hands out an unsigned token to anyone who asks (`auth/impl.e2e.tsx`). That is
 * fine in a test host and catastrophic in a deployed one, so the thing worth
 * proving is not that the substitute works but that it cannot reach production.
 *
 * "The bundler will drop it" is an assumption, and an assumption about a build
 * tool's dead-code analysis is exactly the kind of thing that stops being true
 * during a dependency upgrade nobody reads the changelog for. So this builds the
 * app the way CI builds it and reads the output.
 *
 * Slow — it is a real production build — and deliberately so. It is one test, it
 * runs on every push, and the failure it prevents is shipping an authentication
 * bypass.
 */
describe('the production bundle', () => {
  it('contains no trace of the test identity provider', () => {
    const out = mkdtempSync(join(tmpdir(), '1remote-bundle-'))

    try {
      execFileSync('npx', ['vite', 'build', '--outDir', out, '--emptyOutDir'], {
        cwd: process.cwd(),
        stdio: 'pipe',
        shell: process.platform === 'win32',
        // Whatever the developer has in their shell must not decide this.
        env: { ...process.env, VITE_E2E: '' },
      })

      const scripts = readdirSync(join(out, 'assets')).filter((name) => name.endsWith('.js'))
      expect(scripts.length).toBeGreaterThan(0)

      const bundle = scripts.map((name) => readFileSync(join(out, 'assets', name), 'utf8')).join('\n')

      // The storage key is the substitute's most distinctive string and the one
      // hardest to introduce by accident.
      expect(bundle).not.toContain('1remote-e2e-user')
      expect(bundle).not.toContain('e2e-user')
    } finally {
      rmSync(out, { recursive: true, force: true })
    }
  }, 180_000)
})
