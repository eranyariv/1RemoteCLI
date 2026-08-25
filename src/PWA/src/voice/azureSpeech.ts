import type {
  SpeechRecognizer,
  SpeechSynthesizer,
} from 'microsoft-cognitiveservices-speech-sdk'

import { auth } from '../auth/impl'
import { resolveHubUrl } from '../relay/endpoint'
import { MAX_RECOGNIZED_TEXT_CHARS, MAX_SPEECH_TEXT_CHARS } from './output'

export const MAX_UTTERANCE_MS = 30_000
const LISTEN_OPERATION_TIMEOUT_MS = MAX_UTTERANCE_MS + 5_000
const TOKEN_REFRESH_MARGIN_MS = 60_000

export interface SpeechGrant {
  token: string
  region: string
  recognitionLanguage: string
  voiceName: string
  expiresAt: number
}

export interface SpeechProvider {
  listen(): Promise<string>
  speak(text: string): Promise<void>
  cancel(): void
  dispose(): void
}

export function validateRecognizedText(value: string): string {
  const text = value.trim()
  if (text.length > MAX_RECOGNIZED_TEXT_CHARS) {
    throw new Error(`Voice input is limited to ${MAX_RECOGNIZED_TEXT_CHARS} characters.`)
  }
  return text
}

export function validateSpeechText(value: string): string {
  const text = value.trim()
  if (text.length > MAX_SPEECH_TEXT_CHARS) {
    throw new Error(`Spoken output is limited to ${MAX_SPEECH_TEXT_CHARS} characters.`)
  }
  return text
}

function voiceApiUrl(path: string): string {
  const hub = new URL(resolveHubUrl())
  const hubBase = hub.pathname.replace(/\/hub\/?$/i, '').replace(/\/+$/, '')
  return `${hub.origin}${hubBase}/api/voice/${path}`
}

function textField(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`Voice service returned an invalid ${field}.`)
  }
  return value.trim()
}

export function decodeSpeechGrant(value: unknown, now = Date.now()): SpeechGrant {
  if (!value || typeof value !== 'object') throw new Error('Voice service returned an invalid response.')

  const item = value as Record<string, unknown>
  const expires = typeof item.expiresAt === 'string' ? Date.parse(item.expiresAt) : Number.NaN

  return {
    token: textField(item.token, 'token'),
    region: textField(item.region, 'region'),
    recognitionLanguage:
      typeof item.recognitionLanguage === 'string' && item.recognitionLanguage.trim()
        ? item.recognitionLanguage.trim()
        : 'en-US',
    voiceName:
      typeof item.voiceName === 'string' && item.voiceName.trim()
        ? item.voiceName.trim()
        : 'en-US-AvaMultilingualNeural',
    expiresAt: Number.isFinite(expires) ? expires : now + 9 * 60_000,
  }
}

