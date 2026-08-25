export type VoiceIntent =
  | { kind: 'back-projects' }
  | { kind: 'back-sessions' }
  | { kind: 'repeat' }
  | { kind: 'cancel' }
  | { kind: 'stop' }
  | { kind: 'more' }
  | { kind: 'yes' }
  | { kind: 'no' }
  | { kind: 'content'; text: string }

export interface SpokenChoice<T> {
  value: T
  label: string
  aliases?: readonly string[]
}

export type ChoiceMatch<T> =
  | { kind: 'match'; choice: SpokenChoice<T> }
  | { kind: 'ambiguous'; choices: SpokenChoice<T>[] }
  | { kind: 'none' }

const NUMBER_WORDS: Readonly<Record<string, number>> = {
  one: 1,
  first: 1,
  two: 2,
  second: 2,
  three: 3,
  third: 3,
  four: 4,
  fourth: 4,
  five: 5,
  fifth: 5,
  six: 6,
  sixth: 6,
  seven: 7,
  seventh: 7,
  eight: 8,
  eighth: 8,
  nine: 9,
  ninth: 9,
  ten: 10,
  tenth: 10,
}

export function normalizeSpeech(value: string): string {
  return value
    .toLocaleLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim()
    .replace(/\s+/g, ' ')
}

export function routeVoiceUtterance(value: string): VoiceIntent {
  const text = normalizeSpeech(value)

  if (/^(?:go )?back to (?:the )?projects?$/.test(text) || text === 'projects') {
    return { kind: 'back-projects' }
  }
  if (/^(?:go )?back to (?:the )?sessions?$/.test(text) || text === 'sessions') {
    return { kind: 'back-sessions' }
  }
  if (/^(?:please )?(?:repeat|say that again)$/.test(text)) return { kind: 'repeat' }
  if (/^(?:please )?cancel$/.test(text)) return { kind: 'cancel' }
  if (/^(?:please )?(?:stop|exit|end)(?: voice mode)?$/.test(text)) return { kind: 'stop' }
  if (/^(?:hear |tell me |read )?(?:more|more detail|more details|full detail|full details)$/.test(text)) {
    return { kind: 'more' }
  }
  if (/^(?:yes|yeah|yep|confirm|send it|do it|proceed)$/.test(text)) return { kind: 'yes' }
  if (/^(?:no|nope|do not|don t|decline|reject)$/.test(text)) return { kind: 'no' }

  return { kind: 'content', text: value.trim() }
}

function selectionText(value: string): string {
  return normalizeSpeech(value)
    .replace(/^(?:(?:choose|select|open|pick)(?: number)?|number)\s+/, '')
    .replace(/^(?:the )?(?:project|session)\s+/, '')
}

function selectedNumber(value: string): number | null {
  const selected = selectionText(value)
  if (/^\d+$/.test(selected)) return Number(selected)
  return NUMBER_WORDS[selected] ?? null
}

export function matchSpokenChoice<T>(
  value: string,
  choices: readonly SpokenChoice<T>[],
): ChoiceMatch<T> {
  const number = selectedNumber(value)
  if (number !== null) {
    const choice = choices[number - 1]
    return choice ? { kind: 'match', choice } : { kind: 'none' }
  }

  const selected = selectionText(value)
  if (!selected) return { kind: 'none' }

  const exact = choices.filter((choice) =>
    [choice.label, ...(choice.aliases ?? [])].some(
      (candidate) => normalizeSpeech(candidate) === selected,
    ),
  )
  if (exact.length === 1) return { kind: 'match', choice: exact[0] }
  if (exact.length > 1) return { kind: 'ambiguous', choices: exact }

  const partial = choices.filter((choice) =>
    [choice.label, ...(choice.aliases ?? [])].some((candidate) => {
      const normalized = normalizeSpeech(candidate)
      return normalized.startsWith(`${selected} `) || selected.startsWith(`${normalized} `)
    }),
  )
  if (partial.length === 1) return { kind: 'match', choice: partial[0] }
  if (partial.length > 1) return { kind: 'ambiguous', choices: partial }

  return { kind: 'none' }
}

export function numberedChoices<T>(
  lead: string,
  choices: readonly SpokenChoice<T>[],
  empty: string,
): string {
  if (choices.length === 0) return empty
  return `${lead} ${choices.map((choice, index) => `${index + 1}, ${choice.label}.`).join(' ')}`
}
