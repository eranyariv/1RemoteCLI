import { useEffect, useRef, useState, type ReactNode } from 'react'

import type { ChatContentBlock, ChatEvent, ChatPlanEntry } from '../protocol/wire'

export type AcpDetailLevel = 'compact' | 'summary' | 'full'

export function AcpEventView({
  item,
  detailLevel,
}: {
  item: ChatEvent
  detailLevel: AcpDetailLevel
}) {
  if (item.kind === 'AgentThought') {
    return <ThoughtView item={item} detailLevel={detailLevel} />
  }

  if (item.kind === 'Plan') {
    return <PlanView item={item} detailLevel={detailLevel} />
  }

  return <ToolCallView item={item} detailLevel={detailLevel} />
}

export function AcpContentBlocks({
  blocks,
  includeText = true,
}: {
  blocks: ChatContentBlock[]
  includeText?: boolean
}) {
  const visible = includeText ? blocks : blocks.filter((block) => block.type !== 'text')
  if (visible.length === 0) return null

  return (
    <div className="mt-2 space-y-2">
      {visible.map((block, index) => (
        <ContentBlock key={`${block.type}-${block.path ?? block.uri ?? index}`} block={block} />
      ))}
    </div>
  )
}

function ThoughtView({
  item,
  detailLevel,
}: {
  item: ChatEvent
  detailLevel: AcpDetailLevel
}) {
  const [expanded, setExpanded] = useState(detailLevel === 'full')

  useEffect(() => {
    if (detailLevel === 'full') setExpanded(true)
  }, [detailLevel])

  return (
    <article className="min-w-0 max-w-full overflow-hidden rounded-xl border border-violet-400/20 bg-violet-400/5">
      <DisclosureHeader
        expanded={expanded}
        onToggle={() => setExpanded((value) => !value)}
        icon={<ThoughtIcon />}
        label="Thought"
        tone="text-violet-200"
      />
      {expanded ? (
        <div className="border-t border-violet-400/15 px-3 py-2.5">
          <p className="whitespace-pre-wrap break-words text-sm leading-6 text-slate-300 [overflow-wrap:anywhere]">
            {item.text}
          </p>
          <AcpContentBlocks blocks={item.content} includeText={false} />
        </div>
      ) : null}
    </article>
  )
}

function PlanView({
  item,
  detailLevel,
}: {
  item: ChatEvent
  detailLevel: AcpDetailLevel
}) {
  const [expanded, setExpanded] = useState(detailLevel !== 'compact')
  const [collapsedTasks, setCollapsedTasks] = useState<Set<string>>(() => new Set())
  const previousDetailLevel = useRef(detailLevel)
  const completed = item.planEntries.filter((entry) => entry.status === 'completed').length
  const failed = item.planEntries.filter((entry) => entry.status === 'failed').length
  const running = item.planEntries.some((entry) => entry.status === 'in_progress')
  const plan = planForest(item.planEntries)

  useEffect(() => {
    const levelChanged = previousDetailLevel.current !== detailLevel
    previousDetailLevel.current = detailLevel

    if (levelChanged) {
      setExpanded(detailLevel !== 'compact')
    } else if (running && detailLevel !== 'compact') {
      setExpanded(true)
    }
  }, [detailLevel, running])

  useEffect(() => {
    const current = new Set(item.planEntries.map((entry) => entry.taskId))
    setCollapsedTasks((collapsed) => {
      const retained = new Set([...collapsed].filter((taskId) => current.has(taskId)))
      return retained.size === collapsed.size ? collapsed : retained
    })
  }, [item.planEntries])

  if (item.planEntries.length === 0) return null

  return (
    <article className="min-w-0 max-w-full overflow-hidden rounded-xl border border-sky-400/20 bg-sky-400/5">
      <DisclosureHeader
        expanded={expanded}
        onToggle={() => setExpanded((value) => !value)}
        icon={<PlanIcon />}
        label="Plan"
        meta={`${completed}/${item.planEntries.length}${failed > 0 ? ` · ${failed} failed` : ''}`}
        tone="text-sky-200"
      />
      {expanded ? (
        <div className="border-t border-sky-400/15 px-3 py-3">
          <div className="mb-3 flex items-center gap-3">
            <div
              role="progressbar"
              aria-label="Plan progress"
              aria-valuemin={0}
              aria-valuemax={item.planEntries.length}
              aria-valuenow={completed}
              className="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-slate-800"
            >
              <div
                className="h-full rounded-full bg-emerald-400 transition-[width]"
                style={{ width: `${(completed / item.planEntries.length) * 100}%` }}
              />
            </div>
            <span className="shrink-0 text-[11px] text-slate-400">
              {completed} of {item.planEntries.length} complete
            </span>
          </div>
          <ol role="tree" aria-label="Plan tasks" className="space-y-1">
            {plan.map((node) => (
              <PlanTask
                key={node.entry.taskId}
                node={node}
                collapsedTasks={collapsedTasks}
                onToggle={(taskId) =>
                  setCollapsedTasks((collapsed) => {
                    const next = new Set(collapsed)
                    if (next.has(taskId)) next.delete(taskId)
                    else next.add(taskId)
                    return next
                  })
                }
              />
            ))}
          </ol>
        </div>
      ) : null}
    </article>
  )
}

