using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Threading.Channels;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.Protocol.Hub;
using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Daemon.Ipc;

/// <summary>
/// The wrapper's side of the agent pipe.
/// <para>
/// Once a session has been registered, this reconnects on its own if the pipe is
/// lost — the agent restarting for an update is the ordinary case, not an error. It
/// keeps the local pseudoconsole running throughout: <see cref="Commands"/> stays
/// open and <see cref="SendOutputAsync"/> keeps accepting bytes for as long as it
/// takes to find the agent again, with no bound on how long that is. Before a
/// session exists — the initial connect, and the one-shot channel a shortcut
/// launcher uses for <see cref="CreateChatAsync"/> — a lost pipe is exactly as fatal
/// as it always was, because there is nothing yet worth staying up for.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentPipeClient : IAgentConnection
{
    /// <summary>
    /// How many frames may be waiting for the agent before the link is declared lost.
    /// <para>
    /// Terminal output is bursty, so a little slack absorbs a redraw. Beyond that
    /// there are only bad options, and this is the least bad: stalling would freeze
    /// the desk terminal on a remote link the user may not even be using, and
    /// dropping frames silently would leave the phone rendering a screen that never
    /// existed. Instead sharing stops and the user is told. A reconnect attempt in
    /// progress does not raise this bar: it is the same slack, just spent waiting for
    /// the agent to come back instead of waiting for it to keep up.
    /// </para>
    /// </summary>
    private const int OutboundQueueDepth = 512;

    /// <summary>Screen dimensions before a session exists to size it from.</summary>
    private const int PlaceholderCols = 80;

    private const int PlaceholderRows = 24;
    private const int MaxUnsafeScreenTailBytes = OutputCoalescer.MaxFrameBytes * 10;

    private static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly TimeSpan _reconnectDelay;
    private readonly Channel<AgentCommand> _commands = Channel.CreateUnbounded<AgentCommand>();
    private readonly Channel<PipeEnvelope> _outbound;
    private readonly TaskCompletionSource<string> _accepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ChatCreatedMessage> _chatCreated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// What a terminal would be showing right now, kept up to date locally so a
    /// reconnect has something to hand the fresh agent besides silence.
    /// <para>
    /// Never read by anything on this side except to serialise it: the desk terminal
    /// gets its bytes straight from the pseudoconsole, same as always. This mirror
    /// exists purely to reseed the far side.
    /// </para>
    /// </summary>
    private readonly SessionScreen _screen = new(PlaceholderCols, PlaceholderRows);

    /// <summary>
    /// Guards <see cref="_screen"/> and <see cref="_outbound"/> together, so a
    /// reconnect can drain the queue and take a snapshot as one indivisible step.
    /// <para>
    /// Without it, output fed to the screen a moment after the snapshot was taken but
    /// enqueued a moment before the drain — or the reverse — would be either missing
    /// from the resumed session or present twice. Every call in this class that
    /// touches both takes this lock around the whole thing; nothing here ever awaits
    /// while holding it, so a plain <see cref="object"/> lock is enough.
    /// </para>
    /// </summary>
    private readonly object _ioGate = new();
    private readonly List<byte> _unsafeScreenTail = [];
    private bool _discardingOversizedUnsafeTail;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    /// <summary>The connection currently in use. Replaced whole after a reconnect.</summary>
    private volatile AgentPipeConnection _connection;

    private SessionStartInfo? _sessionInfo;
    private volatile string? _lastSessionId;
    private volatile bool _sessionEstablished;
    private volatile bool _reconnecting;

    /// <summary>
    /// Set once <see cref="CloseSessionAsync"/> is called, so the agent closing its
    /// end of the pipe right afterwards — which is the ordinary, expected way a
    /// session ends — is not mistaken for the agent going away mid-session.
    /// </summary>
    private volatile bool _closing;
    private volatile Exception? _fault;
    private int _disposed;

    private AgentPipeClient(string pipeName, AgentPipeConnection connection, TimeSpan? reconnectDelay = null)
    {
        _pipeName = pipeName;
        _connection = connection;
        _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        _outbound = Channel.CreateBounded<PipeEnvelope>(new BoundedChannelOptions(OutboundQueueDepth)
        {
            // Wait, not DropWrite: DropWrite makes TryWrite report success even for
            // the frame it just silently discarded, which would turn the overflow
            // check below into dead code. Wait is what makes TryWrite actually
            // return false once the channel is full, without ever making the caller
            // block — nothing here calls WriteAsync, only TryWrite.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        _pump = Task.Run(() => PumpForeverAsync(_stopping.Token));
    }

    public ChannelReader<AgentCommand> Commands => _commands.Reader;

    /// <summary>
    /// Connects to the agent, retrying briefly.
    /// <para>
    /// The retry exists because at logon the wrapper and the agent race: a user's
    /// startup terminal can easily beat the agent's scheduled task. Retrying turns a
    /// confusing early-morning failure into a short pause.
    /// </para>
    /// </summary>
    /// <param name="reconnectDelay">
    /// How long to wait between attempts once a session is lost and being reconnected
    /// — a separate, unbounded concern from <paramref name="retryWindow"/>, which only
    /// covers the very first connect. Exposed for tests; production leaves it at the
    /// default.
    /// </param>
    public static async Task<AgentPipeClient> ConnectAsync(
        string? pipeName = null,
        TimeSpan? retryWindow = null,
        TimeSpan? reconnectDelay = null,
        CancellationToken cancellationToken = default)
    {
        string name = pipeName ?? AgentPipe.NameForCurrentUser();
        DateTime deadline = DateTime.UtcNow + (retryWindow ?? AgentPipe.ConnectRetryWindow);

        while (true)
        {
            var stream = new NamedPipeClientStream(
                ".",
                name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await stream.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                return new AgentPipeClient(name, new AgentPipeConnection(stream), reconnectDelay);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await stream.DisposeAsync().ConfigureAwait(false);

                if (DateTime.UtcNow >= deadline)
                {
                    throw new AgentUnavailableException(AgentConnector.NotRunningMessage, ex);
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task<string> OpenSessionAsync(SessionStartInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        _sessionInfo = info;
        _screen.Resize(info.Cols, info.Rows);

        EnqueueOrThrow(
            PipeMessageKind.SessionOpened,
            new SessionOpenedMessage
            {
                Program = info.Program,
                Args = [.. info.Args],
                Cwd = info.Cwd,
                Cols = info.Cols,
                Rows = info.Rows,
                DisplayName = info.DisplayName,
                CliType = info.CliType,
                SupportsReconnect = true,
            });

        string sessionId = await _accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Only from here on does a lost pipe mean "reconnect" rather than "fail": a
        // session now exists for the agent to hand back, and a wrapper this new
        // always tells it so.
        _lastSessionId = sessionId;
        _sessionEstablished = true;
        return sessionId;
    }

    /// <summary>Asks the running agent to create one ACP chat.</summary>
    public async Task<ChatCreatedMessage> CreateChatAsync(
        string cwd,
        string? displayName,
        CliType cliType,
        CancellationToken cancellationToken = default)
    {
        EnqueueOrThrow(
            PipeMessageKind.ChatCreate,
            new ChatCreateMessage
            {
                Cwd = cwd,
                DisplayName = displayName,
                CliType = cliType,
            });

        return await _chatCreated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendOutputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        lock (_ioGate)
        {
            // Fed to the mirror before it is queued for the wire: whatever this call
            // enqueues, successfully delivered or later discarded by a reconnect, is
            // by then already part of the picture a reconnect would replay instead.
            int safeOffset = _screen.Feed(bytes.Span);
            if (safeOffset >= 0)
            {
                _discardingOversizedUnsafeTail = false;
                _unsafeScreenTail.Clear();
                _unsafeScreenTail.AddRange(bytes.Span[safeOffset..].ToArray());
            }
            else if (!_discardingOversizedUnsafeTail)
            {
                _unsafeScreenTail.AddRange(bytes.ToArray());
                if (_unsafeScreenTail.Count > MaxUnsafeScreenTailBytes)
                {
                    // An unterminated OSC/DCS payload can otherwise grow forever and
                    // eventually exceed the pipe frame limit during reconnect. Drop
                    // that entire non-ground sequence until the parser finds its end;
                    // the screen snapshot still represents everything safely parsed
                    // before it.
                    _unsafeScreenTail.Clear();
                    _discardingOversizedUnsafeTail = true;
                }
            }

            // During a reconnect the screen mirror is the queue. Keeping every raw
            // frame as well would let a busy terminal exhaust the bounded channel
            // while no writer exists, only for those same frames to be discarded
            // after the snapshot is taken.
            if (_reconnecting)
            {
                return ValueTask.CompletedTask;
            }

            EnqueueOrThrow(PipeMessageKind.Output, new OutputMessage { Bytes = bytes.ToArray() });
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseSessionAsync(int exitCode, CancellationToken cancellationToken)
    {
        lock (_ioGate)
        {
            _closing = true;

            // There is no agent to receive a close while reconnecting. Stop the
            // retry; disposal will close any candidate that won the race, and its
            // server side will retire a briefly reopened session on EOF.
            if (_reconnecting)
            {
                _stopping.Cancel();
                return ValueTask.CompletedTask;
            }

            EnqueueOrThrow(PipeMessageKind.SessionClosed, new SessionClosedMessage { ExitCode = exitCode });
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Queues a frame, or throws if that is no longer possible. Never awaits.</summary>
    private void EnqueueOrThrow<T>(PipeMessageKind kind, T message)
    {
        if (_fault is Exception fault)
        {
            throw new IOException("The agent connection was lost.", fault);
        }

        // Never awaits the pipe: queueing keeps the caller, and therefore the desk
        // terminal, moving at the child's pace rather than the agent's — including
        // while a reconnect is under way, since the queue is exactly the same one.
        if (!_outbound.Writer.TryWrite(PipeFraming.Encode(kind, message)))
        {
            Fault(new IOException($"The agent is not keeping up; more than {OutboundQueueDepth} frames are queued."));
            throw new IOException("The agent is not keeping up.", _fault);
        }
    }

    /// <summary>
    /// Owns the connection for as long as this client lives: runs it, notices when it
    /// is gone, and — once a session exists to preserve — replaces it and goes again.
    /// <para>
    /// This is the only place that ever dials or swaps <see cref="_connection"/>, so
    /// there is exactly one reconnect in flight at a time by construction rather than
    /// by locking around it.
    /// </para>
    /// </summary>
    private async Task PumpForeverAsync(CancellationToken cancellationToken)
    {
        AgentPipeConnection connection = _connection;

        while (true)
        {
            using CancellationTokenSource generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            Task<bool> reading = RunReadAsync(connection, generation.Token);
            Task<bool> writing = RunWriteAsync(connection, generation.Token);

            await Task.WhenAny(reading, writing).ConfigureAwait(false);

            // Whichever of the two ended first ends the round for both: reading has
            // nothing left to read once writing can no longer reach the agent, and
            // writing has nothing left to send once reading says the agent is gone.
            await generation.CancelAsync().ConfigureAwait(false);

            bool readLostTransport = await SwallowAsync(reading).ConfigureAwait(false);
            bool writeLostTransport = await SwallowAsync(writing).ConfigureAwait(false);

            await DisposeQuietlyAsync(connection).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!readLostTransport && !writeLostTransport)
            {
                // Neither saw a transport problem, so whatever ended this round already
                // handled itself (a decode failure, or the outbound queue overflowing) —
                // there is nothing left here to reconnect for.
                return;
            }

            if (_closing)
            {
                // The wrapper told the agent the session was over; the agent closing
                // its end right afterwards is that working as intended, not a loss.
                return;
            }

            if (!_sessionEstablished)
            {
                // No session has ever been accepted on this client: either the very
                // first handshake never finished, or this is a one-shot ChatCreate
                // connection. Both keep the old, bounded, loud behaviour.
                Fault(new IOException("The agent closed the connection."));
                return;
            }

            _reconnecting = true;

            if (_closing)
            {
                return;
            }

            try
            {
                connection = await ReconnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposed while reconnecting.
                return;
            }
            catch (Exception ex)
            {
                Fault(ex);
                return;
            }
            finally
            {
                _reconnecting = false;
            }

            _connection = connection;
        }
    }

    /// <summary>Dials, reopens the session, and reseeds the fresh agent's screen.</summary>
    private async Task<AgentPipeConnection> ReconnectAsync(CancellationToken cancellationToken)
    {
        (AgentPipeConnection reconnected, string sessionId) =
            await DialAndReopenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _lastSessionId = sessionId;

            lock (_ioGate)
            {
                // Everything still queued here was addressed to the connection that
                // just died. Output is already part of the screen captured below, so
                // sending it after the snapshot would repaint the same bytes twice.
                while (_outbound.Reader.TryPeek(out PipeEnvelope? queued) &&
                       queued.Kind == PipeMessageKind.Output)
                {
                    _outbound.Reader.TryRead(out _);
                }

                // Queue the reset snapshot before allowing new output into the
                // channel. The next writer therefore sees snapshot, then every byte
                // produced after it, with no gap between capture and resumption.
                EnqueueOrThrow(
                    PipeMessageKind.Output,
                    new OutputMessage { Bytes = _screen.Snapshot() });
                if (_unsafeScreenTail.Count > 0)
                {
                    EnqueueOrThrow(
                        PipeMessageKind.Output,
                        new OutputMessage { Bytes = [.. _unsafeScreenTail] });
                }
                _reconnecting = false;
            }

            return reconnected;
        }
        catch
        {
            await DisposeQuietlyAsync(reconnected).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Dials the agent's pipe and reopens the session on it, retrying with a growing
    /// delay until it succeeds or this client is disposed.
    /// <para>
    /// No deadline, unlike <see cref="ConnectAsync"/>: giving up here would strand a
    /// session that is otherwise perfectly fine, purely because the agent took longer
    /// than some arbitrary window to come back up.
    /// </para>
    /// </summary>
    private async Task<(AgentPipeConnection Connection, string SessionId)> DialAndReopenAsync(
        CancellationToken cancellationToken)
    {
        TimeSpan delay = _reconnectDelay;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            AgentPipeConnection? candidate = null;

            try
            {
                // The old agent can dispose its end of the pipe well before a
                // replacement is listening on the same name; that shows up here the
                // same way a not-yet-started agent does at initial connect, just with
                // no deadline on how long it is tolerated.
                await stream.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
                candidate = new AgentPipeConnection(stream);

                string sessionId = await ReopenSessionAsync(candidate, cancellationToken).ConfigureAwait(false);
                return (candidate, sessionId);
            }
            catch (OperationCanceledException)
            {
                if (candidate is null)
                {
                    await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                }
                else
                {
                    await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
                }

                throw;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
            {
                if (candidate is null)
                {
                    await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                }
                else
                {
                    await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(MaxReconnectDelay.TotalMilliseconds, delay.TotalMilliseconds * 1.5));
            }
            catch
            {
                if (candidate is null)
                {
                    await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                }
                else
                {
                    await DisposeQuietlyAsync(candidate).ConfigureAwait(false);
                }

                throw;
            }
        }
    }

    /// <summary>Sends the reconnect handshake and waits for its acceptance, on a connection nobody else reads yet.</summary>
    private async Task<string> ReopenSessionAsync(AgentPipeConnection connection, CancellationToken cancellationToken)
    {
        SessionStartInfo info = _sessionInfo!;

        await connection.SendAsync(
            PipeMessageKind.SessionOpened,
            new SessionOpenedMessage
            {
                Program = info.Program,
                Args = [.. info.Args],
                Cwd = info.Cwd,

                // The size as last resized by the phone, not the size the session
                // originally opened with: the fresh agent should see what this
                // session currently looks like.
                Cols = _screen.Cols,
                Rows = _screen.Rows,
                DisplayName = info.DisplayName,
                CliType = info.CliType,
                PriorSessionId = _lastSessionId,
                SupportsReconnect = true,
            },
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            PipeEnvelope? envelope = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (envelope is null)
            {
                throw new IOException("The agent closed the connection while the session was reopening.");
            }

            if (envelope.Kind == PipeMessageKind.SessionAccepted)
            {
                return PipeFraming.DecodePayload<SessionAcceptedMessage>(envelope).SessionId;
            }

            // Nothing else is valid before acceptance; skipping it costs nothing and
            // keeps this resilient to whatever a future agent version might send.
        }
    }

    /// <summary>Sends everything queued, on the given connection, until it or the token gives out.</summary>
    private async Task<bool> RunWriteAsync(AgentPipeConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                PipeEnvelope frame;

                try
                {
                    frame = await _outbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (ChannelClosedException)
                {
                    // Completed cleanly (disposal) or with a fault already recorded
                    // elsewhere; either way there is nothing left to reconnect for.
                    return false;
                }

                try
                {
                    await connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Fault(ex);
            return false;
        }
    }

    /// <summary>Reads and dispatches frames from the given connection until it or the token gives out.</summary>
    private async Task<bool> RunReadAsync(AgentPipeConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                PipeEnvelope? envelope;

                try
                {
                    envelope = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return true;
                }

                if (envelope is null)
                {
                    // The agent closed the pipe or exited. The local session is not
                    // over; the supervisor decides whether that means reconnecting.
                    return true;
                }

                switch (envelope.Kind)
                {
                    case PipeMessageKind.SessionAccepted:
                        _accepted.TrySetResult(
                            PipeFraming.DecodePayload<SessionAcceptedMessage>(envelope).SessionId);
                        break;

                    case PipeMessageKind.Input:
                        _commands.Writer.TryWrite(
                            new AgentCommand.Input(PipeFraming.DecodePayload<InputMessage>(envelope).Bytes));
                        break;

                    case PipeMessageKind.Resize:
                        var resize = PipeFraming.DecodePayload<ResizeMessage>(envelope);
                        _commands.Writer.TryWrite(new AgentCommand.Resize(resize.Cols, resize.Rows));

                        // Kept in step so a snapshot taken later reflects the size the
                        // phone actually set, not the size the session opened with.
                        _screen.Resize(resize.Cols, resize.Rows);
                        break;

                    case PipeMessageKind.Interrupt:
                        _commands.Writer.TryWrite(new AgentCommand.Interrupt());
                        break;

                    case PipeMessageKind.ChatCreated:
                        _chatCreated.TrySetResult(
                            PipeFraming.DecodePayload<ChatCreatedMessage>(envelope));
                        break;

                    default:
                        // A frame kind this build does not know about. The envelope
                        // carries its own length, so skipping it keeps the stream in
                        // sync and lets an older wrapper talk to a newer agent.
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // Something about handling a frame itself failed (a payload that would
            // not decode, say). Retrying the same bytes on a new connection would
            // only fail identically, so this is fatal rather than a reconnect cause.
            Fault(ex);
            return false;
        }
    }

    private static async Task<bool> SwallowAsync(Task<bool> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            // RunReadAsync/RunWriteAsync handle their own failures and always
            // complete normally; this is only a backstop against a mistake in one of
            // them taking down the whole client instead of just this round.
            return false;
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Already broken; disposing it is cleanup, not news.
        }
    }

    private void Fault(Exception ex)
    {
        _fault ??= ex;
        _accepted.TrySetException(ex);
        _chatCreated.TrySetException(ex);
        _commands.Writer.TryComplete(ex);
        _outbound.Writer.TryComplete(ex);

        // Faulted is permanent. A reconnect attempt in flight must stop immediately
        // rather than eventually handing back a connection nobody can use any more.
        _stopping.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Let anything already queued reach the agent before the pipe goes away,
        // so a final SessionClosed is not lost on the way out.
        _outbound.Writer.TryComplete();
        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        // Stops a reconnect attempt in progress as well as the current connection's
        // read and write: everything in the pump keys off this same token.
        await _stopping.CancelAsync().ConfigureAwait(false);

        await _connection.DisposeAsync().ConfigureAwait(false);

        await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        _commands.Writer.TryComplete();
        _stopping.Dispose();
    }
}
