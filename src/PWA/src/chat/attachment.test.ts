import { describe, expect, it } from 'vitest'

import {
  attachmentsAllowed,
  describeType,
  formatBytes,
  isImageFile,
  rejectAttachment,
  MAX_CHAT_ATTACHMENT_BYTES,
  MAX_CHAT_ATTACHMENT_COUNT,
  MAX_CHAT_PROMPT_BYTES,
  type ChatAttachmentDraft,
} from './attachment'

/**
 * The browser half of the shared attachment policy.
 *
 * Everything here is checked again on the machine, against the bytes rather than
 * against a declared type — this exists so a phone can say "that is 12 MB" before
 * spending a minute uploading it, not because the browser is trusted.
 */

function file(name: string, type: string, size: number): File {
  const blob = new Blob([new Uint8Array(0)], { type })
  Object.defineProperty(blob, 'size', { value: size })
  Object.defineProperty(blob, 'name', { value: name })
  Object.defineProperty(blob, 'type', { value: type })
  return blob as File
}

function draft(size: number): ChatAttachmentDraft {
  return {
    attachmentId: crypto.randomUUID(),
    name: 'existing.bin',
    mimeType: 'application/octet-stream',
    size,
    status: 'ready',
    confirmedBytes: size,
    previewUrl: null,
    error: null,
  }
}

const both = { image: true, embeddedContext: true }

describe('what a composer may offer', () => {
  it('offers nothing when the agent advertised nothing', () => {
    expect(attachmentsAllowed(null)).toBe(false)
    expect(attachmentsAllowed({ image: false, embeddedContext: false })).toBe(false)
    expect(attachmentsAllowed({ image: true, embeddedContext: false })).toBe(true)
    expect(attachmentsAllowed({ image: false, embeddedContext: true })).toBe(true)
  })

  it('refuses everything when the session has no capabilities at all', () => {
    expect(rejectAttachment(file('a.png', 'image/png', 10), null, [])).toBe(
      'This agent does not accept attachments.',
    )
  })

  it('refuses an image when only embedded context was advertised, and the reverse', () => {
    expect(
      rejectAttachment(file('a.png', 'image/png', 10), { image: false, embeddedContext: true }, []),
    ).toBe('This agent does not accept images.')

    expect(
      rejectAttachment(file('a.txt', 'text/plain', 10), { image: true, embeddedContext: false }, []),
    ).toBe('This agent does not accept file attachments.')
  })

  it('refuses an image type ACP cannot carry, and accepts the four it can', () => {
    expect(rejectAttachment(file('a.avif', 'image/avif', 10), both, [])).toContain('not a PNG')

    for (const type of ['image/png', 'image/jpeg', 'image/webp', 'image/gif']) {
      expect(rejectAttachment(file(`a.${type.slice(6)}`, type, 10), both, [])).toBeNull()
    }
  })

  it('accepts ordinary documents when embedded context is advertised', () => {
    expect(rejectAttachment(file('notes.md', 'text/markdown', 10), both, [])).toBeNull()
    expect(rejectAttachment(file('data.bin', '', 10), both, [])).toBeNull()
  })

  it('enforces the per-file, aggregate, and count limits', () => {
    expect(
      rejectAttachment(file('huge.png', 'image/png', MAX_CHAT_ATTACHMENT_BYTES + 1), both, []),
    ).toContain('larger than')

    expect(rejectAttachment(file('empty.txt', 'text/plain', 0), both, [])).toContain('is empty')

    const nearlyFull = [draft(MAX_CHAT_PROMPT_BYTES - 1)]
    expect(rejectAttachment(file('extra.txt', 'text/plain', 1024), both, nearlyFull)).toContain(
      'All attachments on one prompt',
    )

    const full = Array.from({ length: MAX_CHAT_ATTACHMENT_COUNT }, () => draft(1))
    expect(rejectAttachment(file('one-more.txt', 'text/plain', 1), both, full)).toContain(
      `at most ${MAX_CHAT_ATTACHMENT_COUNT}`,
    )
  })
})

describe('what a person reads', () => {
  it('formats sizes without spurious precision', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(2048)).toBe('2 KB')
    expect(formatBytes(1.5 * 1024 * 1024)).toBe('1.5 MB')
  })

  it('names a type from the subtype, or from the extension when there is none', () => {
    expect(describeType('image/png', 'a.png')).toBe('PNG')
    expect(describeType('application/octet-stream', 'archive.7z')).toBe('7Z')
    expect(describeType('', 'no-extension')).toBe('File')
  })

  it('knows a picture from a document', () => {
    expect(isImageFile({ type: 'image/JPEG' })).toBe(true)
    expect(isImageFile({ type: 'application/pdf' })).toBe(false)
  })
})
