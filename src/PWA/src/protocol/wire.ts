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
export type SessionKind = 'Terminal' | 'AgentChat'
export type ChatTranscriptKind = 'Delta' | 'Snapshot'
export type ChatEventKind =
  | 'UserMessage'
  | 'AgentMessage'
  | 'ToolCall'
  | 'Permission'
  | 'AgentThought'
  | 'Plan'

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
  /**
   * What the user renamed this session to, or null if nobody has.
   *
   * Null rather than an empty string on purpose: clearing the name is meant to
   * reveal the agent's own {@link displayName} again, so the two have to stay
   * distinguishable all the way down the wire.
   */
  customName: string | null
  pinned: boolean
  kind: SessionKind
  /** The project this session is grouped under. Null means the user's General project. */
  projectId: string | null
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

/**
 * A per-user grouping of sessions. Every user always has one non-deletable
 * project, {@link ProjectInfo.isGeneral}, that new sessions default into.
 */
export interface ProjectInfo {
  projectId: string
  name: string
  description: string | null
  siteUrl: string | null
  repoUrl: string | null
  isGeneral: boolean
  /** Zero means no custom icon has been uploaded — show the project's built-in default icon. */
  iconVersion: number
  createdAt: Date
}

/** `ProjectResult` — the direct RPC return of `CreateProject`/`UpdateProject`. */
export interface ProjectResult {
  project: ProjectInfo | null
  /** One of the stable strings in `errors.ts`, set only on failure. */
  error: string | null
}

export interface TerminalOutput {
  sessionId: string
  seq: number
  kind: TerminalOutputKind
  data: Uint8Array
}

export interface ChatPermissionOption {
  optionId: string
  name: string
  kind: string
}

export interface ChatContentBlock {
  type: string
  text: string | null
  path: string | null
  oldText: string | null
  newText: string | null
  terminalId: string | null
  mimeType: string | null
  data: string | null
  uri: string | null
  name: string | null
  title: string | null
  description: string | null
  size: number | null
  rawJson: string | null
}

export interface ChatToolLocation {
  path: string
  line: number | null
}

export interface ChatPlanEntry {
  content: string
  priority: string
  status: string
}

export interface ChatEvent {
  eventId: string
  kind: ChatEventKind
  text: string
  title: string | null
  status: string | null
  toolKind: string | null
  permissionRequestId: string | null
  options: ChatPermissionOption[]
  content: ChatContentBlock[]
  locations: ChatToolLocation[]
  planEntries: ChatPlanEntry[]
  rawInputJson: string | null
  rawOutputJson: string | null
}

