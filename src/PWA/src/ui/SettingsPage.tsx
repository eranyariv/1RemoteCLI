import {
  CliDensities,
  SpeechLanguages,
  SpeechVoices,
  type CliDensity,
  type SpeechLanguage,
  type SpeechVoice,
  type UserSettings,
} from '../settings/userSettings'

const DensityOrder: CliDensity[] = ['comfortable', 'compact', 'dense']
const LanguageOrder = Object.keys(SpeechLanguages) as SpeechLanguage[]
const VoiceOrder = Object.keys(SpeechVoices) as SpeechVoice[]

function Toggle({
  checked,
  label,
  description,
  onChange,
}: {
  checked: boolean
  label: string
  description: string
  onChange(checked: boolean): void
}) {
  return (
    <label className="flex min-h-14 cursor-pointer items-center justify-between gap-4 rounded-xl border border-slate-800 bg-slate-900/50 px-4 py-3 active:bg-slate-800">
      <span className="min-w-0">
        <span className="block text-sm font-medium text-slate-100">{label}</span>
        <span className="mt-0.5 block text-xs leading-5 text-slate-500">{description}</span>
      </span>
      <input
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.currentTarget.checked)}
        className="size-5 shrink-0 accent-sky-400"
      />
    </label>
  )
}

export function SettingsPage({
  settings,
  onChange,
}: {
  settings: UserSettings
  onChange: (changes: Partial<UserSettings>) => void
}) {
  const selected = CliDensities[settings.cliDensity]

  return (
    <section className="flex flex-col gap-8" aria-label="User settings">
      <div className="flex flex-col gap-4">
        <div>
          <h2 id="terminal-density-heading" className="text-base font-semibold text-slate-100">
            Terminal
          </h2>
          <p className="mt-1 text-[13px] leading-5 text-slate-400">
            Choose how much terminal content and supporting information fits on screen.
          </p>
        </div>

        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium text-slate-300">CLI density</legend>
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
                  onChange={() => onChange({ cliDensity: density })}
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

        <div className="grid gap-2">
          <Toggle
            checked={settings.showKeyBar}
            label="On-screen key bar"
            description="Show Ctrl, Alt, Enter, Escape, Tab, and arrow keys in terminals."
            onChange={(showKeyBar) => onChange({ showKeyBar })}
          />
          <Toggle
            checked={settings.showLatency}
            label="Latency line"
            description="Show keystroke response-time details below the terminal controls."
            onChange={(showLatency) => onChange({ showLatency })}
          />
        </div>
      </div>

      <div className="flex flex-col gap-4">
        <div>
          <h2 className="text-base font-semibold text-slate-100">Voice</h2>
          <p className="mt-1 text-[13px] leading-5 text-slate-400">
            Configure speech recognition and the voice used for spoken replies.
          </p>
        </div>

        <label className="grid gap-2 text-sm text-slate-300">
          Spoken language
          <select
            value={settings.speechLanguage}
            onChange={(event) =>
              onChange({ speechLanguage: event.currentTarget.value as SpeechLanguage })
            }
            className="min-h-11 rounded-xl border border-slate-700 bg-slate-900 px-3 text-sm text-slate-100"
          >
            {LanguageOrder.map((language) => (
              <option key={language} value={language}>
                {SpeechLanguages[language]}
              </option>
            ))}
          </select>
        </label>

        <label className="grid gap-2 text-sm text-slate-300">
          Reply voice
          <select
            value={settings.speechVoice}
            onChange={(event) =>
              onChange({ speechVoice: event.currentTarget.value as SpeechVoice })
            }
            className="min-h-11 rounded-xl border border-slate-700 bg-slate-900 px-3 text-sm text-slate-100"
          >
            {VoiceOrder.map((voice) => (
              <option key={voice} value={voice}>
                {SpeechVoices[voice]}
              </option>
            ))}
          </select>
        </label>

        <Toggle
          checked={settings.autoListen}
          label="Auto-listen"
          description="Start listening automatically after each spoken reply. Turn off for tap-to-talk."
          onChange={(autoListen) => onChange({ autoListen })}
        />
      </div>

      <div className="flex flex-col gap-4">
        <div>
          <h2 className="text-base font-semibold text-slate-100">App notifications</h2>
          <p className="mt-1 text-[13px] leading-5 text-slate-400">
            Choose which events this device receives after notification permission is granted.
          </p>
        </div>

        <div className="grid gap-2">
          <Toggle
            checked={settings.notifyAwaitingInput}
            label="Waiting for input"
            description="A CLI or agent needs an answer or approval."
            onChange={(notifyAwaitingInput) => onChange({ notifyAwaitingInput })}
          />
          <Toggle
            checked={settings.notifySessionFinished}
            label="Session finished or failed"
            description="A background terminal exits successfully or with an error."
            onChange={(notifySessionFinished) => onChange({ notifySessionFinished })}
          />
          <Toggle
            checked={settings.notifyAnnouncements}
            label="Service announcements"
            description="Important messages from the 1RemoteCLI service."
            onChange={(notifyAnnouncements) => onChange({ notifyAnnouncements })}
          />
        </div>
      </div>

      <p className="text-xs leading-5 text-slate-500">
        Saved automatically for this signed-in user on this device.
      </p>
    </section>
  )
}
