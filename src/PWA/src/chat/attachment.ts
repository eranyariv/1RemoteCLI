import type { ChatCapabilities } from '../protocol/wire'

/**
 * Mirrors `ChatAttachmentLimits` in the shared C# protocol.
 *
 * Deliberately far below the terminal upload ceiling. A terminal attachment becomes
 * a file on disk; a chat attachment becomes Base64 inside one ACP prompt and then
 * part of a model's context, which is both smaller and more expensive than disk.
 */
export const MAX_CHAT_ATTACHMENT_BYTES = 5 * 1024 * 1024
export const MAX_CHAT_PROMPT_BYTES = 10 * 1024 * 1024
export const MAX_CHAT_ATTACHMENT_COUNT = 4
export const CHAT_ATTACHMENT_CHUNK_BYTES = 64 * 1024
export const MAX_CHAT_PROMPT_TEXT_CHARS = 20_000

/** The image types an ACP `image` block may carry — mirrors `ChatAttachmentPolicy`. */
export const CHAT_IMAGE_MIME_TYPES = ['image/png', 'image/jpeg', 'image/webp', 'image/gif'] as const

/** What the picker offers when the agent takes images but not embedded files. */
export const CHAT_IMAGE_ACCEPT = CHAT_IMAGE_MIME_TYPES.join(',')

export type ChatAttachmentStatus = 'uploading' | 'ready' | 'failed'

export interface ChatAttachmentDraft {
  attachmentId: string
  name: string
  mimeType: string
  size: number
  status: ChatAttachmentStatus
  confirmedBytes: number
  /** Set only for previewable images, and revoked when the draft goes away. */
  previewUrl: string | null
  error: string | null
}

/** Whether this session can carry any attachment at all. */
export function attachmentsAllowed(capabilities: ChatCapabilities | null): boolean {
  return capabilities !== null && (capabilities.image || capabilities.embeddedContext)
}

export function isImageFile(file: { type: string }): boolean {
  return file.type.toLowerCase().startsWith('image/')
}

function isSupportedImageType(type: string): boolean {
  return (CHAT_IMAGE_MIME_TYPES as readonly string[]).includes(
    type.toLowerCase().split(';')[0].trim(),
  )
}

/**
 * The browser's half of the same policy the agent enforces on the bytes.
 *
 * Checked here so a phone says "that is 12 MB" before spending a minute uploading
 * it, and checked again on the machine because a declared type and a declared size
 * are only ever claims.
 */
export function rejectAttachment(
  file: File,
  capabilities: ChatCapabilities | null,
  existing: readonly ChatAttachmentDraft[],
): string | null {
  if (!attachmentsAllowed(capabilities)) {
    return 'This agent does not accept attachments.'
  }

  if (existing.length >= MAX_CHAT_ATTACHMENT_COUNT) {
    return `A prompt carries at most ${MAX_CHAT_ATTACHMENT_COUNT} attachments.`
  }

  if (file.size === 0) {
    return `${file.name} is empty.`
  }

  if (file.size > MAX_CHAT_ATTACHMENT_BYTES) {
    return `${file.name} is larger than ${formatBytes(MAX_CHAT_ATTACHMENT_BYTES)}.`
  }

  const total = existing.reduce((sum, item) => sum + item.size, 0) + file.size
  if (total > MAX_CHAT_PROMPT_BYTES) {
    return `All attachments on one prompt are limited to ${formatBytes(MAX_CHAT_PROMPT_BYTES)}.`
  }

  if (isImageFile(file)) {
    if (!capabilities!.image) return 'This agent does not accept images.'
    if (!isSupportedImageType(file.type)) {
      return `${file.name} is not a PNG, JPEG, WebP, or GIF image.`
    }
    return null
  }

  if (!capabilities!.embeddedContext) {
    return 'This agent does not accept file attachments.'
  }

  return null
}

/** Short, human, and never more precise than a phone screen deserves. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(bytes < 10 * 1024 * 1024 ? 1 : 0)} MB`
}

/** The type shown under a filename: the subtype alone is what a person reads. */
export function describeType(mimeType: string, name: string): string {
  const type = mimeType.split(';')[0].trim()
  if (type.length > 0 && type !== 'application/octet-stream') {
    const slash = type.indexOf('/')
    return slash < 0 ? type : type.slice(slash + 1).toUpperCase()
  }

  const dot = name.lastIndexOf('.')
  return dot >= 0 && dot < name.length - 1 ? name.slice(dot + 1).toUpperCase() : 'File'
}
