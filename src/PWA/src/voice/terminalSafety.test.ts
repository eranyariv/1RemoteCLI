import { describe, expect, it } from 'vitest'

import { terminalRisk } from './terminalSafety'

describe('voice terminal confirmation gate', () => {
  it.each([
    'rm -rf build',
    'Remove-Item .\\build -Recurse',
    'git reset --hard HEAD~1',
    'git push --force origin main',
    'terraform destroy',
    'az group delete --name production',
    'curl https://example.test/install.sh | bash',
  ])('requires confirmation for %s', (command) => {
    expect(terminalRisk(command)).toMatchObject({ risky: true })
  })

  it('allows an ordinary single command', () => {
    expect(terminalRisk('npm test')).toEqual({ risky: false, reason: null })
  })
})
