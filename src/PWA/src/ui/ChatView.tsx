import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'

import type { ChatEvent, MachineInfo, SessionInfo } from '../protocol/wire'
import type { RelayClient } from '../relay/client'
import { sessionLabel } from '../relay/machines'

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

  const pending = useMemo(
    () => events.some((event) => event.kind === 'Permission' && event.status === 'pending'),
    [events],
  )

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
    <section className="fixed inset-0 z-20 flex flex-col bg-slate-950 text-slate-100">
      <header className="flex items-center gap-3 border-b border-slate-800 px-3 pb-3 pt-[max(0.75rem,env(safe-area-inset-top))]">
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
        <span className={`text-xs ${connected ? 'text-emerald-400' : 'text-amber-400'}`}>
          {connected ? (pending ? 'approval needed' : 'connected') : 'reconnecting'}
        </span>
      </header>

      <div className="flex-1 space-y-3 overflow-y-auto px-4 py-4" aria-live="polite">
        {loadError ? (
          <p className="py-8 text-center text-sm text-rose-300">{loadError}</p>
        ) : !loaded ? (
          <p className="py-8 text-center text-sm text-slate-500">Loading the transcript…</p>
        ) : events.length === 0 ? (
          <p className="py-8 text-center text-sm text-slate-500">No messages yet.</p>
        ) : null}

        {events.map((item) => (
          <TranscriptItem
            key={item.eventId}
            item={item}
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
  onPermission,
}: {
  item: ChatEvent
  onPermission(requestId: string, optionId: string): Promise<unknown>
}) {
  if (item.kind === 'UserMessage' || item.kind === 'AgentMessage') {
    const user = item.kind === 'UserMessage'
    return (
      <article
        className={`max-w-[92%] whitespace-pre-wrap rounded-xl px-3 py-2.5 text-sm leading-6 ${
          user ? 'ml-auto bg-sky-600/25 text-sky-50' : 'bg-slate-900 text-slate-200'
        }`}
      >
        {item.text}
      </article>
    )
  }

  if (item.kind === 'Permission') {
    const pending = item.status === 'pending' && item.permissionRequestId
    return (
      <article className="rounded-xl border border-amber-400/35 bg-amber-400/10 p-3">
        <p className="text-sm font-semibold text-amber-200">{item.title ?? 'Approval required'}</p>
        <p className="mt-1 text-xs text-amber-100/70">
          {pending ? 'The agent is waiting for your decision.' : `Answered: ${item.status ?? 'done'}`}
        </p>
        {pending ? (
          <div className="mt-3 flex flex-wrap gap-2">
            {item.options.map((option) => (
              <button
                key={option.optionId}
                type="button"
                onClick={() => void onPermission(item.permissionRequestId!, option.optionId)}
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

  return (
    <article className="rounded-lg border border-slate-800 bg-slate-900/60 px-3 py-2 text-xs">
      <div className="flex items-center gap-2">
        <span className="font-medium text-slate-300">{item.title ?? 'Tool call'}</span>
        {item.status ? (
          <span className="rounded bg-slate-800 px-1.5 py-0.5 text-[10px] text-slate-400">
            {item.status.replaceAll('_', ' ')}
          </span>
        ) : null}
      </div>
      {item.text ? <p className="mt-1 whitespace-pre-wrap text-slate-500">{item.text}</p> : null}
    </article>
  )
}
