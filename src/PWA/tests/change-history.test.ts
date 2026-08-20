import { existsSync, readFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

function repoFile(path: string): string {
  let dir = resolve(process.cwd())

  for (;;) {
    const root = join(dir, 'VERSION')
    if (existsSync(root)) return readFileSync(join(dir, path), 'utf8')

    const parent = dirname(dir)
    if (parent === dir) throw new Error(`No VERSION file above ${process.cwd()}`)
    dir = parent
  }
}

function versionsThrough(current: string): string[] {
  const [major, minor] = current.split('.').map(Number)
  const ordinal = major * 100 + minor

  return Array.from({ length: ordinal }, (_, index) => {
    const value = index + 1
    return `${Math.floor(value / 100)}.${String(value % 100).padStart(2, '0')}`
  })
}

describe('the public change history', () => {
  const current = repoFile('VERSION').trim()
  const history = repoFile('src/PWA/public/change-history.html')

  it('has one newest-first entry for every release through VERSION', () => {
    const entries = [...history.matchAll(/<h2 id="v(\d+\.\d{2})">/g)].map((match) => match[1])
    expect(entries).toEqual(versionsThrough(current).reverse())
  })

  it('is linked from the public readme', () => {
    expect(repoFile('src/PWA/public/readme.html')).toContain(
      'href="/change-history.html">Change history</a>',
    )
  })
})
