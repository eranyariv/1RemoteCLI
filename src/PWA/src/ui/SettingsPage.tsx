import {
  CliDensities,
  type CliDensity,
  type UserSettings,
} from '../settings/userSettings'

const DensityOrder: CliDensity[] = ['comfortable', 'compact', 'dense']

export function SettingsPage({
  settings,
  onDensityChange,
}: {
  settings: UserSettings
  onDensityChange: (density: CliDensity) => void
}) {
  const selected = CliDensities[settings.cliDensity]

  return (
    <section className="flex flex-col gap-4" aria-labelledby="terminal-density-heading">
      <div>
        <h2 id="terminal-density-heading" className="text-base font-semibold text-slate-100">
          CLI density
        </h2>
        <p className="mt-1 text-[13px] leading-5 text-slate-400">
          Controls terminal text size and how many columns and rows fit on your phone.
        </p>
      </div>

      <fieldset className="grid gap-2">
        <legend className="sr-only">CLI density</legend>
        {DensityOrder.map((density) => {
          const definition = CliDensities[density]
          const checked = density === settings.cliDensity

          return (
            <label
              key={density}
              className={`flex min-h-14 cursor-pointer items-center gap-3 rounded-xl border px-4 py-3 transition ${
                checked
                  ? 'border-sky-400/70 bg-sky-400/10'
                  : 'border-slate-800 bg-slate-900/50 active:bg-slate-800'
              }`}
            >
              <input
                type="radio"
                name="cli-density"
                value={density}
                checked={checked}
                onChange={() => onDensityChange(density)}
                className="size-4 accent-sky-400"
              />
              <span className="min-w-0">
                <span className="block text-sm font-medium text-slate-100">
                  {definition.label}
                  {density === 'compact' ? (
                    <span className="ml-2 text-[11px] font-normal text-sky-400">Default</span>
                  ) : null}
                </span>
                <span className="mt-0.5 block text-xs text-slate-500">
                  {definition.description}
                </span>
              </span>
            </label>
          )
        })}
      </fieldset>

      <div className="rounded-xl border border-slate-800 bg-slate-950 p-4">
        <div className="mb-3 flex items-center justify-between">
          <h3 className="text-xs font-medium uppercase tracking-wide text-slate-500">Preview</h3>
          <span className="text-xs text-slate-600">{selected.label}</span>
        </div>
        <div
          aria-live="polite"
          className="overflow-hidden whitespace-pre font-mono text-slate-200"
          style={{ fontSize: selected.fontSize, lineHeight: selected.lineHeight }}
        >
          <span className="text-emerald-400">●</span> npm test{'\n'}
          {'  '}Test Files 46 passed (46){'\n'}
          {'  '}Tests 433 passed (433){'\n'}
          <span className="text-sky-400">❯</span> _
        </div>
      </div>

      <p className="text-xs leading-5 text-slate-500">
        Saved automatically for this signed-in user on this device.
      </p>
    </section>
  )
}
