import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'

import {
  attachmentsAllowed,
  describeType,
  formatBytes,
  isImageFile,
  rejectAttachment,
  CHAT_IMAGE_ACCEPT,
  MAX_CHAT_ATTACHMENT_COUNT,
  MAX_CHAT_PROMPT_TEXT_CHARS,
  type ChatAttachmentDraft,
} from '../chat/attachment'
import { describeError } from '../protocol/errors'
import type { ChatContentBlock, ChatEvent, MachineInfo, SessionInfo } from '../protocol/wire'
import type { RelayClient } from '../relay/client'
import { sessionLabel } from '../relay/machines'
import { AcpContentBlocks, AcpEventView, type AcpDetailLevel } from './AcpEventView'
import { MarkdownText } from './MarkdownText'

type DetailLevel = AcpDetailLevel

const CancelElicitationOption = '__1remote_cancel__'
const DeclineElicitationOption = '__1remote_decline__'

function isElicitation(item: ChatEvent): boolean {
  return (
    item.kind === 'Permission' &&
    item.options.some((option) => option.kind === 'select')
  )
}

/**
 * The metadata-only record the agent echoes for an attachment the user just sent.
 *
 * Recognised by its synthetic `attachment:` URI, which is what the daemon puts on a
 * browser-selected file precisely because it is not a path to anything. The bytes
 * are never in the transcript, so a summary is all there is to draw.
 */
function isAttachmentSummary(block: ChatContentBlock): boolean {
  return (
    block.type === 'resource_link' &&
    (block.uri?.startsWith('attachment://') ?? false) &&
    block.name !== null
  )
}

