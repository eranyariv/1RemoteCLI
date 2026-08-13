using System.Runtime.Versioning;
using OneRemoteCli.Daemon.Ipc;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>Thrown when the pipe name is already owned, which almost always means a second agent.</summary>
public sealed class AgentAlreadyRunningException : Exception
{
    public AgentAlreadyRunningException(Exception inner)
        : base("Another 1remote agent is already running for this user.", inner)
    {
    }
}

/// <summary>
/// The long-running <c>1remote agent</c> process: it owns the machine identity, the
/// session registry, and the pipe every wrapper connects to.
/// <para>
/// It is a plain Win32 process, not a Windows service. Sessions are per user and
/// per desktop, and a service runs in neither — it would have to impersonate its way
/// back to the user for no benefit. A normal process also fails visibly, which is
/// what you want from something the user is expected to notice is running.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentHost : IAsyncDisposable
{
    private readonly AgentPipeServer _server;
    private readonly ISessionSink _sink;
    private readonly AwaitingInputMonitor _awaitingInput;
    private readonly Action<string>? _log;
    private readonly List<Task> _connections = [];
    private readonly object _connectionsLock = new();

    public AgentHost(
        MachineIdentity identity,
        SessionRegistry? registry = null,
        ISessionSink? sink = null,
        AgentPipeServer? server = null,
        Action<string>? log = null,
        AwaitingInputOptions? awaitingInput = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Sessions = registry ?? new SessionRegistry();
        _sink = sink ?? NullSessionSink.Instance;
        _server = server ?? new AgentPipeServer();
        _log = log;
        _awaitingInput = new AwaitingInputMonitor(Sessions, _sink, awaitingInput, log: log);
    }

    public MachineIdentity Identity { get; }

    public SessionRegistry Sessions { get; }

    public string PipeName => _server.PipeName;

    /// <summary>
    /// Accepts wrappers until cancelled.
    /// <para>
    /// One faulted connection never takes down the accept loop: a wrapper crashing
    /// is a normal event, and it must not cost every other session on the machine.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Detached rather than interleaved with the accept loop: the sweep is about
        // sessions that already exist, and it must keep running while the loop is
        // blocked waiting for the next wrapper to connect - which is most of the time.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitor = Task.Run(() => _awaitingInput.RunAsync(stopping.Token), CancellationToken.None);

        try
        {
            await AcceptAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }

        await DrainAsync().ConfigureAwait(false);
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AgentPipeConnection connection;

            try
            {
                connection = await _server.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // FirstPipeInstance refused the name. Starting anyway would mean
                // sharing a pipe we do not control, so this is fatal by design.
                throw new AgentAlreadyRunningException(ex);
            }

            var wrapper = new WrapperConnection(connection, Sessions, _sink, _log);
            Track(Task.Run(() => wrapper.RunAsync(cancellationToken), CancellationToken.None));
        }
    }

    private void Track(Task connection)
    {
        lock (_connectionsLock)
        {
            _connections.RemoveAll(t => t.IsCompleted);
            _connections.Add(connection);
        }
    }

    private async Task DrainAsync()
    {
        Task[] pending;
        lock (_connectionsLock)
        {
            pending = [.. _connections];
        }

        // Bounded: a wedged wrapper must not stop the agent from exiting, and the
        // sessions die with their wrappers regardless.
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
    }
}
