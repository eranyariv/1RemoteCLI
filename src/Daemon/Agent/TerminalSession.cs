namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// What the agent can ask a wrapper to do. Kept as an interface so the registry
/// routes to a session without knowing that a named pipe is involved, and so tests
/// can stand in for a wrapper without one.
/// </summary>
public interface ISessionChannel
{
    ValueTask SendInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    ValueTask SendResizeAsync(int cols, int rows, CancellationToken cancellationToken = default);

    ValueTask SendInterruptAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One live terminal session: a wrapper somewhere on this machine with a child
/// running inside its pseudoconsole.
/// <para>
/// A session exists only while its wrapper is connected. It cannot outlive the
/// wrapper, because the wrapper owns the pseudoconsole and the child; there is
/// therefore no persisted session state and no orphan reconciliation to get wrong.
/// </para>
/// </summary>
public sealed class TerminalSession
{
    private int _cols;
    private int _rows;

    public TerminalSession(
        string sessionId,
        string program,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        string? displayName,
        ISessionChannel channel)
    {
        SessionId = sessionId;
        Program = program;
        Args = args;
        Cwd = cwd;
        _cols = cols;
        _rows = rows;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? program : displayName;
        Channel = channel;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public string SessionId { get; }

    public string Program { get; }

    public IReadOnlyList<string> Args { get; }

    public string Cwd { get; }

    public string DisplayName { get; }

    public DateTimeOffset StartedUtc { get; }

    public int Cols => Volatile.Read(ref _cols);

    public int Rows => Volatile.Read(ref _rows);

    internal ISessionChannel Channel { get; }

    internal void RecordSize(int cols, int rows)
    {
        Volatile.Write(ref _cols, cols);
        Volatile.Write(ref _rows, rows);
    }
}

/// <summary>Thrown when a caller names a session the agent does not have.</summary>
public sealed class UnknownSessionException : Exception
{
    public UnknownSessionException(string sessionId)
        : base($"There is no session with id '{sessionId}' on this machine.")
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
}