export async function requestSpeechGrant(
  getAccessToken: () => Promise<string | null> = () => auth.getAccessToken(),
  fetcher: typeof fetch = fetch,
): Promise<SpeechGrant> {
  const accessToken = await getAccessToken()
  if (!accessToken) throw new Error('Sign in again before starting voice mode.')

  const response = await fetcher(voiceApiUrl('token'), {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${accessToken}`,
      'Content-Type': 'application/json',
    },
    body: '{}',
  })

  if (!response.ok) {
    let detail = ''
    try {
      const body = (await response.json()) as { detail?: unknown }
      if (typeof body.detail === 'string') detail = body.detail
    } catch {
      // The status is still useful when a proxy replaced the JSON error body.
    }

    throw new Error(detail || `Voice service is unavailable (${response.status}).`)
  }

  return decodeSpeechGrant(await response.json())
}

export class AzureSpeechProvider implements SpeechProvider {
  private grant: SpeechGrant | null = null
  private recognizer: SpeechRecognizer | null = null
  private synthesizer: SpeechSynthesizer | null = null
  private operation = 0

  cancel(): void {
    this.operation += 1
    this.recognizer?.close()
    this.synthesizer?.close()
    this.recognizer = null
    this.synthesizer = null
  }

  dispose(): void {
    this.cancel()
    this.grant = null
  }

  async listen(): Promise<string> {
    const operation = ++this.operation
    const [SpeechSDK, grant] = await Promise.all([
      import('microsoft-cognitiveservices-speech-sdk'),
      this.currentGrant(),
    ])
    if (operation !== this.operation) throw new Error('Listening was cancelled.')

    const config = SpeechSDK.SpeechConfig.fromAuthorizationToken(grant.token, grant.region)
    config.speechRecognitionLanguage = grant.recognitionLanguage
    config.setProperty(SpeechSDK.PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, '10000')
    config.setProperty(
      SpeechSDK.PropertyId.Speech_SegmentationMaximumTimeMs,
      String(MAX_UTTERANCE_MS),
    )

    const audio = SpeechSDK.AudioConfig.fromDefaultMicrophoneInput()
    const recognizer = new SpeechSDK.SpeechRecognizer(config, audio)
    this.recognizer = recognizer

    return new Promise<string>((resolve, reject) => {
      let settled = false

      const finish = (result: { text?: string } | null, error?: string) => {
        if (settled) return
        settled = true
        clearTimeout(timeout)
        recognizer.close()
        if (this.recognizer === recognizer) this.recognizer = null

        if (operation !== this.operation) {
          reject(new Error('Listening was cancelled.'))
          return
        }
        if (error) {
          reject(new Error(error))
          return
        }

        try {
          resolve(validateRecognizedText(result?.text ?? ''))
        } catch (validationError) {
          reject(validationError)
        }
      }

      const timeout = setTimeout(() => {
        finish(null, `Listening is limited to ${MAX_UTTERANCE_MS / 1_000} seconds.`)
      }, LISTEN_OPERATION_TIMEOUT_MS)

      recognizer.recognizeOnceAsync(
        (result) => {
          if (result.reason === SpeechSDK.ResultReason.RecognizedSpeech) {
            finish(result)
            return
          }
          if (result.reason === SpeechSDK.ResultReason.NoMatch) {
            finish(null)
            return
          }

          const details = SpeechSDK.CancellationDetails.fromResult(result)
          finish(null, details.errorDetails || 'Azure Speech could not recognize that utterance.')
        },
        (error) => finish(null, error || 'Azure Speech recognition failed.'),
      )
    })
  }

  async speak(text: string): Promise<void> {
    const message = validateSpeechText(text)
    if (!message) return

    const operation = ++this.operation
    const [SpeechSDK, grant] = await Promise.all([
      import('microsoft-cognitiveservices-speech-sdk'),
      this.currentGrant(),
    ])
    if (operation !== this.operation) throw new Error('Speech was cancelled.')

    const config = SpeechSDK.SpeechConfig.fromAuthorizationToken(grant.token, grant.region)
    config.speechSynthesisVoiceName = grant.voiceName
    const audio = SpeechSDK.AudioConfig.fromDefaultSpeakerOutput()
    const synthesizer = new SpeechSDK.SpeechSynthesizer(config, audio)
    this.synthesizer = synthesizer

    return new Promise<void>((resolve, reject) => {
      synthesizer.speakTextAsync(
        message,
        (result) => {
          synthesizer.close()
          if (this.synthesizer === synthesizer) this.synthesizer = null

          if (operation !== this.operation) {
            reject(new Error('Speech was cancelled.'))
          } else if (result.reason === SpeechSDK.ResultReason.SynthesizingAudioCompleted) {
            resolve()
          } else {
            const details = SpeechSDK.CancellationDetails.fromResult(result)
            reject(new Error(details.errorDetails || 'Azure Speech synthesis failed.'))
          }
        },
        (error) => {
          synthesizer.close()
          if (this.synthesizer === synthesizer) this.synthesizer = null
          reject(new Error(error || 'Azure Speech synthesis failed.'))
        },
      )
    })
  }

  private async currentGrant(): Promise<SpeechGrant> {
    if (this.grant && this.grant.expiresAt - Date.now() > TOKEN_REFRESH_MARGIN_MS) {
      return this.grant
    }

    this.grant = await requestSpeechGrant()
    return this.grant
  }
}
