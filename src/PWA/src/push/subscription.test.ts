import { describe, expect, it } from 'vitest'

import {
  applicationServerKey,
  describeSubscription,
  readDeepLink,
  toBase64Url,
  withoutDeepLink,
} from './subscription'

/** A stand-in for the browser's PushSubscription, which jsdom does not provide. */
function fakeSubscription(keys: Partial<Record<'p256dh' | 'auth', ArrayBuffer>>): PushSubscription {
  return {
    endpoint: 'https://push.example/abc',
    getKey: (name: string) => keys[name as 'p256dh' | 'auth'] ?? null,
  } as unknown as PushSubscription
}

function bytes(...values: number[]): ArrayBuffer {
  return new Uint8Array(values).buffer
}

describe('applicationServerKey', () => {
  it('decodes an unpadded key', () => {
    // "hi" is 2 bytes -> "aGk=" in base64, which arrives without its "=".
    expect(Array.from(applicationServerKey('aGk'))).toEqual([0x68, 0x69])
  })

  it('decodes the base64url alphabet', () => {
    // 0xfb 0xff is "+/8=" in base64 and "-_8" in base64url. A decoder that
    // forgot the substitution would throw here rather than quietly differ.
    expect(Array.from(applicationServerKey('-_8'))).toEqual([0xfb, 0xff])
  })

  it('produces the 65 bytes a real VAPID key decodes to', () => {
    const key = toBase64Url(new Uint8Array(65).fill(7).buffer)
    expect(applicationServerKey(key)).toHaveLength(65)
  })

  it('tolerates surrounding whitespace', () => {
    expect(Array.from(applicationServerKey('  aGk\n'))).toEqual([0x68, 0x69])
  })
})

describe('toBase64Url', () => {
  it('strips padding and substitutes the alphabet', () => {
    expect(toBase64Url(bytes(0xfb, 0xff))).toBe('-_8')
  })

  it('round-trips at every remainder', () => {
    for (const length of [1, 2, 3, 4, 5, 65]) {
      const source = new Uint8Array(length).map((_, index) => (index * 37) % 256)
      const encoded = toBase64Url(source.buffer)

      expect(encoded).not.toContain('=')
      expect(Array.from(applicationServerKey(encoded))).toEqual(Array.from(source))
    }
  })
})

describe('describeSubscription', () => {
  it('reads the endpoint and both keys', () => {
    const described = describeSubscription(
      fakeSubscription({ p256dh: bytes(0xfb, 0xff), auth: bytes(0x68, 0x69) }),
    )

    expect(described).toEqual({
      endpoint: 'https://push.example/abc',
      keys: { p256dh: '-_8', auth: 'aGk' },
    })
  })

  it('gives up rather than throwing when a key is missing', () => {
    // Should not happen; a browser that did it must not take the app down on
    // start-up, because notifications are not worth that.
    expect(describeSubscription(fakeSubscription({ p256dh: bytes(1) }))).toBeNull()
    expect(describeSubscription(fakeSubscription({ auth: bytes(1) }))).toBeNull()
    expect(describeSubscription(fakeSubscription({}))).toBeNull()
  })
})

describe('readDeepLink', () => {
  it('reads both ids', () => {
    expect(readDeepLink('?machine=desk&session=s1')).toEqual({
      machineId: 'desk',
      sessionId: 's1',
    })
  })

  it('unescapes ids', () => {
    expect(readDeepLink('?machine=a%20b&session=c%26d')).toEqual({
      machineId: 'a b',
      sessionId: 'c&d',
    })
  })

  it('needs both halves', () => {
    expect(readDeepLink('?machine=desk')).toBeNull()
    expect(readDeepLink('?session=s1')).toBeNull()
    expect(readDeepLink('')).toBeNull()
    expect(readDeepLink('?other=1')).toBeNull()
  })
})

describe('withoutDeepLink', () => {
  it('removes the link and keeps the rest of the URL', () => {
    expect(withoutDeepLink('https://app.example/?machine=desk&session=s1&keep=1')).toBe(
      '/?keep=1',
    )
  })

  it('leaves a bare path alone', () => {
    expect(withoutDeepLink('https://app.example/')).toBe('/')
  })

  it('preserves the hash', () => {
    expect(withoutDeepLink('https://app.example/?machine=a&session=b#top')).toBe('/#top')
  })

  it('leaves nothing for readDeepLink to find', () => {
    const stripped = withoutDeepLink('https://app.example/?machine=desk&session=s1')
    expect(readDeepLink(new URL(stripped, 'https://app.example').search)).toBeNull()
  })
})
