using OneRemoteCli.Protocol.Hub;

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
    /// <summary>
    /// Serialises everything that puts bytes on the wire for this session.
    /// <para>
    /// Without it, a snapshot taken while output was in flight would race the delta
    /// carrying that same output: whichever won, the client would end up either
    /// missing a chunk or applying it twice. Both look like corruption on screen, and
    /// both would be intermittent and dependent on when someone happened to attach —
    /// the worst possible bug to be handed. The gate makes "feed the emulator and
    /// forward the bytes" and "take a snapshot and send it" each indivisible.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _outputGate = new(1, 1);

    private int _cols;
    private int _rows;
    private long _outputCount;
    private long _lastOutputTicks;
    private int _cliType;

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
        Screen = new SessionScreen(cols, rows);
        StartedUtc = DateTimeOffset.UtcNow;
        _lastOutputTicks = StartedUtc.UtcTicks;
        _cliType = (int)CliTypes.Detect(program, args);
    }

    public string SessionId { get; }

    public string Program { get; }

    public IReadOnlyList<string> Args { get; }

    public string Cwd { get; }

    public string DisplayName { get; }

    public DateTimeOffset StartedUtc { get; }

    /// <summary>
    /// Which CLI this session is hosting.
    /// <para>
    /// Detected from the command line when the session opens, and settable afterwards
    /// because detection is a guess and the person looking at the screen is not. The
    /// setter is what the phone's picker ultimately reaches; it is deliberately not
    /// guarded by the output gate, since nothing here orders against terminal bytes.
    /// </para>
    /// </summary>
    public CliType CliType
    {
        get => (CliType)Volatile.Read(ref _cliType);
        set => Volatile.Write(ref _cliType, (int)value);
    }

    public int Cols => Volatile.Read(ref _cols);

    public int Rows => Volatile.Read(ref _rows);

    /// <summary>What a terminal would be showing for this session right now.</summary>
    public SessionScreen Screen { get; }

    /// <summary>
    /// When the program last wrote anything.
    /// <para>
    /// Seeded with the start time rather than left at zero, so a session that has
    /// never produced a byte is measured from when it began instead of appearing to
    /// have been silent since the epoch.
    /// </para>
    /// </summary>
    public DateTimeOffset LastOutputUtc => new(Volatile.Read(ref _lastOutputTicks), TimeSpan.Zero);

    /// <summary>
    /// How many times the program has written.
    /// <para>
    /// A monotonic counter, not a timestamp, because it is used as a token: something
    /// that observed the session at one moment can tell whether anything has happened
    /// since without holding a lock or comparing clocks. The awaiting-input monitor
    /// uses it to re-arm itself.
    /// </para>
    /// </summary>
    public long OutputCount => Interlocked.Read(ref _outputCount);

    /// <summary>Output waiting to be framed and sent.</summary>
    public OutputCoalescer Output { get; } = new();

    /// <summary>Sequence numbering and the recent frames, for resume.</summary>
    public SessionTail Tail { get; } = new();

    internal ISessionChannel Channel { get; }

    /// <summary>
    /// Runs <paramref name="action"/> with nothing else sending for this session.
    /// <para>
    /// Callers hold this across the network send deliberately. The alternative — take
    /// the snapshot under the gate, send it outside — reintroduces exactly the race
    /// the gate exists to close, because ordering on the wire is what matters, not
    /// ordering in memory. The cost is a per-session queue of one, which is also the
    /// backpressure a single session ought to have.
    /// </para>
    /// </summary>
    public async ValueTask RunExclusiveAsync(
        Func<ValueTask> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _outputGate.Release();
        }
    }

    internal void RecordSize(int cols, int rows)
    {
        Volatile.Write(ref _cols, cols);
        Volatile.Write(ref _rows, rows);
        Screen.Resize(cols, rows);
    }

    /// <summary>
    /// Records that the program wrote something.
    /// <para>
    /// Called where the bytes reach the screen rather than where they reach the wire,
    /// because a session that is producing output while the hub is unreachable is
    /// still not waiting for anybody.
    /// </para>
    /// </summary>
    internal void NoteOutput(DateTimeOffset now)
    {
        Volatile.Write(ref _lastOutputTicks, now.UtcTicks);
        Interlocked.Increment(ref _outputCount);
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