interface PlanNode {
  entry: ChatPlanEntry
  children: PlanNode[]
}

function planForest(entries: ChatPlanEntry[]): PlanNode[] {
  const roots: PlanNode[] = []
  const seen = new Map<string, PlanNode>()
  const depthStack: PlanNode[] = []

  for (const entry of entries) {
    const node: PlanNode = { entry, children: [] }
    const explicitParent = entry.parentTaskId ? seen.get(entry.parentTaskId) : undefined
    const depthParent = entry.depth > 0 ? depthStack[entry.depth - 1] : undefined
    const parent = explicitParent ?? depthParent

    if (parent) parent.children.push(node)
    else roots.push(node)

    seen.set(entry.taskId, node)
    depthStack[entry.depth] = node
    depthStack.length = entry.depth + 1
  }

  return roots
}

function PlanTask({
  node,
  collapsedTasks,
  onToggle,
}: {
  node: PlanNode
  collapsedTasks: Set<string>
  onToggle(taskId: string): void
}) {
  const { entry, children } = node
  const collapsed = children.length > 0 && collapsedTasks.has(entry.taskId)
  const active = entry.status === 'in_progress'
  const textTone =
    entry.status === 'completed'
      ? 'text-slate-500 line-through'
      : entry.status === 'failed'
        ? 'text-rose-200'
        : active
          ? 'font-semibold text-white'
          : 'text-slate-300'

  return (
    <li
      role="treeitem"
      aria-level={entry.depth + 1}
      aria-current={active ? 'step' : undefined}
      aria-expanded={children.length > 0 ? !collapsed : undefined}
      className="min-w-0"
    >
      <div
        className={`relative flex min-w-0 items-start gap-2 rounded-lg px-2 py-1.5 text-sm ${
          entry.depth > 0
            ? 'before:absolute before:-left-2 before:top-4 before:w-2 before:border-t before:border-slate-700/80'
            : ''
        } ${
          active ? 'bg-sky-400/10 ring-1 ring-inset ring-sky-400/25' : ''
        }`}
      >
        <PlanStatus status={entry.status} />
        <span className={`min-w-0 flex-1 break-words leading-5 [overflow-wrap:anywhere] ${textTone}`}>
          {entry.content}
        </span>
        {entry.priority !== 'medium' ? (
          <span
            className={`mt-0.5 shrink-0 rounded px-1.5 py-0.5 text-[10px] uppercase tracking-wide ${
              entry.priority === 'high'
                ? 'bg-amber-400/10 text-amber-300'
                : 'bg-slate-800 text-slate-500'
            }`}
          >
            {entry.priority}
          </span>
        ) : null}
        {children.length > 0 ? (
          <button
            type="button"
            aria-label={`${collapsed ? 'Expand' : 'Collapse'} ${entry.content}`}
            onClick={() => onToggle(entry.taskId)}
            className="-mr-1 flex size-7 shrink-0 items-center justify-center rounded-md text-slate-400 active:bg-slate-700"
          >
            <Chevron expanded={!collapsed} />
          </button>
        ) : null}
      </div>
      {!collapsed && children.length > 0 ? (
        <ol
          role="group"
          className="ml-4 space-y-1 border-l border-slate-700/80 pl-2 before:block"
        >
          {children.map((child) => (
            <PlanTask
              key={child.entry.taskId}
              node={child}
              collapsedTasks={collapsedTasks}
              onToggle={onToggle}
            />
          ))}
        </ol>
      ) : null}
    </li>
  )
}

