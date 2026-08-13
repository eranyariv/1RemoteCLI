using System.Collections.Concurrent;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// The agent's view of every terminal session running on this machine, and the
/// single place that turns a session id into the wrapper that owns it.
/// </summary>
public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Raised whenever a session is added or removed, so a later hub client can
    /// republish the machine's session list without polling.
    /// </summary>
    public event Action? Changed;

    public int Count => _sessions.Count;

    /// <summary>Sessions in the order they started, which is the order a user thinks in.</summary>
    public IReadOnlyList<TerminalSession> Snapshot() =>
        [.. _sessions.Values.OrderBy(s => s.StartedUtc)];

    public TerminalSession Add(
        string program,
        IReadOnlyList<string> args,
        string cwd,
        int cols,
        int rows,
        string? displayName,
        ISessionChannel channel)
    {
        // A GUID, not a counter: ids leave this machine, and a counter would make
        // one machine's session id collide with another's the moment the phone
        // holds both.
        var session = new TerminalSession(
            Guid.NewGuid().ToString("n"),
            program,
            args,
            cwd,
            cols,
            rows,
            displayName,
            channel);

        _sessions[session.SessionId] = session;
        Changed?.Invoke();
        return session;
    }

    public bool Remove(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out _))
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    public bool TryGet(string sessionId, out TerminalSession session) =>
        _sessions.TryGetValue(sessionId, out session!);

    /// <summary>
    /// Looks a session up or throws. Routing failures are deliberately loud: a
    /// keystroke that silently goes nowhere is the worst possible outcome for a
    /// remote terminal, because the user cannot tell it from a slow command.
    /// </summary>
    public TerminalSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out TerminalSession? session)
            ? session
            : throw new UnknownSessionException(sessionId);

    public ValueTask SendInputAsync(
        string sessionId,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default) =>
        Get(sessionId).Channel.SendInputAsync(bytes, cancellationToken);

    public ValueTask ResizeAsync(
        string sessionId,
        int cols,
        int rows,
        CancellationToken cancellationToken = default)
    {
        TerminalSession session = Get(sessionId);
        session.RecordSize(cols, rows);
        return session.Channel.SendResizeAsync(cols, rows, cancellationToken);
    }

    public ValueTask InterruptAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Get(sessionId).Channel.SendInterruptAsync(cancellationToken);
}
