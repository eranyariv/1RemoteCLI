using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Daemon.Ipc;

/// <summary>
/// One framed, duplex conversation over a connected pipe.
/// <para>
/// Writes are serialised: two frames interleaved on a byte-mode pipe would leave
/// the peer permanently desynchronised, since it has no way to resynchronise on a
/// length prefix it has already misread.
/// </para>
/// </summary>
public sealed class AgentPipeConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _disposed;

    public AgentPipeConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>True until the peer disconnects or this side is disposed.</summary>
    public bool IsConnected => _disposed == 0 && _stream is not System.IO.Pipes.PipeStream { IsConnected: false };

    public ValueTask SendAsync<T>(PipeMessageKind kind, T message, CancellationToken cancellationToken = default) =>
        SendAsync(PipeFraming.Encode(kind, message), cancellationToken);

    /// <summary>Sends an envelope that was encoded earlier, preserving frame order.</summary>
    public async ValueTask SendAsync(PipeEnvelope envelope, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PipeFraming.WriteAsync(_stream, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads the next frame, or null when the peer has gone away.</summary>
    public ValueTask<PipeEnvelope?> ReceiveAsync(CancellationToken cancellationToken = default) =>
        PipeFraming.ReadAsync(_stream, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
