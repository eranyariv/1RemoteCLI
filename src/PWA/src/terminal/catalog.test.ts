import { describe, expect, it } from 'vitest'

import { CLI_TYPES, type CliType } from '../protocol/wire'
import { catalogFor, labelFor } from './catalog'

/**
 * The catalogue is data, so the tests are about the properties that make it usable
 * rather than about its contents. Asserting that Claude Code offers `/compact` would
 * only pin a list to itself; asserting that no type is missing, that every command
 * is typeable, and that the fallback is honest catches the ways this actually breaks.
 */
describe('the per-CLI catalogue', () => {
  it('has an entry for every type the protocol can send', () => {
    for (const type of CLI_TYPES) {
      expect(catalogFor(type), type).toBeDefined()
      expect(labelFor(type).length, type).toBeGreaterThan(0)
    }
  })

  it('falls back rather than crashing on a type from a newer hub', () => {
    // The decoder is supposed to prevent this, but a lookup that returns undefined
    // takes the whole terminal view down with it, and the terminal view is the part
    // of the app somebody is relying on at the time.
    const unknown = 'Emacs' as CliType

    expect(catalogFor(unknown)).toBe(catalogFor('Generic'))
  })

  it('does not guess at commands for a program it cannot name', () => {
    // Offering `/clear` to something that might be `bash` would be a button whose
    // effect nobody can predict.
    expect(catalogFor('Generic').commands).toEqual([])
    expect(catalogFor('Generic').shortcuts.length).toBeGreaterThan(0)
  })

  it('gives every button something to send and something to say', () => {
    for (const type of CLI_TYPES) {
      const catalog = catalogFor(type)

      for (const key of catalog.shortcuts) {
        expect(key.bytes.length, `${type} ${key.label}`).toBeGreaterThan(0)
        expect(key.label.length, type).toBeGreaterThan(0)
        expect(key.name.length, type).toBeGreaterThan(0)
      }

      for (const command of catalog.commands) {
        expect(command.text.trim(), type).toBe(command.text)
        expect(command.text.length, type).toBeGreaterThan(0)
        expect(command.description.length, command.text).toBeGreaterThan(0)
      }
    }
  })

  it('offers each command once per CLI', () => {
    for (const type of CLI_TYPES) {
      const texts = catalogFor(type).commands.map((c) => c.text)

      expect(new Set(texts).size, type).toBe(texts.length)
    }
  })

  /**
   * `\x1b[Z` is what a terminal sends for Shift+Tab, and it is the single most
   * valuable button on the bar for either agent: it is how you change what the thing
   * is allowed to do without touching a config file.
   */
  it('sends the real Shift+Tab to the agents that listen for it', () => {
    for (const type of ['ClaudeCode', 'CopilotCli'] as const) {
      const key = catalogFor(type).shortcuts.find((k) => k.name.startsWith('Shift+Tab'))

      expect(key, type).toBeDefined()
      expect([...key!.bytes], type).toEqual([0x1b, 0x5b, 0x5a])
    }
  })

  it('sends two escapes as two escapes', () => {
    // Claude Code tells a single Esc from a double by timing, and a relayed double
    // tap cannot be relied on to land inside that window.
    const key = catalogFor('ClaudeCode').shortcuts.find((k) => k.label === '⎋⎋')

    expect([...key!.bytes]).toEqual([0x1b, 0x1b])
  })
})
