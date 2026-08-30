import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'

import { auth } from '../auth/impl'
import { Client, PROTOCOL_VERSION, Server } from '../protocol/methods'
import { ErrorCodes, describeError } from '../protocol/errors'
import {
  decodeAwaitingInput,
  decodeChatAttachmentReply,
  decodeChatPromptReply,
  decodeChatTranscript,
  decodeError,
  decodeMachineList,
  decodeMachineOffline,
  decodeMachineOnline,
  decodeProjectDeleted,
  decodeProjectList,
  decodeProjectNotification,
  decodeProjectResult,
  decodeSessionClosed,
  decodeSessionOpened,
  decodeSessionAttention,
  decodeSessionUpdated,
  decodeTerminalUploadReply,
  decodeTerminalOutput,
  encodeBeginChatAttachment,
  encodeBeginTerminalUpload,
  encodeCancelChatAttachment,
  encodeCancelTerminalUpload,
  encodeAttachSession,
  encodeChatAttachmentChunk,
  encodeClientHandshake,
  encodeCreateProject,
  encodeDeleteProject,
  encodeDetachSession,
  encodeInterruptSession,
  encodeRespondChatPermission,
  encodeRegisterPush,
  encodeResizeTerminal,
  encodeRefreshToken,
  encodeSendInput,
  encodeSendChatMessage,
  encodeSendChatPrompt,
  encodeSetSessionName,
  encodeSetSessionPinned,
  encodeSetSessionProject,
  encodeSetSessionType,
  encodeTerminalUploadChunk,
  encodeUpdateProject,
  type ChatAttachmentReply,
  type CliType,
  type ChatTranscript,
  type HubError,
  type MachineInfo,
  type ProjectInfo,
  type ProjectResult,
  type SessionInfo,
  type TerminalOutput,
  type TerminalUploadReply,
} from '../protocol/wire'
import type { PushPreferences, PushRegistration } from '../push/subscription'
import {
  CHAT_ATTACHMENT_CHUNK_BYTES,
  MAX_CHAT_ATTACHMENT_BYTES,
  formatBytes,
} from '../chat/attachment'
import {
  MAX_TERMINAL_UPLOAD_BYTES,
  TERMINAL_UPLOAD_CHUNK_BYTES,
} from '../terminal/attachment'
import { resolveHubUrl } from './endpoint'
import { ForeverRetryPolicy, reconnectDelay } from './backoff'

/** What the app shows about the connection itself. */
export type RelayStatus =
  | 'signed-out'
  | 'connecting'
  | 'connected'
  | 'reconnecting'
  | 'rejected'
  | 'offline'

export interface RelayEvents {
  status(status: RelayStatus, detail?: string): void
  machines(machines: MachineInfo[]): void
  machineOnline(machine: MachineInfo): void
  machineOffline(machineId: string): void
  sessionOpened(machineId: string, session: SessionInfo): void
  sessionUpdated(machineId: string, session: SessionInfo): void
  sessionClosed(machineId: string, sessionId: string, exitCode: number): void
  awaitingInput(machineId: string, sessionId: string, hint: string | null): void
  terminalOutput(output: TerminalOutput): void
  chatTranscript(transcript: ChatTranscript): void
  attention(machineId: string, sessionId: string, awaitingInput: boolean, hint: string | null): void
  error(error: HubError): void
  projects(projects: ProjectInfo[]): void
  projectCreated(project: ProjectInfo): void
  projectUpdated(project: ProjectInfo): void
  projectDeleted(projectId: string): void
}

export interface TerminalUploadProgress {
  confirmedBytes: number
  totalBytes: number
}

export interface TerminalUploadOutcome {
  remotePath: string | null
  error: HubError | null
  cancelled: boolean
}

/** The result of staging one chat attachment on the machine that owns the chat. */
export interface ChatAttachmentOutcome {
  ready: boolean
  error: HubError | null
  cancelled: boolean
}

const CLIENT_VERSION = `pwa/${__APP_VERSION__}`

