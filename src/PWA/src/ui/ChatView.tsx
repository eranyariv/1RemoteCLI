import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'

import type { ChatEvent, MachineInfo, SessionInfo } from '../protocol/wire'
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
  const bottom = useRef<HTMLDivElement>(null)

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

  const send = async (event: FormEvent) => {
    event.preventDefault()
    const text = draft.trim()
    if (!text || sending || !connected) return

    setSending(true)
    const error = await client.sendChatMessage(session.sessionId, text)
    if (!error) setDraft('')
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
        className="flex gap-2 border-t border-slate-800 px-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] pt-3"
      >
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
          maxLength={20_000}
          placeholder="Message agent"
          aria-label="Message agent"
          className="min-h-12 min-w-0 flex-1 resize-none rounded-xl border border-slate-700 bg-slate-900 px-3 py-2 text-[16px] outline-none placeholder:text-slate-600 focus:border-sky-500"
        />
        <button
          type="submit"
          disabled={!connected || sending || draft.trim().length === 0}
          className="min-h-12 self-end rounded-xl bg-sky-600 px-4 text-sm font-semibold disabled:opacity-40"
        >
          {sending ? 'Sending…' : 'Send'}
        </button>
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
        <AcpContentBlocks blocks={item.content} includeText={false} />
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
