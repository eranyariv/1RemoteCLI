/**
 * What each CLI is worth having a button for.
 *
 * The generic key bar solves the keys a phone is missing. It does not solve the
 * keys a phone *has* but which are miserable to reach: `Shift+Tab` needs two
 * fingers on a surface that has one, `Esc Esc` needs a double tap fast enough to
 * count, and every slash command is a dozen taps on a software keyboard that likes
 * to autocorrect `/compact` into `/compact.` — into a prompt that will then be sent
 * to a model as a question.
 *
 * So the catalogue is per CLI: the handful of things that particular program is
 * driven by, one tap each. It is deliberately short. A list of everything is a
 * reference manual, and a reference manual on a phone screen is slower than typing.
 *
 * Nothing here is interpreted by the agent. A shortcut is bytes on the wire, the
 * same bytes the desk keyboard would send, and a command is the literal text of the
 * command — which is why this file can be wrong about a CLI's feature set without
 * being dangerous. The worst case is a program answering "unknown command".
 */

import type { CliType } from '../protocol/wire'
import type { KeyDefinition } from './keys'

const ESC = 0x1b

function seq(...bytes: number[]): Uint8Array {
  return new Uint8Array(bytes)
}

/**
 * A slash command, or any literal text worth a button.
 *
 * The text is *inserted, not submitted*. Half of these take an argument — `/model`
 * with a name, `/add-dir` with a path — and the ones that do not are still one tap
 * from Return on a bar that is always on screen. Submitting for the user would make
 * the safe commands one tap faster and the arguable ones (`/clear`, which discards
 * the conversation) impossible to take back.
 */
export interface CommandDefinition {
  /** What goes on the button, and what is typed. */
  text: string
  /** Why you would press it. */
  description: string
}

/**
 * `CSI Z` — the sequence a terminal sends for Shift+Tab, and what both agents
 * listen for to cycle their permission modes. Not in `Keys`, because a shell has no
 * use for it and the shared bar has no room to spare.
 */
const shiftTab: KeyDefinition = {
  label: '⇧⇥',
  name: 'Shift+Tab — cycle mode',
  bytes: seq(ESC, 0x5b, 0x5a),
}

/**
 * Two escapes, in one press.
 *
 * Claude Code distinguishes a single Esc (stop talking) from a double (open the
 * rewind menu) by timing, and a phone cannot reliably produce a double tap inside
 * that window through a relay. Sending both bytes at once always lands.
 */
const escEsc: KeyDefinition = {
  label: '⎋⎋',
  name: 'Escape twice — rewind',
  bytes: seq(ESC, ESC),
}

const ctrlR: KeyDefinition = { label: '^R', name: 'Ctrl+R — search history', bytes: seq(0x12) }
const ctrlO: KeyDefinition = { label: '^O', name: 'Ctrl+O — transcript', bytes: seq(0x0f) }
const ctrlL: KeyDefinition = { label: '^L', name: 'Ctrl+L — clear screen', bytes: seq(0x0c) }
const ctrlU: KeyDefinition = { label: '^U', name: 'Ctrl+U — clear the line', bytes: seq(0x15) }
const ctrlA: KeyDefinition = { label: '^A', name: 'Ctrl+A — start of line', bytes: seq(0x01) }
const ctrlE: KeyDefinition = { label: '^E', name: 'Ctrl+E — end of line', bytes: seq(0x05) }

export interface CliCatalog {
  /** How the type is named in the interface. */
  label: string
  /** The keys this program is actually driven by, beyond the shared bar. */
  shortcuts: KeyDefinition[]
  /** Its commands, most-reached-for first. */
  commands: CommandDefinition[]
}

/**
 * Claude Code, from Anthropic's published cheatsheet, cut to what a phone session
 * plausibly needs: change how much it is allowed to do, take back what it just did,
 * see what it cost, and stop it running out of context mid-task.
 */
const claudeCode: CliCatalog = {
  label: 'Claude Code',
  shortcuts: [shiftTab, escEsc, ctrlO, ctrlR],
  commands: [
    { text: '/compact', description: 'Summarise the conversation to free context' },
    { text: '/clear', description: 'Start over, keeping project memory' },
    { text: '/rewind', description: 'Roll back to an earlier state' },
    { text: '/diff', description: 'Review uncommitted changes' },
    { text: '/context', description: "What's in the context window" },
    { text: '/cost', description: 'Tokens used and what they cost' },
    { text: '/model', description: 'Show or switch model' },
    { text: '/resume', description: 'Continue a previous session' },
  ],
}

/**
 * Copilot CLI. The same job, a different vocabulary — and `/usage` rather than
 * `/cost`, because the plan is a quota rather than a bill.
 */
const copilotCli: CliCatalog = {
  label: 'Copilot CLI',
  shortcuts: [shiftTab, ctrlR],
  commands: [
    { text: '/compact', description: 'Summarise the conversation to free context' },
    { text: '/clear', description: 'Reset the session' },
    { text: '/undo', description: 'Undo the last turn' },
    { text: '/usage', description: 'Tokens used this session' },
    { text: '/model', description: 'Show or switch model' },
    { text: '/cwd', description: 'Where it thinks it is' },
    { text: '/resume', description: 'Continue a previous session' },
    { text: '/help', description: 'Every command this version has' },
  ],
}

/**
 * PowerShell. No slash commands — its equivalent is a handful of cmdlets you would
 * otherwise be typing in full, and PSReadLine's line editing, which is the part that
 * hurts most without a physical keyboard.
 */
const powerShell: CliCatalog = {
  label: 'PowerShell',
  shortcuts: [ctrlR, ctrlL, ctrlU, ctrlA, ctrlE],
  commands: [
    { text: 'Get-Location', description: 'Where am I' },
    { text: 'Get-ChildItem', description: 'List this directory' },
    { text: 'git status', description: 'What has changed' },
    { text: 'Get-History', description: 'Recent commands' },
  ],
}

const cmd: CliCatalog = {
  label: 'Command Prompt',
  shortcuts: [ctrlL],
  commands: [
    { text: 'cd', description: 'Where am I' },
    { text: 'dir', description: 'List this directory' },
    { text: 'git status', description: 'What has changed' },
  ],
}

/**
 * The fallback, and the shape of the honest answer: when we do not know what is
 * running, we do not guess at its commands. The shortcuts offered are the ones every
 * line-editing program on a POSIX-ish terminal agrees about.
 */
const generic: CliCatalog = {
  label: 'Terminal',
  shortcuts: [ctrlL, ctrlU, ctrlA, ctrlE],
  commands: [],
}

const catalogs: Record<CliType, CliCatalog> = {
  Generic: generic,
  Cmd: cmd,
  PowerShell: powerShell,
  ClaudeCode: claudeCode,
  CopilotCli: copilotCli,
}

export function catalogFor(type: CliType): CliCatalog {
  return catalogs[type] ?? generic
}

/** The name to show for a type, without pulling in the rest of the catalogue. */
export function labelFor(type: CliType): string {
  return catalogFor(type).label
}
