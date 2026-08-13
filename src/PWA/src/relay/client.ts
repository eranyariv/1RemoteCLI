import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack'

import { getAccessToken } from '../auth/msal'
import { Client, PROTOCOL_VERSION, Server } from '../protocol/methods'
import { ErrorCodes, describeError } from '../protocol/errors'
import {
  decodeAwaitingInput,
  decodeError,
  decodeMachineList,
  decodeMachineOffline,
  decodeMachineOnline,
  decodeSessionClosed,
  decodeSessionOpened,
  decodeTerminalOutput,
  encodeAttachSession,
  encodeClientHandshake,
  encodeDetachSession,
  encodeInterruptSession,
  encodeResizeTerminal,
  encodeSendInput,
  type HubError,
  type MachineInfo,
  type SessionInfo,
  type TerminalOutput,
} from '../protocol/wire'
import { resolveHubUrl } from './endpoint'

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
  sessionClosed(machineId: string, sessionId: string, exitCode: number): void
  awaitingInput(machineId: string, sessionId: string, hint: string | null): void
  terminalOutput(output: TerminalOutput): void
  error(error: HubError): void
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
    this.starting = this.connect().finally(() => {
      this.starting = null
    })

    return this.starting
  }

  async stop(): Promise<void> {
    this.stopped = true
    const connection = this.connection
    this.connection = null

    await connection?.stop()
  }

  private async connect(): Promise<void> {
    const token = await getAccessToken()

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
        accessTokenFactory: async () => (await getAccessToken()) ?? '',
      })
      // MessagePack because terminal output is binary, and JSON would base64
      // every frame on the one path that is hot.
      .withHubProtocol(new MessagePackHubProtocol())
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning)
      .build()

    this.attachHandlers(connection)
    this.connection = connection

    try {
      await connection.start()
    } catch (error) {
      this.connection = null
      this.emit('status', 'offline', errorText(error))
      return
    }

    // The handshake is separate from everything else so an incompatible client is
    // turned away before it can issue any other method, rather than half-working.
    const rejection = await connection.invoke(
      Server.ClientHandshake,
      encodeClientHandshake(PROTOCOL_VERSION, CLIENT_VERSION),
    )

    if (rejection) {
      const problem = decodeError(rejection)
      this.emit('status', 'rejected', describeError(problem.code, problem.message))
      this.emit('error', problem)

      // Retrying a refusal would produce a login loop against a hub that has
      // already made its answer clear.
      await this.stop()
      return
    }

    this.emit('status', 'connected')
    await this.refreshMachines()
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

    connection.on(Client.SessionClosed, (n) => {
      const { machineId, sessionId, exitCode } = decodeSessionClosed(n)
      this.emit('sessionClosed', machineId, sessionId, exitCode)
    })

    connection.on(Client.SessionAwaitingInput, (n) => {
      const { machineId, sessionId, hint } = decodeAwaitingInput(n)
      this.emit('awaitingInput', machineId, sessionId, hint)
    })

    connection.on(Client.TerminalOutput, (n) => this.emit('terminalOutput', decodeTerminalOutput(n)))

    connection.on(Client.Error, (n) => {
      const problem = decodeError(n)
      this.emit('error', problem)

      if (problem.code === ErrorCodes.TokenExpired || problem.code === ErrorCodes.IdentityChanged) {
        this.emit('status', 'signed-out', describeError(problem.code))
      }
    })

    connection.onreconnecting(() => this.emit('status', 'reconnecting'))

    connection.onreconnected(async () => {
      this.emit('status', 'connected')
      // The hub's registry is per connection, so a new connection id means it has
      // never heard of us. Re-listing is the normal path, not a repair.
      await this.refreshMachines()
    })

    connection.onclose(() => {
      if (!this.stopped) this.emit('status', 'offline')
    })
  }

  /** Asks for the whole picture. Safe to call at any time. */
  async refreshMachines(): Promise<void> {
    if (!this.connected) return

    const list = await this.connection!.invoke(Server.ListMachines)
    this.emit('machines', decodeMachineList(list))
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

  async resize(sessionId: string, cols: number, rows: number): Promise<HubError | null> {
    return this.request(Server.ResizeTerminal, encodeResizeTerminal(sessionId, cols, rows))
  }

  async interrupt(sessionId: string): Promise<HubError | null> {
    return this.request(Server.InterruptSession, encodeInterruptSession(sessionId))
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
