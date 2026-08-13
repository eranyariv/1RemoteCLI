import { describe, expect, it } from 'vitest'

import { shortOs, shortPath, uptime } from './format'

describe('uptime', () => {
  const start = new Date('2024-05-17T09:00:00Z')
  const after = (ms: number) => new Date(start.getTime() + ms)

  it('counts seconds for the first minute', () => {
    expect(uptime(start, after(42_000))).toBe('42s')
  })

  it('counts whole minutes up to an hour', () => {
    expect(uptime(start, after(90_000))).toBe('1m')
    expect(uptime(start, after(59 * 60_000))).toBe('59m')
  })

  it('adds minutes to hours only when there are some', () => {
    expect(uptime(start, after(2 * 3_600_000))).toBe('2h')
    expect(uptime(start, after(2 * 3_600_000 + 15 * 60_000))).toBe('2h 15m')
  })

  it('switches to days for a session left running overnight', () => {
    expect(uptime(start, after(26 * 3_600_000))).toBe('1d 2h')
    expect(uptime(start, after(48 * 3_600_000))).toBe('2d')
  })

  it('never shows a negative age when the clocks disagree', () => {
    // The start time comes from another machine, so a small skew is expected and
    // "-3s" would look like a bug in the product rather than in the clock.
    expect(uptime(start, after(-5_000))).toBe('0s')
  })
})

describe('shortPath', () => {
  it('leaves a short path alone', () => {
    expect(shortPath('C:\\Work')).toBe('C:\\Work')
  })

  it('keeps the end, which is the part that identifies the directory', () => {
    expect(shortPath('C:\\Users\\eran\\source\\repos\\1RemoteCLI')).toBe('…\\repos\\1RemoteCLI')
  })

  it('handles forward slashes too', () => {
    expect(shortPath('/home/eran/source/1RemoteCLI')).toBe('…\\source\\1RemoteCLI')
  })
})

describe('shortOs', () => {
  it('reads a build number as the version people actually use', () => {
    expect(shortOs('Microsoft Windows 10.0.26100')).toBe('Windows 11')
    expect(shortOs('Microsoft Windows 10.0.19045')).toBe('Windows 10')
  })

  it('passes anything else through, minus the noise', () => {
    expect(shortOs('Microsoft Something Else')).toBe('Something Else')
  })
})