function ToolCallView({
  item,
  detailLevel,
}: {
  item: ChatEvent
  detailLevel: AcpDetailLevel
}) {
  const active = item.status === 'pending' || item.status === 'in_progress'
  const hasDetail =
    item.content.length > 0 ||
    item.locations.length > 0 ||
    item.rawInputJson !== null ||
    item.rawOutputJson !== null ||
    item.text.length > 0
  const [expanded, setExpanded] = useState(
    detailLevel === 'full' || (active && detailLevel !== 'compact'),
  )
  const previousDetailLevel = useRef(detailLevel)

  useEffect(() => {
    const levelChanged = previousDetailLevel.current !== detailLevel
    previousDetailLevel.current = detailLevel

    if (levelChanged) {
      setExpanded(detailLevel === 'full' || (active && detailLevel !== 'compact'))
    } else if ((active || detailLevel === 'full') && detailLevel !== 'compact') {
      setExpanded(true)
    }
  }, [active, detailLevel])

  return (
    <article className="min-w-0 max-w-full overflow-hidden rounded-xl border border-slate-800 bg-slate-900/70">
      <button
        type="button"
        aria-expanded={expanded}
        disabled={!hasDetail}
        onClick={() => setExpanded((value) => !value)}
        className="flex min-h-11 w-full min-w-0 items-center gap-2 px-3 py-2 text-left disabled:cursor-default"
      >
        <ToolKindIcon kind={item.toolKind} />
        <span className="min-w-0 flex-1 truncate text-sm font-medium text-slate-200">
          {item.title ?? 'Tool call'}
        </span>
        {item.status ? <ToolStatus status={item.status} /> : null}
        {hasDetail ? <Chevron expanded={expanded} /> : null}
      </button>
      {expanded && hasDetail ? (
        <div className="border-t border-slate-800 px-3 py-3">
          {item.locations.length > 0 ? (
            <div className="mb-3 flex min-w-0 flex-wrap gap-1.5">
              {item.locations.map((location, index) => (
                <span
                  key={`${location.path}:${location.line ?? ''}-${index}`}
                  title={`${location.path}${location.line === null ? '' : `:${location.line}`}`}
                  className="inline-flex min-w-0 max-w-full items-center gap-1 rounded-md bg-slate-800 px-2 py-1 font-mono text-[11px] text-sky-200"
                >
                  <FileIcon />
                  <span className="truncate">
                    {basename(location.path)}
                    {location.line === null ? '' : `:${location.line}`}
                  </span>
                </span>
              ))}
            </div>
          ) : null}
          <AcpContentBlocks blocks={item.content} />
          {item.content.length === 0 && item.text ? (
            <pre className="whitespace-pre-wrap break-words rounded-lg bg-slate-950/70 p-3 font-mono text-xs leading-5 text-slate-300 [overflow-wrap:anywhere]">
              {item.text}
            </pre>
          ) : null}
          <JsonDetails label="Input" value={item.rawInputJson} />
          <JsonDetails label="Output" value={item.rawOutputJson} />
        </div>
      ) : null}
    </article>
  )
}