export function ChatView({
  client,
  connected,
  machine,
  session,
  onClose,
}: {
  client: RelayClient
  connected: boolean
  machine: MachineInfo
  session: SessionInfo
  onClose(): void
}) {
  const [events, setEvents] = useState<ChatEvent[]>([])
  const [loaded, setLoaded] = useState(false)
  const [draft, setDraft] = useState('')
  const [sending, setSending] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [detailLevel, setDetailLevel] = useState<DetailLevel>('summary')
  const [attachments, setAttachments] = useState<ChatAttachmentDraft[]>([])
  const [composerError, setComposerError] = useState<string | null>(null)
  const bottom = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement | null>(null)
  const imageInput = useRef<HTMLInputElement | null>(null)
  const cameraInput = useRef<HTMLInputElement | null>(null)

  /**
   * Uploads in flight, and the object URLs their previews hold.
   *
   * Kept in refs rather than state because both have to be reachable from an unmount
   * that is happening for reasons the render has nothing to say about: a preview that
   * is not revoked leaks the whole file, and staged bytes nobody cancels sit on the
   * machine until a sweeper notices.
   */
  const uploads = useRef(new Map<string, AbortController>())
  const previews = useRef(new Map<string, string>())
  const attachmentsRef = useRef<ChatAttachmentDraft[]>([])

  const capabilities = session.chatCapabilities
  const canAttach = attachmentsAllowed(capabilities)

  useEffect(() => {
    const off = client.on('chatTranscript', (transcript) => {
      if (transcript.sessionId !== session.sessionId) return

      if (transcript.kind === 'Snapshot') setLoaded(true)
      setEvents((current) => {
        if (transcript.kind === 'Snapshot') return transcript.events

        const next = [...current]
        for (const changed of transcript.events) {
          const index = next.findIndex((item) => item.eventId === changed.eventId)
          if (index >= 0) next[index] = changed
          else next.push(changed)
        }
        return next
      })
    })

    return () => {
      off()
      void client.detach(session.sessionId)
    }
  }, [client, machine.machineId, session.sessionId])

  useEffect(() => {
    if (!connected) return

    let active = true
    setLoaded(false)
    setLoadError(null)
    void client.attach(machine.machineId, session.sessionId, 0, 0).then((error) => {
      if (active && error) setLoadError(error.message)
    })

    return () => {
      active = false
    }
  }, [client, connected, machine.machineId, session.sessionId])

  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' })
  }, [events])

  const pendingInput = useMemo(
    () => events.find((event) => event.kind === 'Permission' && event.status === 'pending'),
    [events],
  )
  const visibleEvents = useMemo(() => {
    const elicitationTools = new Set(
      events.flatMap((event) =>
        isElicitation(event) && event.toolKind ? [event.toolKind] : [],
      ),
    )

    return events.filter((event) => {
      if (event.kind !== 'ToolCall') return true
      if (elicitationTools.has(event.eventId)) return false
      return detailLevel !== 'compact' || event.status === 'pending' || event.status === 'in_progress'
    })
  }, [detailLevel, events])

  useEffect(() => {
    attachmentsRef.current = attachments
  }, [attachments])

  const forget = useCallback((attachmentId: string) => {
    uploads.current.get(attachmentId)?.abort()
    uploads.current.delete(attachmentId)

    const preview = previews.current.get(attachmentId)
    if (preview) {
      URL.revokeObjectURL(preview)
      previews.current.delete(attachmentId)
    }
  }, [])

  // Everything still staged when the view goes away is cancelled and revoked. A
  // composer the user walked away from must not leave their photo on a machine —
  // including an attachment that finished uploading and was never sent.
  useEffect(
    () => () => {
      for (const controller of uploads.current.values()) controller.abort()
      uploads.current.clear()

      for (const item of attachmentsRef.current) {
        void client.cancelChatAttachment(session.sessionId, item.attachmentId)
      }

      for (const preview of previews.current.values()) URL.revokeObjectURL(preview)
      previews.current.clear()
    },
    [client, session.sessionId],
  )

  const attach = useCallback(
    (files: FileList | null) => {
      if (!files || files.length === 0) return
      if (sending) {
        setComposerError('Wait for the current message to be accepted before attaching another file.')
        return
      }

      // Validated against a local running list rather than against state, because a
      // multi-file pick is one synchronous loop and React has not re-rendered between
      // its iterations — checking state would let four 4 MB files past a 10 MB
      // aggregate limit.
      let selected = [...attachmentsRef.current]
      let batchError: string | null = null

      for (const file of Array.from(files)) {
        const rejection = rejectAttachment(file, capabilities, selected)
        if (rejection) {
          batchError ??= rejection
          continue
        }

        const attachmentId = crypto.randomUUID()
        const previewUrl = isImageFile(file) ? URL.createObjectURL(file) : null
        if (previewUrl) previews.current.set(attachmentId, previewUrl)

        const item: ChatAttachmentDraft = {
          attachmentId,
          name: file.name,
          mimeType: file.type,
          size: file.size,
          status: 'uploading',
          confirmedBytes: 0,
          previewUrl,
          error: null,
        }
        selected = [...selected, item]
        setAttachments((current) => [...current, item])

        const controller = new AbortController()
        uploads.current.set(attachmentId, controller)

        // Staged as soon as it is chosen, so the wait happens while the user is
        // still typing rather than after they press Send.
        void client
          .uploadChatAttachment(
            session.sessionId,
            attachmentId,
            file,
            (progress) =>
              setAttachments((current) =>
                current.map((existing) =>
                  existing.attachmentId === attachmentId
                    ? { ...existing, confirmedBytes: progress.confirmedBytes }
                    : existing,
                ),
              ),
            controller.signal,
          )
          .then((outcome) => {
            uploads.current.delete(attachmentId)

            if (outcome.cancelled) {
              setAttachments((current) =>
                current.filter((existing) => existing.attachmentId !== attachmentId),
              )
              return
            }

            setAttachments((current) =>
              current.map((existing) =>
                existing.attachmentId === attachmentId
                  ? {
                      ...existing,
                      status: outcome.ready ? 'ready' : 'failed',
                      confirmedBytes: outcome.ready ? existing.size : existing.confirmedBytes,
                      error: outcome.error
                        ? describeError(outcome.error.code, outcome.error.message)
                        : null,
                    }
                  : existing,
              ),
            )
          })
      }

      // A multi-file picker can contain both accepted and rejected files. Report the
      // rejected one after processing the whole batch so a later valid file cannot
      // erase the explanation.
      setComposerError(batchError)
    },
    [capabilities, client, sending, session.sessionId],
  )

  const remove = useCallback(
    (attachmentId: string) => {
      forget(attachmentId)
      setAttachments((current) => current.filter((item) => item.attachmentId !== attachmentId))
      void client.cancelChatAttachment(session.sessionId, attachmentId)
    },
    [client, forget, session.sessionId],
  )

  const uploading = attachments.some((item) => item.status === 'uploading')
  const failed = attachments.some((item) => item.status === 'failed')
  const ready = attachments.filter((item) => item.status === 'ready')

  // A failure blocks Send rather than being quietly dropped from the prompt: the
  // user chose that file, and sending without it while it is still sitting in the
  // composer would look like it went.
  const canSend =
    connected && !sending && !uploading && !failed && (draft.trim().length > 0 || ready.length > 0)

  const send = async (event: FormEvent) => {
    event.preventDefault()
    const text = draft.trim()
    if (!canSend) return

    setSending(true)
    setComposerError(null)
    const submittedDraft = draft
    const submittedAttachments = [...ready]
    const submittedIds = new Set(submittedAttachments.map((item) => item.attachmentId))

    // Text with nothing attached still travels as `SendChatMessage`, which is the
    // one path an agent that predates attachments understands.
    const error =
      submittedAttachments.length === 0
        ? await client.sendChatMessage(session.sessionId, text)
        : await client.sendChatPrompt(
            session.sessionId,
            text,
            submittedAttachments.map((item) => item.attachmentId),
          )

    if (!error) {
      // Do not erase a next message typed while the acknowledgement was in flight,
      // or an attachment selected from a picker that was already open.
      setDraft((current) => (current === submittedDraft ? '' : current))
      for (const item of submittedAttachments) forget(item.attachmentId)
      setAttachments((current) =>
        current.filter((item) => !submittedIds.has(item.attachmentId)),
      )
    } else {
      // The draft and the selection are kept: the machine rejected the prompt
      // before consuming anything, so there is something here worth correcting.
      setComposerError(describeError(error.code, error.message))
    }

    setSending(false)
  }

  return (
    <section className="fixed inset-0 z-20 flex min-w-0 max-w-full flex-col overflow-x-hidden bg-slate-950 text-slate-100">
      <header className="flex min-w-0 max-w-full items-center gap-3 border-b border-slate-800 px-3 pb-3 pt-[max(0.75rem,env(safe-area-inset-top))]">
        <button
          type="button"
          onClick={onClose}
          className="min-h-10 rounded-lg px-3 text-sm text-slate-300 active:bg-slate-800"
        >
          ‹ Back
        </button>
        <div className="min-w-0 flex-1">
          <h2 className="truncate text-sm font-semibold">{sessionLabel(session)}</h2>
          <p className="truncate text-xs text-slate-500">
            {machine.displayName} · {session.program}
          </p>
        </div>
        <span
          className={`shrink-0 text-xs ${connected ? 'text-emerald-400' : 'text-amber-400'}`}
        >
          {connected
            ? pendingInput
              ? isElicitation(pendingInput)
                ? 'input needed'
                : 'approval needed'
              : 'connected'
            : 'reconnecting'}
        </span>
      </header>

      <div
        className="flex items-center justify-between gap-3 border-b border-slate-800 px-4 py-2"
        role="group"
        aria-label="Transcript detail level"
      >
        <span className="text-xs font-medium text-slate-400">Details</span>
        <div className="flex rounded-lg bg-slate-900 p-0.5">
          {(['compact', 'summary', 'full'] as const).map((level) => (
            <button
              key={level}
              type="button"
              aria-pressed={detailLevel === level}
              onClick={() => setDetailLevel(level)}
              className={`min-h-8 rounded-md px-2.5 text-xs capitalize ${
                detailLevel === level ? 'bg-slate-700 text-white' : 'text-slate-400'
              }`}
            >
              {level[0].toUpperCase() + level.slice(1)}
            </button>
          ))}
        </div>
      </div>

      <div
        className="min-w-0 max-w-full flex-1 space-y-3 overflow-x-hidden overflow-y-auto px-4 py-4"
        aria-live="polite"
      >
        {loadError ? (
          <p className="py-8 text-center text-sm text-rose-300">{loadError}</p>
        ) : !loaded ? (
          <p className="py-8 text-center text-sm text-slate-500">Loading the transcript…</p>
        ) : events.length === 0 ? (
          <p className="py-8 text-center text-sm text-slate-500">No messages yet.</p>
        ) : null}

        {visibleEvents.map((item) => (
          <TranscriptItem
            key={item.eventId}
            item={item}
            detailLevel={detailLevel}
            onPermission={(requestId, optionId) =>
              client.respondChatPermission(session.sessionId, requestId, optionId)
            }
          />
        ))}
        <div ref={bottom} />
      </div>

      <form
        onSubmit={(event) => void send(event)}
        className="border-t border-slate-800 px-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] pt-3"
      >
        {canAttach ? (
          <>
            <input
              ref={fileInput}
              type="file"
              multiple
              className="hidden"
              data-testid="chat-file-input"
              accept={capabilities?.embeddedContext ? undefined : CHAT_IMAGE_ACCEPT}
              onChange={(event) => {
                attach(event.currentTarget.files)
                event.currentTarget.value = ''
              }}
            />
            <input
              ref={imageInput}
              type="file"
              multiple
              accept={CHAT_IMAGE_ACCEPT}
              className="hidden"
              data-testid="chat-image-input"
              onChange={(event) => {
                attach(event.currentTarget.files)
                event.currentTarget.value = ''
              }}
            />
            {/*
              A separate input, not a mode on the one above: `capture` is what makes a
              phone open the camera directly instead of the photo library, and putting
              it on the shared input would take the library away from everyone.
            */}
            <input
              ref={cameraInput}
              type="file"
              accept={CHAT_IMAGE_ACCEPT}
              capture="environment"
              className="hidden"
              data-testid="chat-camera-input"
              onChange={(event) => {
                attach(event.currentTarget.files)
                event.currentTarget.value = ''
              }}
            />
          </>
        ) : null}

        {composerError ? (
          <p
            role="alert"
            className="mb-2 rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-xs text-rose-200"
          >
            {composerError}
          </p>
        ) : null}

        {attachments.length > 0 ? (
          <ul className="mb-2 grid gap-2" aria-label="Attachments">
            {attachments.map((item) => (
              <li
                key={item.attachmentId}
                className="flex min-w-0 items-center gap-3 rounded-xl border border-slate-800 bg-slate-900 px-3 py-2"
              >
                {item.previewUrl ? (
                  <img
                    src={item.previewUrl}
                    alt=""
                    className="size-10 shrink-0 rounded-lg object-cover"
                  />
                ) : (
                  <span
                    aria-hidden
                    className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-slate-800 text-[10px] font-semibold text-slate-400"
                  >
                    {describeType(item.mimeType, item.name).slice(0, 4)}
                  </span>
                )}
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-xs text-slate-200">{item.name}</span>
                  <span className="block truncate text-[11px] text-slate-500">
                    {describeType(item.mimeType, item.name)} · {formatBytes(item.size)}
                    {item.status === 'uploading'
                      ? ` · ${
                          item.size === 0
                            ? 100
                            : Math.round((item.confirmedBytes / item.size) * 100)
                        }%`
                      : item.status === 'ready'
                        ? ' · ready'
                        : ''}
                  </span>
                  {item.status === 'failed' && item.error ? (
                    <span className="block text-[11px] text-rose-300">{item.error}</span>
                  ) : null}
                </span>
                <button
                  type="button"
                  onClick={() => remove(item.attachmentId)}
                  aria-label={`Remove ${item.name}`}
                  className="min-h-10 shrink-0 rounded-lg px-2 text-sm text-slate-400 active:bg-slate-800"
                >
                  ✕
                </button>
              </li>
            ))}
          </ul>
        ) : null}

        <div className="flex gap-2">
          {canAttach ? (
            <div className="flex shrink-0 flex-col justify-end gap-1">
              <button
                type="button"
                onClick={() =>
                  (capabilities?.embeddedContext ? fileInput : imageInput).current?.click()
                }
                disabled={
                  !connected || sending || attachments.length >= MAX_CHAT_ATTACHMENT_COUNT
                }
                aria-label={capabilities?.embeddedContext ? 'Attach a file' : 'Attach a photo'}
                className="min-h-12 rounded-xl border border-slate-700 px-3 text-sm text-slate-300 active:bg-slate-800 disabled:opacity-40"
              >
                📎
              </button>
              {capabilities?.image ? (
                <button
                  type="button"
                  onClick={() => cameraInput.current?.click()}
                  disabled={
                    !connected || sending || attachments.length >= MAX_CHAT_ATTACHMENT_COUNT
                  }
                  aria-label="Take a photo"
                  className="min-h-12 rounded-xl border border-slate-700 px-3 text-sm text-slate-300 active:bg-slate-800 disabled:opacity-40"
                >
                  📷
                </button>
              ) : null}
            </div>
          ) : null}

          <textarea
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault()
                event.currentTarget.form?.requestSubmit()
              }
            }}
            rows={2}
            maxLength={MAX_CHAT_PROMPT_TEXT_CHARS}
            placeholder="Message agent"
            aria-label="Message agent"
            className="min-h-12 min-w-0 flex-1 resize-none rounded-xl border border-slate-700 bg-slate-900 px-3 py-2 text-[16px] outline-none placeholder:text-slate-600 focus:border-sky-500"
          />
          <button
            type="submit"
            disabled={!canSend}
            className="min-h-12 self-end rounded-xl bg-sky-600 px-4 text-sm font-semibold disabled:opacity-40"
          >
            {sending ? 'Sending…' : uploading ? 'Attaching…' : 'Send'}
          </button>
        </div>
      </form>
    </section>
  )
}

