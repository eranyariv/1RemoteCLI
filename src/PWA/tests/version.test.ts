import { existsSync, readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

// Injected by vite.config.ts. Declared here because this file is typechecked as a
// Node project, which does not see the app's ambient declarations.
declare const __APP_VERSION__: string

/**
 * The version the app displays comes from the repository's VERSION file, injected at
 * build time by vite.config.ts. That injection is invisible — a typo in the define,
 * or a `version` field creeping back into package.json — and the cost of getting it
 * wrong is the wrong number on the one screen a user reads before reporting a problem.
 *
 * Found by walking up rather than by a relative path: the file sits outside the Vite
 * root, so it cannot be imported, and `import.meta.url` under jsdom is an http URL.
 */
function readVersionFile(): string {
  let dir = resolve(process.cwd())

  for (;;) {
    const candidate = join(dir, 'VERSION')
    if (existsSync(candidate)) return readFileSync(candidate, 'utf8').trim()

    const parent = dirname(dir)
    if (parent === dir) throw new Error(`No VERSION file above ${process.cwd()}`)
    dir = parent
  }
}

describe('the displayed version', () => {
  it('is the one in the VERSION file', () => {
    expect(__APP_VERSION__).toBe(readVersionFile())
  })

  it('is written the way releases are numbered', () => {
    expect(__APP_VERSION__).toMatch(/^\d+\.\d{2}$/)
  })
})
