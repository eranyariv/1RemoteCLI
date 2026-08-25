import { describe, expect, it } from 'vitest'

import { RecentUtterances, speechChunk, summarizeTerminal, terminalText } from './output'

describe('spoken output', () => {
  it('removes terminal control sequences and summarizes from the useful tail', () => {
    const data = new TextEncoder().encode('\u001b[31mfailed\u001b[0m\r\nat the final step')
    const text = terminalText(data)
    expect(text).toBe('failed at the final step')
    expect(summarizeTerminal(`prefix ${'x'.repeat(500)} final result`, 40)).toContain('final result')
  })

  it('chunks long output into bounded speech', () => {
    const value = `${'a'.repeat(1_500)}. ${'b'.repeat(1_500)}`
    const first = speechChunk(value)
    expect(first.text.length).toBeLessThanOrEqual(2_000)
    expect(first.nextOffset).not.toBeNull()
    expect(speechChunk(value, first.nextOffset!).text).not.toBe('')
  })

  it('drops recognition retries only inside the idempotency window', () => {
    const recent = new RecentUtterances(1_000)
    expect(recent.isDuplicate('Run tests', 100)).toBe(false)
    expect(recent.isDuplicate('run tests', 500)).toBe(true)
    expect(recent.isDuplicate('run tests', 1_501)).toBe(false)
  })
})
