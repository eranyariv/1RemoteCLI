using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Threading.Channels;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Daemon.Ipc;

/// <summary>
/// The wrapper's side of the agent pipe.
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
    /// existed. Instead sharing stops and the user is told.
    /// </para>
    /// </summary>
    private const int OutboundQueueDepth = 512;

    private readonly AgentPipeConnection _connection;
    private readonly Channel<AgentCommand> _commands = Channel.CreateUnbounded<AgentCommand>();
    private readonly Channel<PipeEnvelope> _outbound;
    private readonly TaskCompletionSource<string> _accepted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _readLoop;
    private readonly Task _writeLoop;

    private volatile Exception? _fault;
    private int _disposed;

    private AgentPipeClient(AgentPipeConnection connection)
    {
        _connection = connection;
        _outbound = Channel.CreateBounded<PipeEnvelope>(new BoundedChannelOptions(OutboundQueueDepth)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        _readLoop = Task.Run(() => ReadLoopAsync(_stopping.Token));
        _writeLoop = Task.Run(() => WriteLoopAsync(_stopping.Token));
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
    public static async Task<AgentPipeClient> ConnectAsync(
        string? pipeName = null,
        TimeSpan? retryWindow = null,
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
                return new AgentPipeClient(new AgentPipeConnection(stream));
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

        await SendAsync(
            PipeMessageKind.SessionOpened,
            new SessionOpenedMessage
            {
                Program = info.Program,
                Args = [.. info.Args],
                Cwd = info.Cwd,
                Cols = info.Cols,
                Rows = info.Rows,
                DisplayName = info.DisplayName,
            },
            cancellationToken).ConfigureAwait(false);

        return await _accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendOutputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        SendAsync(PipeMessageKind.Output, new OutputMessage { Bytes = bytes.ToArray() }, cancellationToken);

    public ValueTask CloseSessionAsync(int exitCode, CancellationToken cancellationToken) =>
        SendAsync(PipeMessageKind.SessionClosed, new SessionClosedMessage { ExitCode = exitCode }, cancellationToken);

    private ValueTask SendAsync<T>(PipeMessageKind kind, T message, CancellationToken cancellationToken)
    {
        if (_fault is Exception fault)
        {
            throw new IOException("The agent connection was lost.", fault);
        }

        // Never awaits the pipe: queueing keeps the caller, and therefore the desk
        // terminal, moving at the child's pace rather than the agent's.
        if (!_outbound.Writer.TryWrite(PipeFraming.Encode(kind, message)))
        {
            Fault(new IOException($"The agent is not keeping up; more than {OutboundQueueDepth} frames are queued."));
            throw new IOException("The agent is not keeping up.", _fault);
        }

        return ValueTask.CompletedTask;
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (PipeEnvelope frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                PipeEnvelope? envelope = await _connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    // The agent closed the pipe or exited. Sharing is over; the local
                    // session is not, so this ends the command stream rather than the
                    // process.
                    Fault(new IOException("The agent closed the connection."));
                    return;
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
                        break;

                    case PipeMessageKind.Interrupt:
                        _commands.Writer.TryWrite(new AgentCommand.Interrupt());
                        break;

                    default:
                        // A frame kind this build does not know about. The envelope
                        // carries its own length, so skipping it keeps the stream in
                        // sync and lets an older wrapper talk to a newer agent.
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }

    private void Fault(Exception ex)
    {
        _fault ??= ex;
        _accepted.TrySetException(ex);
        _commands.Writer.TryComplete(ex);
        _outbound.Writer.TryComplete(ex);
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
        await Task.WhenAny(_writeLoop, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);

        await Task.WhenAny(Task.WhenAll(_readLoop, _writeLoop), Task.Delay(TimeSpan.FromSeconds(2)))
            .ConfigureAwait(false);

        _commands.Writer.TryComplete();
        _stopping.Dispose();
    }
}
