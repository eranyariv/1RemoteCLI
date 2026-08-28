import { afterEach, describe, expect, it, vi } from 'vitest'

import { refocusTerminalIfActive } from './focus'

describe('refocusTerminalIfActive', () => {
  afterEach(() => {
    document.body.replaceChildren()
  })

  it('preserves focus when the software keyboard is already active', () => {
    const host = document.createElement('div')
    const input = document.createElement('textarea')
    host.append(input)
    document.body.append(host)
    input.focus()
    const terminal = { focus: vi.fn() }

    refocusTerminalIfActive(host, terminal)

    expect(terminal.focus).toHaveBeenCalledOnce()
  })

  it('does not summon the software keyboard when terminal focus is hidden', () => {
    const host = document.createElement('div')
    const input = document.createElement('textarea')
    host.append(input)
    document.body.append(host)
    const terminal = { focus: vi.fn() }

    refocusTerminalIfActive(host, terminal)

    expect(terminal.focus).not.toHaveBeenCalled()
  })
})
