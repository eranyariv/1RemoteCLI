import type { ChatEvent } from '../protocol/wire'

export const MAX_SPEECH_TEXT_CHARS = 2_000
export const MAX_RECOGNIZED_TEXT_CHARS = 4_000

function stripTerminalControl(value: string): string {
  let clean = ''

  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index)

    if (code === 27) {
      const kind = value[index + 1]
      index += 1

      if (kind === '[') {
        while (index + 1 < value.length) {
          index += 1
          const final = value.charCodeAt(index)
          if (final >= 64 && final <= 126) break
        }
      } else if (kind === ']') {
        while (index + 1 < value.length) {
          index += 1
          const current = value.charCodeAt(index)
          if (current === 7) break
          if (current === 27 && value[index + 1] === '\\') {
            index += 1
            break
          }
        }
      }
      continue
    }

    if (code === 127 || (code < 32 && code !== 9 && code !== 10 && code !== 13)) continue
    clean += value[index]
  }

  return clean
}

export function cleanForSpeech(value: string): string {
  return stripTerminalControl(value)
    .replace(/[`*_#>]/g, '')
    .replace(/\s+/g, ' ')
    .trim()
}

export function speechChunk(value: string, offset = 0): { text: string; nextOffset: number | null } {
  const clean = cleanForSpeech(value)
  if (offset >= clean.length) return { text: '', nextOffset: null }

  const wanted = clean.slice(offset, offset + MAX_SPEECH_TEXT_CHARS)
  if (offset + wanted.length >= clean.length) return { text: wanted, nextOffset: null }

  const boundary = Math.max(wanted.lastIndexOf('. '), wanted.lastIndexOf(' '), 1)
  return { text: wanted.slice(0, boundary + 1).trim(), nextOffset: offset + boundary + 1 }
}

export function terminalText(data: Uint8Array): string {
  return cleanForSpeech(new TextDecoder().decode(data))
}

export function summarizeTerminal(value: string, maxChars = 420): string {
  const clean = cleanForSpeech(value)
  if (!clean) return ''
  if (clean.length <= maxChars) return clean

  const tail = clean.slice(-maxChars)
  const firstSpace = tail.indexOf(' ')
  const summary = firstSpace > 0 ? tail.slice(firstSpace + 1) : tail
  return `The latest terminal output ends with: ${summary}`
}

export function chatEventSpeech(event: ChatEvent): string {
  if (event.kind === 'AgentMessage') return cleanForSpeech(event.text)
  if (event.kind !== 'Permission' || event.status !== 'pending') return ''

  const request = cleanForSpeech(event.title ?? event.text ?? 'The agent needs your approval.')
  const choices = event.options
    .map((option, index) => `${index + 1}, ${cleanForSpeech(option.name)}.`)
    .join(' ')
  return choices ? `${request} ${choices}` : request
}

export class RecentUtterances {
  private readonly seen = new Map<string, number>()
  private readonly windowMs: number

  constructor(windowMs = 2_000) {
    this.windowMs = windowMs
  }

  isDuplicate(value: string, now = Date.now()): boolean {
    const normalized = cleanForSpeech(value).toLocaleLowerCase()
    if (!normalized) return false

    for (const [key, seenAt] of this.seen) {
      if (now - seenAt > this.windowMs) this.seen.delete(key)
    }

    const prior = this.seen.get(normalized)
    this.seen.set(normalized, now)
    return prior !== undefined && now - prior <= this.windowMs
  }
}
