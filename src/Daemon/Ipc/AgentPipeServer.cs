using System.IO.Pipes;
using System.Runtime.Versioning;

namespace OneRemoteCli.Daemon.Ipc;

/// <summary>
/// Accepts wrapper connections on the agent's named pipe — one connection per
/// live session, all on the same pipe name.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentPipeServer : IAsyncDisposable
{
    private const int BufferBytes = 64 * 1024;

    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private bool _firstInstanceCreated;

    public AgentPipeServer(string? pipeName = null)
    {
        _pipeName = pipeName ?? AgentPipe.NameForCurrentUser();
    }

    /// <summary>The pipe name clients must use. Exposed so tests can use a unique one.</summary>
    public string PipeName => _pipeName;

    /// <summary>
    /// Waits for the next wrapper and returns its connection.
    /// <para>
    /// The first instance is created with <see cref="PipeOptions.FirstPipeInstance"/>,
    /// so if something else already owns this name the agent fails immediately and
    /// visibly instead of quietly sharing a pipe it does not control. A wrapper that
    /// then connects to the squatter is a problem the user can at least see, because
    /// their agent refused to start.
    /// </para>
    /// </summary>
    public async Task<AgentPipeConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);

        PipeOptions options = PipeOptions.Asynchronous;
        if (!_firstInstanceCreated)
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            BufferBytes,
            BufferBytes,
            AgentPipe.SecurityForCurrentUser());

        _firstInstanceCreated = true;

        try
        {
            await server.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AgentPipeConnection(server);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