function TranscriptItem({
  item,
  detailLevel,
  onPermission,
}: {
  item: ChatEvent
  detailLevel: DetailLevel
  onPermission(requestId: string, optionId: string): Promise<unknown>
}) {
  const [answer, setAnswer] = useState('')
  const [responding, setResponding] = useState(false)

  const respond = async (optionId: string) => {
    if (!item.permissionRequestId || responding) return
    setResponding(true)
    try {
      await onPermission(item.permissionRequestId, optionId)
    } finally {
      setResponding(false)
    }
  }

  if (item.kind === 'UserMessage' || item.kind === 'AgentMessage') {
    const user = item.kind === 'UserMessage'
    const attached = user ? item.content.filter(isAttachmentSummary) : []
    const rest = user ? item.content.filter((block) => !isAttachmentSummary(block)) : item.content

    return (
      <article
        className={`min-w-0 rounded-2xl px-3 py-2.5 text-sm leading-6 ${
          user
            ? 'ml-auto max-w-[92%] border border-slate-700 bg-slate-800 text-slate-100 sm:max-w-[80%]'
            : 'max-w-full bg-slate-900 text-slate-200'
        }`}
      >
        {item.text && user ? (
          <p className="whitespace-pre-wrap break-words [overflow-wrap:anywhere]">{item.text}</p>
        ) : null}
        {item.text && !user ? <MarkdownText>{item.text}</MarkdownText> : null}
        {attached.length > 0 ? (
          <ul className="mt-2 grid gap-1.5" aria-label="Sent attachments">
            {attached.map((block, index) => (
              <li
                key={`${block.uri ?? block.name ?? 'attachment'}-${index}`}
                className="flex min-w-0 items-center gap-2 rounded-lg bg-slate-900/70 px-2 py-1.5"
              >
                <span
                  aria-hidden
                  className="flex size-6 shrink-0 items-center justify-center rounded bg-slate-800 text-[9px] font-semibold text-slate-400"
                >
                  {describeType(block.mimeType ?? '', block.name ?? '').slice(0, 4)}
                </span>
                <span className="min-w-0 flex-1 truncate text-xs text-slate-200">
                  {block.name ?? 'Attachment'}
                </span>
                <span className="shrink-0 text-[11px] text-slate-500">
                  {formatBytes(block.size ?? 0)}
                </span>
              </li>
            ))}
          </ul>
        ) : null}
        <AcpContentBlocks blocks={rest} includeText={false} />
      </article>
    )
  }

  if (isElicitation(item)) {
    const pending = item.status === 'pending' && item.permissionRequestId
    const selected = item.options.find((option) => option.optionId === item.status)?.name

    return (
      <article className="min-w-0 max-w-full overflow-hidden rounded-xl border border-sky-400/40 bg-sky-400/10 p-3">
        <p className="break-words text-sm font-semibold text-sky-100 [overflow-wrap:anywhere]">
          {item.title ?? 'Your input is needed'}
        </p>
        <p className="mt-1 whitespace-pre-wrap break-words text-sm text-slate-200 [overflow-wrap:anywhere]">
          {item.text}
        </p>
        {pending && item.options.length > 0 ? (
          <div className="mt-3 grid gap-2" aria-label={item.title ?? 'Choose an answer'}>
            {item.options.map((option) => (
              <button
                key={option.optionId}
                type="button"
                onClick={() => setAnswer(option.optionId)}
                disabled={responding}
                aria-pressed={answer === option.optionId}
                className={`min-h-11 break-words rounded-lg border px-3 py-2 text-left text-sm font-medium [overflow-wrap:anywhere] disabled:opacity-50 ${
                  answer === option.optionId
                    ? 'border-sky-300 bg-sky-600 text-white'
                    : 'border-slate-700 bg-slate-900 text-slate-200'
                }`}
              >
                {option.name}
              </button>
            ))}
          </div>
        ) : null}
        {pending ? (
          <div className="mt-3 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => void respond(answer)}
              disabled={responding || answer.length === 0}
              className="min-h-10 rounded-lg bg-sky-600 px-3 text-sm font-medium text-white disabled:opacity-50"
            >
              Submit answer
            </button>
            <button
              type="button"
              onClick={() => void respond(DeclineElicitationOption)}
              disabled={responding}
              className="min-h-10 rounded-lg px-2 text-sm text-slate-300 active:bg-slate-800 disabled:opacity-50"
            >
              Decline
            </button>
            <button
              type="button"
              onClick={() => void respond(CancelElicitationOption)}
              disabled={responding}
              className="min-h-10 rounded-lg px-2 text-sm text-slate-400 active:bg-slate-800 disabled:opacity-50"
            >
              Cancel
            </button>
          </div>
        ) : (
          <p className="mt-2 text-xs text-slate-400">
            {item.status === 'cancelled'
              ? 'Cancelled'
              : item.status === 'declined'
                ? 'Declined'
                : `Answered: ${selected ?? item.status ?? 'done'}`}
          </p>
        )}
      </article>
    )
  }

  if (item.kind === 'Permission') {
    const pending = item.status === 'pending' && item.permissionRequestId
    return (
      <article className="min-w-0 max-w-full overflow-hidden rounded-xl border border-amber-400/35 bg-amber-400/10 p-3">
        <p className="text-sm font-semibold text-amber-200">{item.title ?? 'Approval required'}</p>
        <p className="mt-1 text-xs text-amber-100/70">
          {pending ? 'The agent is waiting for your decision.' : `Answered: ${item.status ?? 'done'}`}
        </p>
        {pending ? (
          <div className="mt-3 flex min-w-0 flex-wrap gap-2">
            {item.options.map((option) => (
              <button
                key={option.optionId}
                type="button"
                onClick={() => void respond(option.optionId)}
                disabled={responding}
                className={`min-h-10 rounded-lg px-3 text-sm font-medium ${
                  option.kind.startsWith('allow')
                    ? 'bg-emerald-600 text-white'
                    : 'border border-rose-400/50 text-rose-200'
                }`}
              >
                {option.name}
              </button>
            ))}
          </div>
        ) : null}
      </article>
    )
  }

  return <AcpEventView item={item} detailLevel={detailLevel} />
}