/**
 * The client end of the relay.
 *
 * Deliberately not a React thing. A SignalR connection outlives any particular
 * render, must survive a phone locking and unlocking, and has to keep working
 * while the component tree is being torn down and rebuilt; wiring it into
 * component lifecycle is how you end up with two connections and a leak.
 */
export class RelayClient {
  private readonly listeners = new Map<keyof RelayEvents, Set<(...args: never[]) => void>>()
  private readonly url: string
  private connection: HubConnection | null = null
  private starting: Promise<void> | null = null
  private stopped = false

  /** Set while a retry is pending, so it can be cancelled by `stop`. */
  private retryTimer: ReturnType<typeof setTimeout> | null = null

  /** How many consecutive attempts have failed, which is what sets the delay. */
  private attempts = 0

  constructor(url: string = resolveHubUrl()) {
    this.url = url
  }

  /**
   * Subscribes to an event and returns the unsubscribe.
   *
   * Several listeners per event, not one: the machine list and whichever terminal
   * is on screen both need to see output, and a single-handler design would have
   * the second subscriber silently evict the first — a bug that presents as the
   * session list going stale only while a terminal is open.
   */
  on<K extends keyof RelayEvents>(event: K, handler: RelayEvents[K]): () => void {
    let set = this.listeners.get(event)

    if (!set) {
      set = new Set()
      this.listeners.set(event, set)
    }

    set.add(handler as (...args: never[]) => void)

    return () => {
      set.delete(handler as (...args: never[]) => void)
    }
  }

  get connected(): boolean {
    return this.connection?.state === HubConnectionState.Connected
  }

  /** Connects, or does nothing if a connection is already up or on its way. */
  async start(): Promise<void> {
    if (this.starting) return this.starting

    this.stopped = false
    this.cancelRetry()

    this.starting = this.connect().finally(() => {
      this.starting = null
    })

    return this.starting
  }

  async stop(): Promise<void> {
    this.stopped = true
    this.cancelRetry()

    const connection = this.connection
    this.connection = null

    await connection?.stop()
  }

  /**
   * Tries again later, forever.
   *
   * SignalR's automatic reconnect only covers a connection that was established
   * and then dropped. A first attempt that fails — the usual case when the app is
   * opened with no signal — is not its problem, and without this the app would sit
   * on "offline" until something else happened to nudge it.
   */
  private scheduleRetry(): void {
    if (this.stopped || this.retryTimer !== null) return

    const delay = reconnectDelay(this.attempts)
    this.attempts += 1

    this.retryTimer = setTimeout(() => {
      this.retryTimer = null
      if (!this.stopped) void this.start()
    }, delay)
  }

  private cancelRetry(): void {
    if (this.retryTimer === null) return

    clearTimeout(this.retryTimer)
    this.retryTimer = null
  }

  private async connect(): Promise<void> {
    const token = await auth.getAccessToken()

    if (!token) {
      this.emit('status', 'signed-out')
      return
    }

    this.emit('status', 'connecting')

    const connection = new HubConnectionBuilder()
      .withUrl(this.url, {
        // Called on every connect and every reconnect, which is the point: a
        // socket that reconnects after an hour in someone's pocket must not
        // present the token it was born with. Returning the empty string rather
        // than throwing lets the hub reject the connection cleanly, which we can
        // report, instead of failing inside the transport, which we cannot.
        accessTokenFactory: async () => (await auth.getAccessToken()) ?? '',
      })
      // MessagePack because terminal output is binary, and JSON would base64
      // every frame on the one path that is hot.
      .withHubProtocol(new MessagePackHubProtocol())
      .withAutomaticReconnect(new ForeverRetryPolicy())
      .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning)
      .build()

    this.attachHandlers(connection)
    this.connection = connection

    try {
      await connection.start()
    } catch (error) {
      this.connection = null
      this.emit('status', 'offline', errorText(error))
      this.scheduleRetry()
      return
    }

    // The handshake is separate from everything else so an incompatible client is
    // turned away before it can issue any other method, rather than half-working.
    if (!(await this.handshake(connection))) return

