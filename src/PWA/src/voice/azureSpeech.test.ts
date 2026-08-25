import { describe, expect, it, vi } from 'vitest'

import {
  decodeSpeechGrant,
  MAX_UTTERANCE_MS,
  requestSpeechGrant,
  validateRecognizedText,
  validateSpeechText,
} from './azureSpeech'

describe('Azure Speech grant', () => {
  it('accepts a short-lived token without exposing server credentials', () => {
    expect(
      decodeSpeechGrant(
        {
          token: 'short-lived',
          region: 'eastus',
          recognitionLanguage: 'en-US',
          voiceName: 'en-US-AvaMultilingualNeural',
          expiresAt: '2030-01-01T00:00:00Z',
        },
        0,
      ),
    ).toMatchObject({
      token: 'short-lived',
      region: 'eastus',
      recognitionLanguage: 'en-US',
    })
  })

  it('surfaces provider failures without attempting a success-shaped fallback', async () => {
    const fetcher = vi.fn(async () =>
      new Response(JSON.stringify({ detail: 'Azure Speech is not configured.' }), {
        status: 503,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    await expect(
      requestSpeechGrant(async () => 'hub-token', fetcher as typeof fetch),
    ).rejects.toThrow('Azure Speech is not configured.')
  })

  it('requires the existing signed-in identity', async () => {
    const fetcher = vi.fn()
    await expect(requestSpeechGrant(async () => null, fetcher)).rejects.toThrow('Sign in again')
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('enforces bounded recognition and synthesis operations', () => {
    expect(MAX_UTTERANCE_MS).toBe(30_000)
    expect(validateRecognizedText('a'.repeat(4_000))).toHaveLength(4_000)
    expect(() => validateRecognizedText('a'.repeat(4_001))).toThrow('4000')
    expect(validateSpeechText('a'.repeat(2_000))).toHaveLength(2_000)
    expect(() => validateSpeechText('a'.repeat(2_001))).toThrow('2000')
  })
})