export interface ChatTranscript {
  sessionId: string
  seq: number
  kind: ChatTranscriptKind
  events: ChatEvent[]
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
    // Both absent from a hub that predates renaming, which decodes as undefined and
    // has to land on "not renamed, not pinned" rather than on the string "undefined".
    customName: typeof s[10] === 'string' && s[10].length > 0 ? s[10] : null,
    pinned: bool(s[11]),
    kind: s[12] === 'AgentChat' ? 'AgentChat' : 'Terminal',
    // Absent from a hub that predates projects, which decodes as undefined and has
    // to land on null — the user's General project — rather than on the string
    // "undefined" reaching a project lookup that will never find it.
    projectId: typeof s[13] === 'string' && s[13].length > 0 ? s[13] : null,
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

function decodePermissionOption(value: unknown): ChatPermissionOption {
  const option = tuple(value, 'ChatPermissionOption')
  return { optionId: str(option[0]), name: str(option[1]), kind: str(option[2]) }
}

function optionalString(value: unknown): string | null {
  return typeof value === 'string' ? value : null
}

function decodeContentBlock(value: unknown): ChatContentBlock {
  const content = tuple(value, 'ChatContentBlock')
  return {
    type: str(content[0]),
    text: optionalString(content[1]),
    path: optionalString(content[2]),
    oldText: optionalString(content[3]),
    newText: optionalString(content[4]),
    terminalId: optionalString(content[5]),
    mimeType: optionalString(content[6]),
    data: optionalString(content[7]),
    uri: optionalString(content[8]),
    name: optionalString(content[9]),
    title: optionalString(content[10]),
    description: optionalString(content[11]),
    size: typeof content[12] === 'number' ? content[12] : null,
    rawJson: optionalString(content[13]),
  }
}

function decodeToolLocation(value: unknown): ChatToolLocation {
  const location = tuple(value, 'ChatToolLocation')
  return {
    path: str(location[0]),
    line: typeof location[1] === 'number' ? location[1] : null,
  }
}

function decodePlanEntry(value: unknown): ChatPlanEntry {
  const entry = tuple(value, 'ChatPlanEntry')
  return {
    content: str(entry[0]),
    priority: str(entry[1]) || 'medium',
    status: str(entry[2]) || 'pending',
  }
}

function decodeChatEvent(value: unknown): ChatEvent {
  const event = tuple(value, 'ChatEvent')
  const kinds: readonly ChatEventKind[] = [
    'UserMessage',
    'AgentMessage',
    'ToolCall',
    'Permission',
    'AgentThought',
    'Plan',
  ]
  const kind = kinds.includes(event[1] as ChatEventKind)
    ? (event[1] as ChatEventKind)
    : 'ToolCall'

  return {
    eventId: str(event[0]),
    kind,
    text: str(event[2]),
    title: typeof event[3] === 'string' ? event[3] : null,
    status: typeof event[4] === 'string' ? event[4] : null,
    toolKind: typeof event[5] === 'string' ? event[5] : null,
    permissionRequestId: typeof event[6] === 'string' ? event[6] : null,
    options: Array.isArray(event[7]) ? event[7].map(decodePermissionOption) : [],
    content: Array.isArray(event[8]) ? event[8].map(decodeContentBlock) : [],
    locations: Array.isArray(event[9]) ? event[9].map(decodeToolLocation) : [],
    planEntries: Array.isArray(event[10]) ? event[10].map(decodePlanEntry) : [],
    rawInputJson: optionalString(event[11]),
    rawOutputJson: optionalString(event[12]),
  }
}

/** `ChatTranscriptNotification` */
export function decodeChatTranscript(value: unknown): ChatTranscript {
  const transcript = tuple(value, 'ChatTranscriptNotification')
  return {
    sessionId: str(transcript[0]),
    seq: num(transcript[1]),
    kind: transcript[2] === 'Snapshot' ? 'Snapshot' : 'Delta',
    events: Array.isArray(transcript[3]) ? transcript[3].map(decodeChatEvent) : [],
  }
}

/** `ClientSessionAttentionNotification` */
export function decodeSessionAttention(
  value: unknown,
): { machineId: string; sessionId: string; awaitingInput: boolean; hint: string | null } {
  const attention = tuple(value, 'ClientSessionAttentionNotification')
  return {
    machineId: str(attention[0]),
    sessionId: str(attention[1]),
    awaitingInput: bool(attention[2]),
    hint: typeof attention[3] === 'string' ? attention[3] : null,
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

/** `ProjectInfo`, decoded on its own — it is nested inside every project message below. */
export function decodeProject(value: unknown): ProjectInfo {
  const p = tuple(value, 'ProjectInfo')

  return {
    projectId: str(p[0]),
    name: str(p[1]),
    description: typeof p[2] === 'string' ? p[2] : null,
    siteUrl: typeof p[3] === 'string' ? p[3] : null,
    repoUrl: typeof p[4] === 'string' ? p[4] : null,
    isGeneral: bool(p[5]),
    iconVersion: num(p[6]),
    createdAt: instant(p[7]),
  }
}

/** `ProjectListNotification` — the direct RPC return of `ListProjects`. */
export function decodeProjectList(value: unknown): ProjectInfo[] {
  const n = tuple(value, 'ProjectListNotification')
  return Array.isArray(n[0]) ? n[0].map(decodeProject) : []
}

/** `ProjectResult` — the direct RPC return of `CreateProject`/`UpdateProject`. */
export function decodeProjectResult(value: unknown): ProjectResult {
  const n = tuple(value, 'ProjectResult')

  return {
    project: n[0] ? decodeProject(n[0]) : null,
    error: typeof n[1] === 'string' ? n[1] : null,
  }
}

/** `ProjectCreatedNotification` / `ProjectUpdatedNotification` — same shape, different meaning. */
export function decodeProjectNotification(value: unknown): ProjectInfo {
  const n = tuple(value, 'ProjectCreatedNotification')
  return decodeProject(n[0])
}

/** `ProjectDeletedNotification` */
export function decodeProjectDeleted(value: unknown): string {
  return str(tuple(value, 'ProjectDeletedNotification')[0])
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

export function encodeSendChatMessage(sessionId: string, text: string): unknown[] {
  return [sessionId, text]
}

export function encodeRespondChatPermission(
  sessionId: string,
  requestId: string,
  optionId: string,
): unknown[] {
  return [sessionId, requestId, optionId]
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
 * `SetSessionNameRequest`
 *
 * Carries the machine id, unlike its neighbours, because renaming is done from the
 * list where nothing is attached and the hub therefore has no attachment to read the
 * machine from. Null clears the name.
 */
export function encodeSetSessionName(
  machineId: string,
  sessionId: string,
  name: string | null,
): unknown[] {
  return [machineId, sessionId, name]
}

/** `SetSessionPinnedRequest` */
export function encodeSetSessionPinned(
  machineId: string,
  sessionId: string,
  pinned: boolean,
): unknown[] {
  return [machineId, sessionId, pinned]
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

/** `CreateProjectRequest` */
export function encodeCreateProject(
  name: string,
  description: string | null,
  siteUrl: string | null,
  repoUrl: string | null,
): unknown[] {
  return [name, description, siteUrl, repoUrl]
}

/** `UpdateProjectRequest` */
export function encodeUpdateProject(
  projectId: string,
  name: string,
  description: string | null,
  siteUrl: string | null,
  repoUrl: string | null,
): unknown[] {
  return [projectId, name, description, siteUrl, repoUrl]
}

/** `DeleteProjectRequest` */
export function encodeDeleteProject(projectId: string): unknown[] {
  return [projectId]
}

/**
 * `SetSessionProjectRequest`
 *
 * Carries the machine id, for the same reason as `SetSessionNameRequest`: moving
 * is done from the list, not from an attachment. Null moves the session back to
 * the user's General project.
 */
export function encodeSetSessionProject(
  machineId: string,
  sessionId: string,
  projectId: string | null,
): unknown[] {
  return [machineId, sessionId, projectId]
}
