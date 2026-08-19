/**
 * The MessagePack wire format, decoded by hand.
 *
 * `[MessagePackObject]` with `[Key(n)]` on the C# side serialises as a **positional
 * array**, not a map. Nothing in the payload names a field, so every accessor here
 * is an integer that has to agree with an attribute in a C# file the TypeScript
 * compiler has never seen.
 *
 * That is a real hazard, not a theoretical one: inserting a property in the middle
 * of a C# message shifts every later field here with no error anywhere — the
 * machine list simply starts showing the agent version in the OS column. So the
 * indices below are pinned by `wire.contract.test.ts`, which decodes bytes emitted
 * by the C# serializer itself.
 *
 * Decoding is deliberately tolerant of a payload that is *longer* than expected, so
 * a newer hub that appends a field to a message does not break an older PWA that
 * has not been reloaded yet. Appending is the only safe way to evolve one of these
 * messages, and this is the half of that bargain the client has to keep.
 */

/** Matches `TerminalOutputKind`. Enums travel as their names, not their numbers. */
export type TerminalOutputKind = 'Delta' | 'Snapshot'

/**
 * Matches `CliType`. Which program a session is hosting, as far as the agent could
 * tell — a hint that decides which buttons to offer, and nothing else.
 */
export type CliType = 'Generic' | 'Cmd' | 'PowerShell' | 'ClaudeCode' | 'CopilotCli'

export const CLI_TYPES: readonly CliType[] = ['Generic', 'Cmd', 'PowerShell', 'ClaudeCode', 'CopilotCli']

export interface SessionInfo {
  sessionId: string
  program: string
  args: string[]
  cwd: string
  cols: number
  rows: number
  /** The instant the session started, as a real point in time. */
  startedAt: Date
  /** Falls back to the program name, which is what the agent does when unset. */
  displayName: string
  awaitingInput: boolean
  cliType: CliType
}

export interface MachineInfo {
  machineId: string
  displayName: string
  os: string
  agentVersion: string
  /** False when the machine is known but its agent is not connected. */
  online: boolean
  sessions: SessionInfo[]
}

export interface TerminalOutput {
  sessionId: string
  seq: number
  kind: TerminalOutputKind
  data: Uint8Array
}

export interface HubError {
  code: string
  message: string
  sessionId: string | null
}

// Primitives.