function ContentBlock({ block }: { block: ChatContentBlock }) {
  if (block.type === 'text' && block.text) {
    return (
      <pre className="whitespace-pre-wrap break-words rounded-lg bg-slate-950/70 p-3 font-mono text-xs leading-5 text-slate-300 [overflow-wrap:anywhere]">
        {block.text}
      </pre>
    )
  }

  if (block.type === 'diff') {
    return (
      <section className="overflow-hidden rounded-lg border border-slate-800 bg-slate-950/70">
        <header className="flex items-center gap-2 border-b border-slate-800 px-3 py-2 font-mono text-xs text-slate-300">
          <FileIcon />
          <span className="truncate">{block.path ?? 'Changed file'}</span>
        </header>
        <div className="grid min-w-0 gap-px bg-slate-800 sm:grid-cols-2">
          <DiffSide label="Before" text={block.oldText ?? ''} tone="rose" />
          <DiffSide label="After" text={block.newText ?? ''} tone="emerald" />
        </div>
      </section>
    )
  }

  if (block.type === 'image' && imageSource(block)) {
    return (
      <figure className="overflow-hidden rounded-lg border border-slate-800 bg-slate-950/70 p-2">
        <img
          src={imageSource(block)!}
          alt={block.title ?? block.name ?? 'Agent output'}
          className="max-h-[28rem] max-w-full rounded object-contain"
        />
      </figure>
    )
  }

  if (block.type === 'audio' && block.data && block.mimeType?.startsWith('audio/')) {
    return (
      <audio
        controls
        preload="metadata"
        src={`data:${block.mimeType};base64,${block.data}`}
        className="w-full"
      />
    )
  }

  if (block.type === 'terminal') {
    return (
      <div className="flex items-center gap-2 rounded-lg bg-slate-950/70 px-3 py-2 font-mono text-xs text-slate-300">
        <TerminalIcon />
        Terminal output
        {block.terminalId ? <span className="truncate text-slate-500">· {block.terminalId}</span> : null}
      </div>
    )
  }

  if (block.type === 'resource' || block.type === 'resource_link') {
    const label = block.title ?? block.name ?? block.uri ?? 'Resource'
    return (
      <section className="rounded-lg border border-slate-800 bg-slate-950/70 p-3">
        {safeWebUrl(block.uri) ? (
          <a
            href={block.uri!}
            target="_blank"
            rel="noreferrer"
            className="break-all text-sm font-medium text-sky-300 underline decoration-sky-500/40"
          >
            {label}
          </a>
        ) : (
          <p className="break-all text-sm font-medium text-slate-300">{label}</p>
        )}
        {block.description ? <p className="mt-1 text-xs text-slate-500">{block.description}</p> : null}
        {block.text ? (
          <pre className="mt-2 whitespace-pre-wrap break-words font-mono text-xs text-slate-400 [overflow-wrap:anywhere]">
            {block.text}
          </pre>
        ) : null}
      </section>
    )
  }

  if (block.rawJson) {
    return (
      <pre className="whitespace-pre-wrap break-words rounded-lg bg-slate-950/70 p-3 font-mono text-xs text-slate-400 [overflow-wrap:anywhere]">
        {prettyJson(block.rawJson)}
      </pre>
    )
  }

  return null
}

