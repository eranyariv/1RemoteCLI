import { describe, expect, it } from 'vitest'

import { pasteClipboardText, quoteTerminalPath } from './attachment'

describe('terminal attachments', () => {
  it('quotes PowerShell paths literally', () => {
    expect(quoteTerminalPath("C:\\Users\\O'Brien\\photo one.png", 'PowerShell')).toBe(
      "'C:\\Users\\O''Brien\\photo one.png'",
    )
  })

  it('quotes cmd and interactive CLI paths as Windows arguments', () => {
    expect(quoteTerminalPath('C:\\Temp\\photo one.png', 'Cmd')).toBe(
      '"C:\\Temp\\photo one.png"',
    )
    expect(quoteTerminalPath('C:\\Temp\\photo one.png', 'ClaudeCode')).toBe(
      '"C:\\Temp\\photo one.png"',
    )
  })

  it('hands multiline and escape-looking clipboard text to xterm paste unchanged', () => {
    const pasted: string[] = []
    const terminal = { paste: (text: string) => pasted.push(text) }

    pasteClipboardText(terminal, 'literal\n\u001b[31m')

    expect(pasted).toEqual(['literal\n\u001b[31m'])
  })
})