function tuple(value: unknown, name: string): unknown[] {
  if (!Array.isArray(value)) {
    throw new TypeError(`Expected ${name} to arrive as a MessagePack array, got ${typeof value}.`)
  }

  return value
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

function num(value: unknown): number {
  if (typeof value === 'number') return value
  // int64 arrives as a bigint when the decoder is configured for it. Sequence
  // numbers are counters, so the precision loss past 2^53 is not reachable in a
  // human lifetime of terminal output.
  if (typeof value === 'bigint') return Number(value)
  return 0
}

function bool(value: unknown): boolean {
  return value === true
}

function bytes(value: unknown): Uint8Array {
  if (value instanceof Uint8Array) return value
  if (ArrayBuffer.isView(value)) {
    return new Uint8Array(value.buffer, value.byteOffset, value.byteLength)
  }
  return new Uint8Array(0)
}

/**
 * `DateTimeOffset` is a two-element array: the **wall-clock** time written as
 * though it were UTC, then the offset in minutes. Subtracting the offset is what
 * recovers the actual instant — reading only the first element yields a time that
 * is wrong by the sender's timezone, which is invisible when the sender happens to
 * be on UTC and off by hours when they are not.
 */
function instant(value: unknown): Date {
  if (!Array.isArray(value)) {
    return value instanceof Date ? value : new Date(0)
  }

  const wall = value[0]
  const offsetMinutes = num(value[1])

  if (!(wall instanceof Date)) return new Date(0)

  return new Date(wall.getTime() - offsetMinutes * 60_000)
}

// Messages the hub sends us.

export function decodeSession(value: unknown): SessionInfo {
  const s = tuple(value, 'SessionInfo')
  const program = str(s[1])

  return {
    sessionId: str(s[0]),
    program,
    args: Array.isArray(s[2]) ? s[2].map(str) : [],
    cwd: str(s[3]),
    cols: num(s[4]),
    rows: num(s[5]),
    startedAt: instant(s[6]),
    // The agent already substitutes the program name, but a null can still reach
    // us from an older agent, and "unnamed session" is nobody's idea of a label.
    displayName: typeof s[7] === 'string' && s[7].length > 0 ? s[7] : program,
    awaitingInput: bool(s[8]),
    // Absent from a version 1 agent, and unrecognised if a later one adds a type
    // this build has never heard of. Both are the same thing to the user — nobody
    // has said what this is — and both must land on Generic rather than on a string
    // the button catalogue will never match.
    cliType: cliType(s[9]),
  }
}

function cliType(value: unknown): CliType {
  return typeof value === 'string' && (CLI_TYPES as readonly string[]).includes(value)
    ? (value as CliType)
    : 'Generic'
}

export function decodeMachine(value: unknown): MachineInfo {
  const m = tuple(value, 'MachineInfo')

  return {
    machineId: str(m[0]),
    displayName: str(m[1]),
    os: str(m[2]),
    agentVersion: str(m[3]),
    online: bool(m[4]),
    sessions: Array.isArray(m[5]) ? m[5].map(decodeSession) : [],
  }
}

/** `MachineListNotification` — the whole picture, sent in reply to `ListMachines`. */
export function decodeMachineList(value: unknown): MachineInfo[] {
  const n = tuple(value, 'MachineListNotification')
  return Array.isArray(n[0]) ? n[0].map(decodeMachine) : []
}

/** `MachineOnlineNotification` */
export function decodeMachineOnline(value: unknown): MachineInfo {
  return decodeMachine(tuple(value, 'MachineOnlineNotification')[0])
}

/** `MachineOfflineNotification` */
export function decodeMachineOffline(value: unknown): string {
  return str(tuple(value, 'MachineOfflineNotification')[0])
}

/** `ClientSessionOpenedNotification` */
export function decodeSessionOpened(value: unknown): { machineId: string; session: SessionInfo } {
  const n = tuple(value, 'ClientSessionOpenedNotification')
  return { machineId: str(n[0]), session: decodeSession(n[1]) }
}

/** `ClientSessionUpdatedNotification` — same shape as the open, different meaning. */
export function decodeSessionUpdated(value: unknown): { machineId: string; session: SessionInfo } {
  const n = tuple(value, 'ClientSessionUpdatedNotification')
  return { machineId: str(n[0]), session: decodeSession(n[1]) }
}

/** `ClientSessionClosedNotification` */
export function decodeSessionClosed(
  value: unknown,
): { machineId: string; sessionId: string; exitCode: number } {
  const n = tuple(value, 'ClientSessionClosedNotification')
  return { machineId: str(n[0]), sessionId: str(n[1]), exitCode: num(n[2]) }
}

/** `ClientSessionAwaitingInputNotification` */
export function decodeAwaitingInput(
  value: unknown,
): { machineId: string; sessionId: string; hint: string | null } {
  const n = tuple(value, 'ClientSessionAwaitingInputNotification')
  return {
    machineId: str(n[0]),
    sessionId: str(n[1]),
    hint: typeof n[2] === 'string' ? n[2] : null,
  }
}

/** `TerminalOutputNotification` */
export function decodeTerminalOutput(value: unknown): TerminalOutput {
  const n = tuple(value, 'TerminalOutputNotification')

  return {
    sessionId: str(n[0]),
    seq: num(n[1]),
    kind: n[2] === 'Snapshot' ? 'Snapshot' : 'Delta',
    data: bytes(n[3]),
  }
}

/** `TokenExpiringNotification` */
export function decodeTokenExpiring(value: unknown): Date {
  return instant(tuple(value, 'TokenExpiringNotification')[0])
}

/** `ErrorNotification` */
export function decodeError(value: unknown): HubError {
  const n = tuple(value, 'ErrorNotification')

  return {
    code: str(n[0]),
    message: str(n[1]),
    sessionId: typeof n[2] === 'string' ? n[2] : null,
  }
}

// Messages we send the hub. Arrays again, for the same reason.

export function encodeClientHandshake(protocolVersion: number, clientVersion: string): unknown[] {
  return [protocolVersion, clientVersion]
}

export function encodeAttachSession(
  machineId: string,
  sessionId: string,
  cols: number,
  rows: number,
  lastSeq: number | null,
): unknown[] {
  return [machineId, sessionId, cols, rows, lastSeq]
}

export function encodeDetachSession(sessionId: string): unknown[] {
  return [sessionId]
}

/** `RefreshTokenRequest` */
export function encodeRefreshToken(token: string): unknown[] {
  return [token]
}

export function encodeSendInput(sessionId: string, data: Uint8Array): unknown[] {
  return [sessionId, data]
}

export function encodeResizeTerminal(sessionId: string, cols: number, rows: number): unknown[] {
  return [sessionId, cols, rows]
}

export function encodeInterruptSession(sessionId: string): unknown[] {
  return [sessionId]
}

/**
 * `SetSessionTypeRequest`
 *
 * The type goes on the wire as its name, which is how SignalR's MessagePack
 * protocol writes a C# enum. Sending the ordinal instead would decode to whatever
 * enum member happens to sit at that number, and the hub would accept it.
 */
export function encodeSetSessionType(sessionId: string, cliType: CliType): unknown[] {
  return [sessionId, cliType]
}

/**
 * `RegisterPushRequest`
 *
 * The nested array is `PushKeys`, which is its own `[MessagePackObject]` and so
 * serialises as a positional array of its own rather than being flattened.
 */
export function encodeRegisterPush(endpoint: string, p256dh: string, auth: string): unknown[] {
  return [endpoint, [p256dh, auth]]
}