function DisclosureHeader({
  expanded,
  onToggle,
  icon,
  label,
  meta,
  tone,
}: {
  expanded: boolean
  onToggle(): void
  icon: ReactNode
  label: string
  meta?: string
  tone: string
}) {
  return (
    <button
      type="button"
      aria-expanded={expanded}
      onClick={onToggle}
      className="flex min-h-11 w-full min-w-0 items-center gap-2 px-3 py-2 text-left"
    >
      <span className={tone}>{icon}</span>
      <span className={`min-w-0 flex-1 text-sm font-medium ${tone}`}>{label}</span>
      {meta ? (
        <span className="shrink-0 rounded bg-slate-900/70 px-1.5 py-0.5 text-[10px] text-slate-400">
          {meta}
        </span>
      ) : null}
      <Chevron expanded={expanded} />
    </button>
  )
}

function ToolStatus({ status }: { status: string }) {
  const active = status === 'pending' || status === 'in_progress'
  const tone =
    status === 'completed'
      ? 'bg-emerald-400/10 text-emerald-300'
      : status === 'failed'
        ? 'bg-rose-400/10 text-rose-300'
        : 'bg-amber-400/10 text-amber-300'

  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded px-1.5 py-0.5 text-[10px] ${tone}`}>
      {active ? <Spinner /> : <span className="size-1.5 rounded-full bg-current" aria-hidden />}
      {status.replaceAll('_', ' ')}
    </span>
  )
}

function PlanStatus({ status }: { status: string }) {
  if (status === 'completed') {
    return (
      <span className="mt-0.5 flex size-4 shrink-0 items-center justify-center rounded-full bg-emerald-500 text-slate-950">
        <svg viewBox="0 0 16 16" className="size-3" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden>
          <path d="m4 8 2.4 2.4L12 5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span className="sr-only">Completed</span>
      </span>
    )
  }

  if (status === 'in_progress') {
    return (
      <span className="mt-0.5 text-sky-300">
        <Spinner />
        <span className="sr-only">In progress</span>
      </span>
    )
  }

  if (status === 'failed') {
    return (
      <span className="mt-0.5 flex size-4 shrink-0 items-center justify-center rounded-full bg-rose-500/20 text-rose-300">
        <svg viewBox="0 0 16 16" className="size-3" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
          <path d="m5 5 6 6m0-6-6 6" strokeLinecap="round" />
        </svg>
        <span className="sr-only">Failed</span>
      </span>
    )
  }

  return (
    <span className="mt-0.5 size-4 shrink-0 rounded-full border border-slate-600">
      <span className="sr-only">Pending</span>
    </span>
  )
}

function DiffSide({ label, text, tone }: { label: string; text: string; tone: 'rose' | 'emerald' }) {
  return (
    <div className={tone === 'rose' ? 'bg-rose-950/20' : 'bg-emerald-950/20'}>
      <div className={`px-3 py-1.5 text-[10px] uppercase tracking-wide ${tone === 'rose' ? 'text-rose-300' : 'text-emerald-300'}`}>
        {label}
      </div>
      <pre className="max-h-72 overflow-auto whitespace-pre-wrap break-words px-3 pb-3 font-mono text-xs leading-5 text-slate-300 [overflow-wrap:anywhere]">
        {text || '∅'}
      </pre>
    </div>
  )
}

function JsonDetails({ label, value }: { label: string; value: string | null }) {
  if (!value) return null

  return (
    <details className="mt-2 overflow-hidden rounded-lg border border-slate-800 bg-slate-950/50">
      <summary className="cursor-pointer px-3 py-2 text-xs font-medium text-slate-400">
        {label}
      </summary>
      <pre className="max-h-72 overflow-auto whitespace-pre-wrap break-words border-t border-slate-800 p-3 font-mono text-xs leading-5 text-slate-400 [overflow-wrap:anywhere]">
        {prettyJson(value)}
      </pre>
    </details>
  )
}

function ToolKindIcon({ kind }: { kind: string | null }) {
  const paths: Record<string, ReactNode> = {
    read: <><path d="M6 3.5h6l3 3V16.5H6z" /><path d="M12 3.5v3h3M8.5 10h4M8.5 13h4" /></>,
    edit: <><path d="m5 15 1-3 6.8-6.8 2 2L8 14z" /><path d="m11.8 6.2 2 2" /></>,
    delete: <><path d="M6.5 7v8h7V7M5 5h10M8 5V3.5h4V5M9 9v4M11 9v4" /></>,
    move: <><path d="M10 3v14M3 10h14M10 3 8 5M10 3l2 2M17 10l-2-2M17 10l-2 2" /></>,
    search: <><circle cx="9" cy="9" r="4.5" /><path d="m12.5 12.5 4 4" /></>,
    execute: <><rect x="3.5" y="4" width="13" height="12" rx="2" /><path d="m6.5 8 2 2-2 2M10.5 12h3" /></>,
    think: <><path d="M7 14h6M8 17h4" /><path d="M6.5 9.5a4.5 4.5 0 1 1 7 3.7c-.7.5-1 1-1 1.3h-5c0-.4-.3-.9-1-1.4a4.5 4.5 0 0 1 0-7.2" /></>,
    fetch: <><circle cx="10" cy="10" r="7" /><path d="M3 10h14M10 3c2 2 2 12 0 14M10 3c-2 2-2 12 0 14" /></>,
  }

  return (
    <span className="flex size-6 shrink-0 items-center justify-center rounded-md bg-slate-800 text-slate-400" title={kind ?? 'tool'}>
      <svg viewBox="0 0 20 20" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden>
        {paths[kind ?? ''] ?? <path d="M7 4h6l1 3 2 1v4l-2 1-1 3H7l-1-3-2-1V8l2-1zM8 10h4" />}
      </svg>
    </span>
  )
}

function Chevron({ expanded }: { expanded: boolean }) {
  return (
    <svg
      viewBox="0 0 16 16"
      className={`size-4 shrink-0 text-slate-500 transition-transform ${expanded ? 'rotate-90' : ''}`}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      aria-hidden
    >
      <path d="m6 3 5 5-5 5" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function Spinner() {
  return (
    <span className="size-3 shrink-0 animate-spin rounded-full border border-current border-r-transparent" aria-hidden />
  )
}

function FileIcon() {
  return (
    <svg viewBox="0 0 16 16" className="size-3.5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden>
      <path d="M3.5 2.5h5l4 4v7h-9zM8.5 2.5v4h4" strokeLinejoin="round" />
    </svg>
  )
}

function ThoughtIcon() {
  return (
    <svg viewBox="0 0 20 20" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
      <circle cx="10" cy="10" r="7" strokeDasharray="2 2" />
      <path d="M8 10h4M10 8v4" strokeLinecap="round" />
    </svg>
  )
}

function PlanIcon() {
  return (
    <svg viewBox="0 0 20 20" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.5" aria-hidden>
      <path d="M7 5h9M7 10h9M7 15h9" strokeLinecap="round" />
      <path d="m3.5 5 .8.8L6 4M3.5 10l.8.8L6 9M3.5 15l.8.8L6 14" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function TerminalIcon() {
  return (
    <svg viewBox="0 0 16 16" className="size-4 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden>
      <rect x="1.5" y="2.5" width="13" height="11" rx="2" />
      <path d="m4 6 2 2-2 2M8 10h3" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  )
}

function basename(path: string): string {
  return path.replaceAll('\\', '/').split('/').filter(Boolean).at(-1) ?? path
}

function prettyJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

function safeWebUrl(uri: string | null): boolean {
  if (!uri) return false
  try {
    const protocol = new URL(uri).protocol
    return protocol === 'https:' || protocol === 'http:'
  } catch {
    return false
  }
}

function imageSource(block: ChatContentBlock): string | null {
  if (block.uri && safeWebUrl(block.uri)) return block.uri
  if (!block.data || !block.mimeType) return null

  const allowed = new Set(['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/avif'])
  return allowed.has(block.mimeType.toLowerCase())
    ? `data:${block.mimeType};base64,${block.data}`
    : null
}
