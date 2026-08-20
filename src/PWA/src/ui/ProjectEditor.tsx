import { useEffect, useRef, useState } from 'react'

import { describeError } from '../protocol/errors'
import type { ProjectInfo } from '../protocol/wire'
import type { RelayClient } from '../relay/client'
import { deleteProjectIcon, uploadProjectIcon, useProjectIconUrl } from '../relay/projectIcon'
import { downscaleToSquare } from './icon'
import { Banner } from './Chrome'

const DEFAULT_ICON = '/icon-192.png'

/**
 * Create and edit, in one form.
 *
 * A modal sheet rather than a route: projects are edited from wherever a tile
 * or the move-to-project picker is, and a route would need a place to go back
 * to that does not already exist for every caller.
 */
export function ProjectEditor({
  client,
  project,
  onClose,
}: {
  client: RelayClient
  /** Omitted to create a new project instead of editing one. */
  project?: ProjectInfo
  onClose(): void
}) {
  const [name, setName] = useState(project?.name ?? '')
  const [description, setDescription] = useState(project?.description ?? '')
  const [siteUrl, setSiteUrl] = useState(project?.siteUrl ?? '')
  const [repoUrl, setRepoUrl] = useState(project?.repoUrl ?? '')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [pendingIcon, setPendingIcon] = useState<File | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  // Tracks the project once creation succeeds, so an icon picked before the
  // first save has an id to upload against, and so a freshly-created project's
  // own icon shows immediately without waiting for the ProjectCreated broadcast
  // to loop back around.
  const [saved, setSaved] = useState(project ?? null)
  const iconUrl = useProjectIconUrl(saved?.projectId ?? '', saved?.iconVersion ?? 0)
  const pendingIconUrl = useFileUrl(pendingIcon)

  const isGeneral = project?.isGeneral ?? false

  const save = async () => {
    const trimmedName = name.trim()
    if (trimmedName.length === 0) {
      setError('Name is required.')
      return
    }

    setBusy(true)
    setError(null)

    const result = saved
      ? await client.updateProject(
          saved.projectId,
          trimmedName,
          blankToNull(description),
          blankToNull(siteUrl),
          blankToNull(repoUrl),
        )
      : await client.createProject(
          trimmedName,
          blankToNull(description),
          blankToNull(siteUrl),
          blankToNull(repoUrl),
        )

    if (result.error || !result.project) {
      setBusy(false)
      setError(describeError(result.error ?? 'internal_error'))
      return
    }

    const savedProject = result.project
    setSaved(savedProject)

    if (pendingIcon) {
      const version = await uploadProjectIcon(savedProject.projectId, pendingIcon)
      if (version === null) {
        setBusy(false)
        setError('Project saved, but its icon could not be uploaded. Try a smaller image.')
        return
      }

      setSaved({ ...savedProject, iconVersion: version })
      setPendingIcon(null)
    }

    setBusy(false)
    onClose()
  }

  const pickIcon = async (file: File) => {
    setBusy(true)
    setError(null)

    try {
      const downscaled = await downscaleToSquare(file)
      if (!saved) {
        setPendingIcon(downscaled)
        return
      }

      const version = await uploadProjectIcon(saved.projectId, downscaled)

      if (version === null) {
        setError('Could not upload that icon. Try a smaller image.')
        return
      }

      setSaved({ ...saved, iconVersion: version })
      setPendingIcon(null)
    } finally {
      setBusy(false)
    }
  }

  const clearIcon = async () => {
    if (pendingIcon) {
      setPendingIcon(null)
      return
    }

    if (!saved) return

    setBusy(true)
    setError(null)

    const ok = await deleteProjectIcon(saved.projectId)
    setBusy(false)

    if (!ok) {
      setError('Could not remove that icon.')
      return
    }

    setSaved({ ...saved, iconVersion: 0 })
  }

  const remove = async () => {
    if (!saved) return

    setBusy(true)
    setError(null)

    const err = await client.deleteProject(saved.projectId)
    setBusy(false)

    if (err) {
      setError(describeError(err.code, err.message))
      return
    }

    onClose()
  }

  return (
    <div className="fixed inset-0 z-30 flex items-end justify-center bg-slate-950/70 sm:items-center">
      <div className="max-h-[85dvh] w-full max-w-md overflow-y-auto rounded-t-2xl border border-slate-800 bg-slate-900 p-4 sm:rounded-2xl">
        <h2 className="text-[15px] font-semibold text-slate-100">
          {saved ? 'Edit project' : 'New project'}
        </h2>

        {error ? (
          <div className="mt-3">
            <Banner tone="error" title={error} />
          </div>
        ) : null}

        <div className="mt-3 flex flex-col gap-3">
          <div className="flex items-center gap-3">
            <img
              src={pendingIconUrl ?? iconUrl ?? DEFAULT_ICON}
              alt=""
              aria-hidden
              className="size-14 shrink-0 rounded-xl bg-slate-800 object-cover"
            />

            <div className="flex flex-col gap-1.5">
              <button
                type="button"
                disabled={busy}
                onClick={() => fileInput.current?.click()}
                className="min-h-9 rounded-lg bg-slate-700 px-3 text-left text-sm font-medium text-slate-100 transition active:bg-slate-600 disabled:opacity-40"
              >
                {saved || pendingIcon ? 'Change icon' : 'Choose icon'}
              </button>

              {pendingIconUrl || iconUrl ? (
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void clearIcon()}
                  className="min-h-9 rounded-lg px-3 text-left text-sm text-slate-400 transition active:bg-slate-800 disabled:opacity-40"
                >
                  Use default icon
                </button>
              ) : null}
            </div>

            <input
              ref={fileInput}
              type="file"
              accept="image/png,image/jpeg,image/webp"
              aria-label="Project icon"
              className="hidden"
              onChange={(event) => {
                const file = event.target.files?.[0]
                event.target.value = ''
                if (file) void pickIcon(file)
              }}
            />
          </div>

          <label className="flex flex-col gap-1">
            <span className="text-xs font-medium text-slate-400">Name</span>
            <input
              value={name}
              onChange={(event) => setName(event.target.value)}
              disabled={isGeneral}
              maxLength={60}
              placeholder="Project name"
              className="min-h-10 rounded-lg border border-slate-700 bg-slate-950 px-2.5 text-[15px] text-slate-100 placeholder:text-slate-600 focus:border-slate-500 focus:outline-none disabled:text-slate-500"
            />
          </label>

          <label className="flex flex-col gap-1">
            <span className="text-xs font-medium text-slate-400">Description</span>
            <textarea
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              maxLength={280}
              rows={2}
              placeholder="Optional"
              className="rounded-lg border border-slate-700 bg-slate-950 px-2.5 py-2 text-[15px] text-slate-100 placeholder:text-slate-600 focus:border-slate-500 focus:outline-none"
            />
          </label>

          <label className="flex flex-col gap-1">
            <span className="text-xs font-medium text-slate-400">Site URL</span>
            <input
              value={siteUrl}
              onChange={(event) => setSiteUrl(event.target.value)}
              type="url"
              placeholder="Optional"
              className="min-h-10 rounded-lg border border-slate-700 bg-slate-950 px-2.5 text-[15px] text-slate-100 placeholder:text-slate-600 focus:border-slate-500 focus:outline-none"
            />
          </label>

          <label className="flex flex-col gap-1">
            <span className="text-xs font-medium text-slate-400">GitHub repo URL</span>
            <input
              value={repoUrl}
              onChange={(event) => setRepoUrl(event.target.value)}
              type="url"
              placeholder="Optional"
              className="min-h-10 rounded-lg border border-slate-700 bg-slate-950 px-2.5 text-[15px] text-slate-100 placeholder:text-slate-600 focus:border-slate-500 focus:outline-none"
            />
          </label>
        </div>

        <div className="mt-4 flex items-center gap-2">
          <button
            type="button"
            disabled={busy}
            onClick={() => void save()}
            className="min-h-10 flex-1 rounded-lg bg-sky-500 px-3 text-sm font-medium text-slate-950 transition active:bg-sky-400 disabled:opacity-40"
          >
            Save
          </button>

          <button
            type="button"
            onClick={onClose}
            className="min-h-10 rounded-lg px-3 text-sm text-slate-400 transition active:bg-slate-800"
          >
            Cancel
          </button>
        </div>

        {saved && !isGeneral ? (
          <div className="mt-4 border-t border-slate-800 pt-3">
            {confirmingDelete ? (
              <div className="flex items-center gap-2">
                <p className="flex-1 text-[13px] text-slate-400">
                  Delete this project? Its sessions move back to General.
                </p>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => void remove()}
                  className="min-h-9 rounded-lg bg-rose-500/90 px-3 text-sm font-medium text-slate-950 transition active:bg-rose-500 disabled:opacity-40"
                >
                  Delete
                </button>
                <button
                  type="button"
                  onClick={() => setConfirmingDelete(false)}
                  className="min-h-9 rounded-lg px-2.5 text-sm text-slate-400 transition active:bg-slate-800"
                >
                  Cancel
                </button>
              </div>
            ) : (
              <button
                type="button"
                onClick={() => setConfirmingDelete(true)}
                className="min-h-9 rounded-lg px-2.5 text-sm text-rose-400 transition active:bg-slate-800"
              >
                Delete project
              </button>
            )}
          </div>
        ) : null}
      </div>
    </div>
  )
}

function blankToNull(value: string): string | null {
  const trimmed = value.trim()
  return trimmed.length > 0 ? trimmed : null
}

function useFileUrl(file: File | null): string | null {
  const [url, setUrl] = useState<string | null>(null)

  useEffect(() => {
    if (!file) {
      setUrl(null)
      return
    }

    const next = URL.createObjectURL(file)
    setUrl(next)
    return () => URL.revokeObjectURL(next)
  }, [file])

  return url
}
