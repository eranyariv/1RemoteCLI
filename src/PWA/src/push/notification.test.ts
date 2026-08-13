import { describe, expect, it } from 'vitest'

import { readPushPayload } from './notification'

const Fallback = {
  title: '1RemoteCLI',
  body: 'A session needs your attention.',
  url: '/',
  tag: '1remotecli',
}

describe('readPushPayload', () => {
  it('reads a well-formed payload', () => {
    expect(
      readPushPayload(
        JSON.stringify({
          title: 'desk',
          body: 'claude is waiting',
          url: '/?machine=m1&session=s1',
          tag: 'awaiting:s1',
        }),
      ),
    ).toEqual({
      title: 'desk',
      body: 'claude is waiting',
      url: '/?machine=m1&session=s1',
      tag: 'awaiting:s1',
    })
  })

  // Each of these must still produce a notification: iOS revokes push permission
  // from an app that receives a push and shows nothing, so a bad payload costs
  // every future notification too, not just this one.
  it.each([
    ['nothing', undefined],
    ['empty', ''],
    ['not JSON', 'not json at all'],
    ['null', 'null'],
    ['a bare number', '42'],
    ['an empty object', '{}'],
    ['fields of the wrong type', JSON.stringify({ title: 7, body: [], url: {} })],
    ['blank strings', JSON.stringify({ title: '   ', body: '', url: '' })],
  ])('falls back on %s', (_name, text) => {
    expect(readPushPayload(text)).toEqual(Fallback)
  })

  it('reduces an absolute URL to its path', () => {
    // The payload is authenticated by nothing the browser checks. Following an
    // absolute URL would let anyone holding the endpoint turn a tap on this
    // app's notification into a visit to their site.
    expect(readPushPayload(JSON.stringify({ url: 'https://evil.example/steal?x=1' })).url).toBe(
      '/steal?x=1',
    )
  })

  it.each([
    'javascript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    '//evil.example/x',
    'http://evil.example',
  ])('never keeps the origin of %s', (url) => {
    const plan = readPushPayload(JSON.stringify({ url }))
    expect(plan.url.startsWith('/')).toBe(true)
    expect(plan.url).not.toContain('evil.example')
    expect(plan.url).not.toContain('javascript:')
  })

  it('keeps the query string, which carries the session', () => {
    expect(readPushPayload(JSON.stringify({ url: '/?machine=m1&session=s1' })).url).toBe(
      '/?machine=m1&session=s1',
    )
  })

  it('falls back to the URL for the tag', () => {
    // Not to a constant: one shared tag would mean a second waiting session
    // silently replaced the notification about the first.
    const first = readPushPayload(JSON.stringify({ url: '/?machine=m&session=a' }))
    const second = readPushPayload(JSON.stringify({ url: '/?machine=m&session=b' }))

    expect(first.tag).toBe('/?machine=m&session=a')
    expect(first.tag).not.toBe(second.tag)
  })
})
