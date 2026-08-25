import { describe, expect, it } from 'vitest'

import { matchSpokenChoice, routeVoiceUtterance, type SpokenChoice } from './routing'

const choices: SpokenChoice<string>[] = [
  { value: 'one', label: 'Checkout', aliases: ['Checkout on Desk'] },
  { value: 'two', label: 'Checkout', aliases: ['Checkout on Laptop'] },
  { value: 'three', label: 'Documentation' },
]

describe('voice intent routing', () => {
  it.each([
    ['back to projects', 'back-projects'],
    ['go back to the sessions', 'back-sessions'],
    ['repeat', 'repeat'],
    ['cancel', 'cancel'],
    ['stop voice mode', 'stop'],
    ['hear more detail', 'more'],
  ])('intercepts %s as %s', (speech, kind) => {
    expect(routeVoiceUtterance(speech).kind).toBe(kind)
  })

  it('leaves ordinary session text unchanged', () => {
    expect(routeVoiceUtterance('Explain the failing test')).toEqual({
      kind: 'content',
      text: 'Explain the failing test',
    })
  })
})

describe('spoken choice matching', () => {
  it('accepts a number or an unambiguous name', () => {
    expect(matchSpokenChoice('number three', choices)).toMatchObject({
      kind: 'match',
      choice: { value: 'three' },
    })
    expect(matchSpokenChoice('select documentation', choices)).toMatchObject({
      kind: 'match',
      choice: { value: 'three' },
    })
  })

  it('returns every exact ambiguous name rather than silently selecting', () => {
    const result = matchSpokenChoice('checkout', choices)
    expect(result.kind).toBe('ambiguous')
    if (result.kind === 'ambiguous') expect(result.choices).toHaveLength(2)
  })

  it('uses a distinguishing alias', () => {
    expect(matchSpokenChoice('checkout on laptop', choices)).toMatchObject({
      kind: 'match',
      choice: { value: 'two' },
    })
  })
})
