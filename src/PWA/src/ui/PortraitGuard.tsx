import { useEffect, useRef, useState, type ReactNode } from 'react'

import { shouldBlockLandscape } from '../terminal/device'

export function PortraitGuard({ children }: { children: ReactNode }) {
  const [blocked, setBlocked] = useState(() => shouldBlockLandscape(window))
  const dialogRef = useRef<HTMLDivElement | null>(null)
  const previousFocusRef = useRef<HTMLElement | null>(null)
  const wasBlockedRef = useRef(false)

  useEffect(() => {
    const update = () => setBlocked(shouldBlockLandscape(window))
    window.addEventListener('resize', update)
    window.addEventListener('orientationchange', update)
    return () => {
      window.removeEventListener('resize', update)
      window.removeEventListener('orientationchange', update)
    }
  }, [])

  useEffect(() => {
    if (blocked && !wasBlockedRef.current) {
      previousFocusRef.current =
        document.activeElement instanceof HTMLElement ? document.activeElement : null
      dialogRef.current?.focus()
    } else if (!blocked && wasBlockedRef.current) {
      previousFocusRef.current?.focus()
      previousFocusRef.current = null
    }

    wasBlockedRef.current = blocked
  }, [blocked])

  return (
    <>
      <div inert={blocked} aria-hidden={blocked || undefined}>
        {children}
      </div>
      {blocked ? (
        <div
          ref={dialogRef}
          tabIndex={-1}
          role="dialog"
          aria-modal="true"
          aria-labelledby="portrait-only-title"
          className="fixed inset-0 z-[100] flex items-center justify-center bg-slate-950 px-8 text-center outline-none"
        >
          <div className="max-w-sm">
            <div className="text-4xl text-sky-400" aria-hidden="true">
              ↻
            </div>
            <h1 id="portrait-only-title" className="mt-4 text-lg font-semibold text-slate-100">
              Rotate to portrait
            </h1>
            <p className="mt-2 text-sm leading-6 text-slate-400">
              1RemoteCLI is designed for portrait orientation on phones.
            </p>
          </div>
        </div>
      ) : null}
    </>
  )
}
