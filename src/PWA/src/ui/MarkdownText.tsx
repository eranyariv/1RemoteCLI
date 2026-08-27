import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'

import { PAN_X } from './useLockHorizontalPan'

export function MarkdownText({ children }: { children: string }) {
  return (
    <ReactMarkdown
      remarkPlugins={[remarkGfm]}
      skipHtml
      components={{
        a: ({ children: content, ...props }) => (
          <a
            {...props}
            target="_blank"
            rel="noreferrer"
            className="break-words text-sky-300 underline decoration-sky-500/40 underline-offset-2"
          >
            {content}
          </a>
        ),
        blockquote: ({ children: content, ...props }) => (
          <blockquote
            {...props}
            className="my-3 border-l-2 border-slate-600 pl-3 text-slate-400"
          >
            {content}
          </blockquote>
        ),
        code: ({ children: content, className, ...props }) => (
          <code
            {...props}
            className={`${className ?? ''} rounded bg-slate-950/80 px-1 py-0.5 font-mono text-[0.9em] text-slate-100`}
          >
            {content}
          </code>
        ),
        h1: ({ children: content, ...props }) => (
          <h1 {...props} className="mb-2 mt-4 text-lg font-semibold text-slate-100 first:mt-0">
            {content}
          </h1>
        ),
        h2: ({ children: content, ...props }) => (
          <h2 {...props} className="mb-2 mt-4 text-base font-semibold text-slate-100 first:mt-0">
            {content}
          </h2>
        ),
        h3: ({ children: content, ...props }) => (
          <h3 {...props} className="mb-1.5 mt-3 font-semibold text-slate-100 first:mt-0">
            {content}
          </h3>
        ),
        hr: (props) => <hr {...props} className="my-4 border-slate-700" />,
        li: ({ children: content, ...props }) => (
          <li {...props} className="my-1 pl-1">
            {content}
          </li>
        ),
        ol: ({ children: content, ...props }) => (
          <ol {...props} className="my-3 list-decimal space-y-1 pl-6">
            {content}
          </ol>
        ),
        p: ({ children: content, ...props }) => (
          <p
            {...props}
            className="my-2 whitespace-normal break-words first:mt-0 last:mb-0 [overflow-wrap:anywhere]"
          >
            {content}
          </p>
        ),
        pre: ({ children: content, ...props }) => (
          <pre
            {...props}
            {...PAN_X}
            className="my-3 max-w-full overflow-x-auto rounded-lg bg-slate-950/80 p-3 font-mono text-xs leading-5 text-slate-200"
          >
            {content}
          </pre>
        ),
        table: ({ children: content, ...props }) => (
          <div {...PAN_X} className="my-3 max-w-full overflow-x-auto rounded-lg border border-slate-700">
            <table {...props} className="w-full min-w-max border-collapse text-left text-xs">
              {content}
            </table>
          </div>
        ),
        td: ({ children: content, ...props }) => (
          <td {...props} className="border-t border-slate-700 px-3 py-2 align-top text-slate-300">
            {content}
          </td>
        ),
        th: ({ children: content, ...props }) => (
          <th {...props} className="bg-slate-800 px-3 py-2 font-semibold text-slate-100">
            {content}
          </th>
        ),
        ul: ({ children: content, ...props }) => (
          <ul {...props} className="my-3 list-disc space-y-1 pl-6">
            {content}
          </ul>
        ),
      }}
    >
      {children}
    </ReactMarkdown>
  )
}