    this.attempts = 0
    this.emit('status', 'connected')
    await this.refreshMachines()
    await this.refreshProjects()
  }

  /**
   * Introduces this connection to the hub.
   *
   * Done on every connection, including the ones SignalR makes for us when it
   * reconnects. The hub's record of a client is keyed by connection id and a reconnect
   * produces a new one, so a connection that skipped this is one the hub has never
   * heard of: it will refuse to attach and refuse to carry a keystroke. That is not a
   * corner case on a phone — a lift, a tunnel or a locked screen is enough — and the
   * failure is silent until the user types something.
   *
   * Returns false when the hub turned us away, having already reported it.
   */
  private async handshake(connection: HubConnection): Promise<boolean> {
    const rejection = await connection.invoke(
      Server.ClientHandshake,
      encodeClientHandshake(PROTOCOL_VERSION, CLIENT_VERSION),
    )

    if (!rejection) return true

    const problem = decodeError(rejection)
    this.emit('status', 'rejected', describeError(problem.code, problem.message))
    this.emit('error', problem)

    // Retrying a refusal would produce a login loop against a hub that has
    // already made its answer clear.
    await this.stop()
    return false
  }

  /**
   * Hands the hub a fresh token before the current one runs out.
   *
   * MSAL serves this from its own cache when the token still has life in it and goes
   * to the network only when it does not, so this is cheap in the common case and
   * correct in the one that matters.
   *
   * A failure is reported as signed out rather than swallowed. The alternative is a
   * terminal that keeps working for a few more minutes and then disconnects for no
   * visible reason, which is the same outcome with the explanation removed.
   */
  private async refreshToken(connection: HubConnection): Promise<void> {
    let token: string | null = null

    try {
      token = await auth.getAccessToken()
    } catch {
      token = null
    }

    if (!token) {
      this.emit('status', 'signed-out', 'Your sign-in could not be renewed.')
      return
    }

    try {
      const rejection = await connection.invoke(Server.RefreshToken, encodeRefreshToken(token))

      if (rejection) {
        const problem = decodeError(rejection)
        this.emit('error', problem)
        this.emit('status', 'signed-out', describeError(problem.code, problem.message))
      }
    } catch {
      // The invoke fails when the hub has already closed the connection, which is
      // exactly what it does when a refresh presents somebody else's identity. The
      // reconnect path owns what happens next.
    }
  }

  private attachHandlers(connection: HubConnection): void {
    connection.on(Client.MachineList, (n) => this.emit('machines', decodeMachineList(n)))
    connection.on(Client.MachineOnline, (n) => this.emit('machineOnline', decodeMachineOnline(n)))
    connection.on(Client.MachineOffline, (n) =>
      this.emit('machineOffline', decodeMachineOffline(n)),
    )

    connection.on(Client.SessionOpened, (n) => {
      const { machineId, session } = decodeSessionOpened(n)
      this.emit('sessionOpened', machineId, session)
    })

    connection.on(Client.SessionUpdated, (n) => {
      const { machineId, session } = decodeSessionUpdated(n)
      this.emit('sessionUpdated', machineId, session)
    })

    connection.on(Client.SessionClosed, (n) => {
      const { machineId, sessionId, exitCode } = decodeSessionClosed(n)
      this.emit('sessionClosed', machineId, sessionId, exitCode)
    })

    connection.on(Client.SessionAwaitingInput, (n) => {
      const { machineId, sessionId, hint } = decodeAwaitingInput(n)
      this.emit('awaitingInput', machineId, sessionId, hint)
    })

    connection.on(Client.TerminalOutput, (n) => this.emit('terminalOutput', decodeTerminalOutput(n)))
    connection.on(Client.ChatTranscript, (n) => this.emit('chatTranscript', decodeChatTranscript(n)))

    connection.on(Client.SessionAttention, (n) => {
      const { machineId, sessionId, awaitingInput, hint } = decodeSessionAttention(n)
      this.emit('attention', machineId, sessionId, awaitingInput, hint)
    })

    connection.on(Client.ProjectCreated, (n) =>
      this.emit('projectCreated', decodeProjectNotification(n)),
    )
    connection.on(Client.ProjectUpdated, (n) =>
      this.emit('projectUpdated', decodeProjectNotification(n)),
    )
    connection.on(Client.ProjectDeleted, (n) => this.emit('projectDeleted', decodeProjectDeleted(n)))

    // SignalR checks the token once, at the handshake. The hub therefore has to ask,
    // and this has to answer, or the connection is dropped when the token runs out.
    connection.on(Client.TokenExpiring, () => this.refreshToken(connection))

    connection.on(Client.Error, (n) => {
      const problem = decodeError(n)
      this.emit('error', problem)

      if (problem.code === ErrorCodes.TokenExpired || problem.code === ErrorCodes.IdentityChanged) {
        this.emit('status', 'signed-out', describeError(problem.code))
      }
    })

    connection.onreconnecting(() => this.emit('status', 'reconnecting'))

    connection.onreconnected(async () => {
      // Introduce ourselves again before claiming to be connected. The hub keys its
      // record of a client by connection id and this is a new one, so until the
      // handshake lands we are a stranger to it — and the app, believing itself
      // connected, would let the user type into a session the hub will not route.
      if (!(await this.handshake(connection))) return

      this.attempts = 0
      this.emit('status', 'connected')
      // The hub's registry is per connection, so a new connection id means it has
      // never heard of us. Re-listing is the normal path, not a repair.
      await this.refreshMachines()
      await this.refreshProjects()
    })

    connection.onclose(() => {
      if (this.stopped) return

      // SignalR only closes for good once its own policy gives up, and ours never
      // does — but the close also fires when the handshake or transport fails
      // outside a reconnect, and those are the cases we have to cover ourselves.
      this.emit('status', 'offline')
      this.scheduleRetry()
    })
  }

  /** Asks for the whole picture. Safe to call at any time. */
  async refreshMachines(): Promise<void> {
    if (!this.connected) return

    const list = await this.connection!.invoke(Server.ListMachines)
    this.emit('machines', decodeMachineList(list))
  }

  /** Asks for this user's projects. Safe to call at any time. */
  async refreshProjects(): Promise<void> {
    if (!this.connected) return

    const list = await this.connection!.invoke(Server.ListProjects)
    this.emit('projects', decodeProjectList(list))
  }

  async attach(
    machineId: string,
    sessionId: string,
    cols: number,
    rows: number,
    lastSeq: number | null = null,
  ): Promise<HubError | null> {
    return this.request(
      Server.AttachSession,
      encodeAttachSession(machineId, sessionId, cols, rows, lastSeq),
    )
  }

  async detach(sessionId: string): Promise<HubError | null> {
    return this.request(Server.DetachSession, encodeDetachSession(sessionId))
  }

  async sendInput(sessionId: string, data: Uint8Array): Promise<HubError | null> {
    return this.request(Server.SendInput, encodeSendInput(sessionId, data))
  }

  /**
   * Sends a browser-local file in ordered chunks, waiting for the agent to confirm
   * each one is on disk before reporting progress.
   */
  async uploadTerminalFile(
    sessionId: string,
    file: File,
    onProgress: (progress: TerminalUploadProgress) => void,
    signal?: AbortSignal,
  ): Promise<TerminalUploadOutcome> {
    if (file.size > MAX_TERMINAL_UPLOAD_BYTES) {
      return this.uploadFailure(
        ErrorCodes.FileTooLarge,
        `Files are limited to ${MAX_TERMINAL_UPLOAD_BYTES / (1024 * 1024)} MB.`,
        sessionId,
      )
    }

    if (signal?.aborted) return { remotePath: null, error: null, cancelled: true }

    const uploadId = crypto.randomUUID()
    let started = false
    let completed = false

    try {
      let reply = await this.terminalUploadRequest(
        Server.BeginTerminalUpload,
        encodeBeginTerminalUpload(sessionId, uploadId, file.name, file.size),
        uploadId,
        file.size,
      )

      if (reply.errorCode) return this.replyFailure(reply, sessionId)
      started = true

      if (
        reply.uploadId !== uploadId ||
        reply.totalBytes !== file.size ||
        reply.confirmedBytes !== 0
      ) {
        return this.uploadFailure(
          ErrorCodes.UploadFailed,
          'The machine returned inconsistent upload metadata.',
          sessionId,
        )
      }

      if (signal?.aborted) return { remotePath: null, error: null, cancelled: true }

      onProgress({ confirmedBytes: reply.confirmedBytes, totalBytes: reply.totalBytes })

      if (reply.remotePath) {
        completed = true
        return { remotePath: reply.remotePath, error: null, cancelled: false }
      }

      while (reply.confirmedBytes < file.size) {
        if (signal?.aborted) {
          return { remotePath: null, error: null, cancelled: true }
        }

        const offset = reply.confirmedBytes
        const end = Math.min(offset + TERMINAL_UPLOAD_CHUNK_BYTES, file.size)
        const data = new Uint8Array(await file.slice(offset, end).arrayBuffer())

        if (signal?.aborted) {
          return { remotePath: null, error: null, cancelled: true }
        }

        reply = await this.terminalUploadRequest(
          Server.UploadTerminalChunk,
          encodeTerminalUploadChunk(sessionId, uploadId, offset, data),
          uploadId,
          file.size,
        )

        if (reply.errorCode) return this.replyFailure(reply, sessionId)
        if (signal?.aborted) return { remotePath: null, error: null, cancelled: true }

        if (
          reply.uploadId !== uploadId ||
          reply.totalBytes !== file.size ||
          reply.confirmedBytes !== end
        ) {
          return this.uploadFailure(
            ErrorCodes.UploadFailed,
            'The machine returned inconsistent upload progress.',
            sessionId,
          )
        }

        onProgress({ confirmedBytes: reply.confirmedBytes, totalBytes: reply.totalBytes })
      }

      if (!reply.remotePath) {
        return this.uploadFailure(
          ErrorCodes.UploadFailed,
          'The machine saved the bytes but did not return a file path.',
          sessionId,
        )
      }

      completed = true
      return { remotePath: reply.remotePath, error: null, cancelled: false }
    } finally {
      if (started && !completed) {
        await this.terminalUploadRequest(
          Server.CancelTerminalUpload,
          encodeCancelTerminalUpload(sessionId, uploadId),
          uploadId,
          file.size,
        )
      }
    }
  }

  async resize(sessionId: string, cols: number, rows: number): Promise<HubError | null> {
    return this.request(Server.ResizeTerminal, encodeResizeTerminal(sessionId, cols, rows))
  }

  async interrupt(sessionId: string): Promise<HubError | null> {
    return this.request(Server.InterruptSession, encodeInterruptSession(sessionId))
  }

  async sendChatMessage(sessionId: string, text: string): Promise<HubError | null> {
    return this.request(Server.SendChatMessage, encodeSendChatMessage(sessionId, text))
  }

  /**
   * Stages one browser-selected file on the machine that owns this chat, in ordered
   * chunks the agent acknowledges one at a time.
   *
   * Nothing is pasted anywhere and no path comes back: the staged bytes exist only
   * so that `sendChatPrompt` can turn them into ACP prompt content.
   */
  async uploadChatAttachment(
    sessionId: string,
    attachmentId: string,
    file: File,
    onProgress: (progress: TerminalUploadProgress) => void,
    signal?: AbortSignal,
  ): Promise<ChatAttachmentOutcome> {
    if (file.size === 0) {
      return this.attachmentFailure(ErrorCodes.InvalidRequest, `${file.name} is empty.`, sessionId)
    }

    if (file.size > MAX_CHAT_ATTACHMENT_BYTES) {
      return this.attachmentFailure(
        ErrorCodes.AttachmentTooLarge,
        `Chat attachments are limited to ${formatBytes(MAX_CHAT_ATTACHMENT_BYTES)} each.`,
        sessionId,
      )
    }

    if (signal?.aborted) return { ready: false, error: null, cancelled: true }

    let started = false
    let completed = false

    try {
      let reply = await this.chatAttachmentRequest(
        Server.BeginChatAttachment,
        encodeBeginChatAttachment(sessionId, attachmentId, file.name, file.type, file.size),
        attachmentId,
        file.size,
      )

      if (reply.errorCode) return this.attachmentReplyFailure(reply, sessionId)
      started = true

      if (
        reply.attachmentId !== attachmentId ||
        reply.totalBytes !== file.size ||
        reply.confirmedBytes !== 0
      ) {
        return this.attachmentFailure(
          ErrorCodes.AttachmentFailed,
          'The machine returned inconsistent attachment metadata.',
          sessionId,
        )
      }

      onProgress({ confirmedBytes: 0, totalBytes: file.size })

      while (reply.confirmedBytes < file.size) {
        if (signal?.aborted) return { ready: false, error: null, cancelled: true }

        const offset = reply.confirmedBytes
        const end = Math.min(offset + CHAT_ATTACHMENT_CHUNK_BYTES, file.size)
        const data = new Uint8Array(await file.slice(offset, end).arrayBuffer())

        if (signal?.aborted) return { ready: false, error: null, cancelled: true }

        reply = await this.chatAttachmentRequest(
          Server.UploadChatAttachmentChunk,
          encodeChatAttachmentChunk(sessionId, attachmentId, offset, data),
          attachmentId,
          file.size,
        )

        if (reply.errorCode) return this.attachmentReplyFailure(reply, sessionId)

        if (
          reply.attachmentId !== attachmentId ||
          reply.totalBytes !== file.size ||
          reply.confirmedBytes !== end
        ) {
          return this.attachmentFailure(
            ErrorCodes.AttachmentFailed,
            'The machine returned inconsistent attachment progress.',
            sessionId,
          )
        }

        onProgress({ confirmedBytes: reply.confirmedBytes, totalBytes: reply.totalBytes })
      }

      if (!reply.completed) {
        return this.attachmentFailure(
          ErrorCodes.AttachmentFailed,
          'The machine staged the bytes but did not mark the attachment ready.',
          sessionId,
        )
      }

      completed = true
      return { ready: true, error: null, cancelled: false }
    } finally {
      // A half-staged attachment is deleted rather than left for the sweeper: it is
      // the user's file, and nothing will ever be able to send it.
      if (started && !completed) {
        await this.chatAttachmentRequest(
          Server.CancelChatAttachment,
          encodeCancelChatAttachment(sessionId, attachmentId),
          attachmentId,
          file.size,
        )
      }
    }
  }

  /** Removes a staged attachment, complete or not, and its bytes with it. */
  async cancelChatAttachment(sessionId: string, attachmentId: string): Promise<void> {
    await this.chatAttachmentRequest(
      Server.CancelChatAttachment,
      encodeCancelChatAttachment(sessionId, attachmentId),
      attachmentId,
      0,
    )
  }

  /**
   * Sends optional text plus staged attachments as one prompt.
   *
   * Resolves once the agent has accepted them — validated ownership, capabilities
   * and types, and read the bytes back off disk — not once the turn has finished.
   */
  async sendChatPrompt(
    sessionId: string,
    text: string,
    attachmentIds: string[],
  ): Promise<HubError | null> {
    if (!this.connected) {
      return { code: ErrorCodes.MachineOffline, message: 'Not connected to the hub.', sessionId }
    }

    try {
      const reply = decodeChatPromptReply(
        await this.connection!.invoke(
          Server.SendChatPrompt,
          encodeSendChatPrompt(sessionId, text, attachmentIds),
        ),
      )

      if (reply.accepted) return null

      const problem: HubError = {
        code: reply.errorCode ?? ErrorCodes.InternalError,
        message: reply.errorMessage ?? 'The machine refused that prompt.',
        sessionId,
      }
      this.emit('error', problem)
      return problem
    } catch (error) {
      const problem: HubError = {
        code: ErrorCodes.InternalError,
        message: errorText(error),
        sessionId,
      }
      this.emit('error', problem)
      return problem
    }
  }

  async respondChatPermission(
    sessionId: string,
    requestId: string,
    optionId: string,
  ): Promise<HubError | null> {
    return this.request(
      Server.RespondChatPermission,
      encodeRespondChatPermission(sessionId, requestId, optionId),
    )
  }

  /**
   * Corrects what the agent guessed this session is running.
   *
   * The answer is not applied optimistically. It travels to the agent, which owns
   * session state, and comes back as a `SessionUpdated` for every device — so the
   * moment the button stops looking pressed is the moment the machine agrees.
   */
  async setSessionType(sessionId: string, cliType: CliType): Promise<HubError | null> {
    return this.request(Server.SetSessionType, encodeSetSessionType(sessionId, cliType))
  }

  /**
   * Renames a session for as long as it runs, or clears the name with null.
   *
   * The name lives at the hub rather than on the machine, so this needs the machine
   * id spelled out: it is invoked from the list, where nothing is attached.
   */
  async setSessionName(
    machineId: string,
    sessionId: string,
    name: string | null,
  ): Promise<HubError | null> {
    return this.request(Server.SetSessionName, encodeSetSessionName(machineId, sessionId, name))
  }

  /** Lifts a session above the rest of the list, on every device this user owns. */
  async setSessionPinned(
    machineId: string,
    sessionId: string,
    pinned: boolean,
  ): Promise<HubError | null> {
    return this.request(Server.SetSessionPinned, encodeSetSessionPinned(machineId, sessionId, pinned))
  }

  /**
   * Moves a live session to a different project, or back to General with null.
   *
   * Like a rename, this needs the machine id spelled out: moving is done from the
   * list, where there is no attachment to read it from.
   */
  async setSessionProject(
    machineId: string,
    sessionId: string,
    projectId: string | null,
  ): Promise<HubError | null> {
    return this.request(
      Server.SetSessionProject,
      encodeSetSessionProject(machineId, sessionId, projectId),
    )
  }

  /**
   * Creates a project and returns it directly, in addition to the
   * `ProjectCreated` broadcast every device of this user also receives.
   *
   * The direct return is what a caller uses to chain an icon upload right away,
   * which needs the hub-generated id; the broadcast is what keeps every other
   * screen in sync.
   */
  async createProject(
    name: string,
    description: string | null,
    siteUrl: string | null,
    repoUrl: string | null,
  ): Promise<ProjectResult> {
    return this.projectRequest(
      Server.CreateProject,
      encodeCreateProject(name, description, siteUrl, repoUrl),
    )
  }

  /** Edits a project's fields. Works on the General project too — only deleting it is refused. */
  async updateProject(
    projectId: string,
    name: string,
    description: string | null,
    siteUrl: string | null,
    repoUrl: string | null,
  ): Promise<ProjectResult> {
    return this.projectRequest(
      Server.UpdateProject,
      encodeUpdateProject(projectId, name, description, siteUrl, repoUrl),
    )
  }

  /** Deletes a project. Its live sessions come back reassigned to General via `SessionUpdated`. */
  async deleteProject(projectId: string): Promise<HubError | null> {
    return this.request(Server.DeleteProject, encodeDeleteProject(projectId))
  }

  /** Offers this browser's push subscription, so the hub can reach the phone when nothing is connected. */
  async registerPush(
    registration: PushRegistration,
    preferences: PushPreferences,
  ): Promise<HubError | null> {
    return this.request(
      Server.RegisterPush,
      encodeRegisterPush(
        registration.endpoint,
        registration.keys.p256dh,
        registration.keys.auth,
        !preferences.awaitingInput,
        !preferences.sessionFinished,
        !preferences.announcements,
      ),
    )
  }

  /**
   * Every hub method answers with an error object or nothing, rather than
   * throwing. A hub *exception* reaches a client as an opaque string no interface
   * can branch on, so a refusal that the UI needs to explain travels as data.
   */
  private async request(method: string, argument: unknown): Promise<HubError | null> {
    if (!this.connected) {
      return { code: ErrorCodes.MachineOffline, message: 'Not connected to the hub.', sessionId: null }
    }

    try {
      const answer = await this.connection!.invoke(method, argument)
      if (!answer) return null

      const problem = decodeError(answer)
      this.emit('error', problem)
      return problem
    } catch (error) {
      const problem: HubError = {
        code: ErrorCodes.InternalError,
        message: errorText(error),
        sessionId: null,
      }
      this.emit('error', problem)
      return problem
    }
  }

  private async terminalUploadRequest(
    method: string,
    argument: unknown,
    uploadId: string,
    totalBytes: number,
  ): Promise<TerminalUploadReply> {
    if (!this.connected) {
      return {
        uploadId,
        confirmedBytes: 0,
        totalBytes,
        remotePath: null,
        errorCode: ErrorCodes.MachineOffline,
        errorMessage: 'Not connected to the hub.',
      }
    }

    try {
      return decodeTerminalUploadReply(await this.connection!.invoke(method, argument))
    } catch (error) {
      return {
        uploadId,
        confirmedBytes: 0,
        totalBytes,
        remotePath: null,
        errorCode: ErrorCodes.UploadFailed,
        errorMessage: errorText(error),
      }
    }
  }

  private replyFailure(reply: TerminalUploadReply, sessionId: string): TerminalUploadOutcome {
    return this.uploadFailure(
      reply.errorCode ?? ErrorCodes.UploadFailed,
      reply.errorMessage ?? 'The upload failed.',
      sessionId,
    )
  }

  private uploadFailure(code: string, message: string, sessionId: string): TerminalUploadOutcome {
    return {
      remotePath: null,
      error: { code, message, sessionId },
      cancelled: code === ErrorCodes.UploadCancelled,
    }
  }

  private async chatAttachmentRequest(
    method: string,
    argument: unknown,
    attachmentId: string,
    totalBytes: number,
  ): Promise<ChatAttachmentReply> {
    if (!this.connected) {
      return {
        attachmentId,
        confirmedBytes: 0,
        totalBytes,
        completed: false,
        errorCode: ErrorCodes.MachineOffline,
        errorMessage: 'Not connected to the hub.',
      }
    }

    try {
      return decodeChatAttachmentReply(await this.connection!.invoke(method, argument))
    } catch (error) {
      return {
        attachmentId,
        confirmedBytes: 0,
        totalBytes,
        completed: false,
        errorCode: ErrorCodes.AttachmentFailed,
        errorMessage: errorText(error),
      }
    }
  }

  private attachmentReplyFailure(
    reply: ChatAttachmentReply,
    sessionId: string,
  ): ChatAttachmentOutcome {
    return this.attachmentFailure(
      reply.errorCode ?? ErrorCodes.AttachmentFailed,
      reply.errorMessage ?? 'The attachment could not be staged.',
      sessionId,
    )
  }

  private attachmentFailure(
    code: string,
    message: string,
    sessionId: string,
  ): ChatAttachmentOutcome {
    return {
      ready: false,
      error: { code, message, sessionId },
      cancelled: code === ErrorCodes.AttachmentCancelled,
    }
  }

  /**
   * `CreateProject`/`UpdateProject` answer with `ProjectResult` directly, not the
   * error-or-nothing shape of every other method — the form calling this wants to
   * show a duplicate-name rejection inline, not as a passing global banner.
   */
  private async projectRequest(method: string, argument: unknown): Promise<ProjectResult> {
    if (!this.connected) {
      return { project: null, error: ErrorCodes.MachineOffline }
    }

    try {
      return decodeProjectResult(await this.connection!.invoke(method, argument))
    } catch (error) {
      return { project: null, error: errorText(error) || ErrorCodes.InternalError }
    }
  }

  private emit<K extends keyof RelayEvents>(event: K, ...args: Parameters<RelayEvents[K]>): void {
    const set = this.listeners.get(event)
    if (!set) return

    // A copy, because a handler is allowed to unsubscribe itself — a terminal that
    // detaches on `sessionClosed` does exactly that.
    for (const handler of [...set]) {
      ;(handler as (...a: unknown[]) => void)(...args)
    }
  }
}

function errorText(error: unknown): string {
  if (error instanceof Error) return error.message
  return String(error)
}
