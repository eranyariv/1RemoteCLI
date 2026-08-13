using OneRemoteCli.Daemon.Ipc;
using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// The agent's side of one wrapper connection: reads that wrapper's frames, keeps
/// the registry honest about the session behind it, and carries input back down.
/// <para>
/// One connection carries exactly one session. That is not an arbitrary limit — a
/// wrapper is a single <c>1remote &lt;program&gt;</c> invocation with a single
/// pseudoconsole, so multiplexing would only add a session id to every frame in
/// exchange for nothing.
/// </para>
/// </summary>
public sealed class WrapperConnection : ISessionChannel
{
    private readonly AgentPipeConnection _connection;
    private readonly SessionRegistry _registry;
    private readonly ISessionSink _sink;
    private readonly Action<string>? _log;
    private readonly TimeSpan? _outputTick;

    private TerminalSession? _session;
    private SessionOutputPump? _pump;
    private CancellationTokenSource? _pumping;
    private Task _pumpTask = Task.CompletedTask;

    public WrapperConnection(
        AgentPipeConnection connection,
        SessionRegistry registry,
        ISessionSink? sink = null,
        Action<string>? log = null,
        TimeSpan? outputTick = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sink = sink ?? NullSessionSink.Instance;
        _log = log;
        _outputTick = outputTick;
    }

    /// <summary>The session this wrapper registered, or null before it has.</summary>
    public TerminalSession? Session => _session;

    /// <summary>
    /// Pumps the connection until the wrapper goes away.
    /// <para>
    /// Every exit path — clean close, crashed wrapper, killed console window —
    /// runs through the same cleanup, because from the agent's side they are
    /// indistinguishable and must not be treated differently.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                PipeEnvelope? envelope = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    return;
                }

                if (!await HandleAsync(envelope, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log?.Invoke($"1remote: a session ended abruptly ({ex.Message}).");
        }
        finally
        {
            await EndSessionAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Returns false when the conversation is over.</summary>
    private async Task<bool> HandleAsync(PipeEnvelope envelope, CancellationToken cancellationToken)
    {
        switch (envelope.Kind)
        {
            case PipeMessageKind.SessionOpened:
                await OnSessionOpenedAsync(
                    PipeFraming.DecodePayload<SessionOpenedMessage>(envelope),
                    cancellationToken).ConfigureAwait(false);
                return true;

            case PipeMessageKind.Output:
                if (_session is TerminalSession live)
                {
                    ReadOnlyMemory<byte> bytes = PipeFraming.DecodePayload<OutputMessage>(envelope).Bytes;

                    // Fed to the emulator immediately but sent on the pump's tick. The
                    // screen must always be current — a snapshot is only worth
                    // anything if it reflects what the program has actually done — but
                    // the wire does not need to hear about every read.
                    await live.RunExclusiveAsync(
                        () =>
                        {
                            int safe = live.Screen.Feed(bytes.Span);
                            live.Output.Append(bytes.Span, safe);
                            return ValueTask.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                return true;

            case PipeMessageKind.SessionClosed:
                await EndSessionAsync(PipeFraming.DecodePayload<SessionClosedMessage>(envelope).ExitCode)
                    .ConfigureAwait(false);
                return false;

            default:
                // Either a frame kind this build does not know, or an agent-to-wrapper
                // frame arriving in the wrong direction. Both are skippable: the
                // envelope is self-delimiting, so ignoring one cannot desynchronise
                // the stream, and refusing the whole connection over it would break
                // mixed-version installs for no gain.
                return true;
        }
    }

    private async Task OnSessionOpenedAsync(SessionOpenedMessage message, CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            _log?.Invoke("1remote: a wrapper tried to open a second session on one connection; ignoring it.");
            return;
        }

        _session = _registry.Add(
            message.Program,
            message.Args,
            message.Cwd,
            message.Cols,
            message.Rows,
            message.DisplayName,
            this);

        // Accept first: the wrapper blocks on this before it starts pumping output,
        // so anything the sink does slowly must not sit in front of it.
        await _connection.SendAsync(
            PipeMessageKind.SessionAccepted,
            new SessionAcceptedMessage { SessionId = _session.SessionId },
            cancellationToken).ConfigureAwait(false);

        _pump = new SessionOutputPump(_session, _sink, _outputTick);
        _pumping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pumpTask = _pump.RunAsync(_pumping.Token);

        await _sink.OnOpenedAsync(_session, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EndSessionAsync(int exitCode = -1)
    {
        if (_session is not TerminalSession session)
        {
            return;
        }

        // Stop the tick, then send what is left. The other order would race the pump
        // for the last frame, and losing it means the phone never sees why the program
        // stopped — which is exactly the moment the user is looking.
        if (_pumping is not null)
        {
            await _pumping.CancelAsync().ConfigureAwait(false);
            await _pumpTask.ConfigureAwait(false);
            _pumping.Dispose();
            _pumping = null;
        }

        if (_pump is not null)
        {
            await _pump.FlushAsync().ConfigureAwait(false);
            _pump = null;
        }

        _session = null;
        _registry.Remove(session.SessionId);

        // Not cancellable: this is the last word about a session, and a cancelled
        // token during shutdown must not leave the phone showing a session that no
        // longer exists.
        await _sink.OnClosedAsync(session, exitCode, CancellationToken.None).ConfigureAwait(false);
    }

    public ValueTask SendInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        _connection.SendAsync(PipeMessageKind.Input, new InputMessage { Bytes = bytes.ToArray() }, cancellationToken);

    public ValueTask SendResizeAsync(int cols, int rows, CancellationToken cancellationToken = default) =>
        _connection.SendAsync(
            PipeMessageKind.Resize,
            new ResizeMessage { Cols = cols, Rows = rows },
            cancellationToken);

    public ValueTask SendInterruptAsync(CancellationToken cancellationToken = default) =>
        _connection.SendAsync(PipeMessageKind.Interrupt, new InterruptMessage(), cancellationToken);
}
