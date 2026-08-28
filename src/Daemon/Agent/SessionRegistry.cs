using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Agent;

/// <summary>
/// The agent's view of every terminal session running on this machine, and the
/// single place that turns a session id into the wrapper that owns it.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);
    private readonly TerminalUploadStore _uploads;
    private readonly object _lifecycleGate = new();

    public SessionRegistry(
        string? uploadRoot = null,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        _uploads = new TerminalUploadStore(uploadRoot, time, logger);
    }

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
        ISessionChannel channel,
        CliType? cliType = null,
        string? priorSessionId = null,
        bool supportsReconnect = false)
    {
        // A reconnecting wrapper asks to keep its old id so the phone's open tab and
        // this machine's own session list stay pointed at the same session across the
        // agent restart that caused the gap. Reusing it is safe only when nothing
        // already holds it — a fresh registry after a real restart never does, but a
        // wrapper reconnecting within one agent's lifetime could race a second wrapper
        // that was handed the same id some other way, so this still checks rather than
        // assuming.
        string sessionId = !string.IsNullOrEmpty(priorSessionId) && !_sessions.ContainsKey(priorSessionId)
            ? priorSessionId
            // A GUID, not a counter: ids leave this machine, and a counter would make
            // one machine's session id collide with another's the moment the phone
            // holds both.
            : Guid.NewGuid().ToString("n");

        var session = new TerminalSession(
            sessionId,
            program,
            args,
            cwd,
            cols,
            rows,
            displayName,
            channel,
            cliType,
            supportsReconnect,
            forceSnapshots: priorSessionId is not null);

        // TryAdd rather than the indexer: if the id was raced after the check above,
        // a second session silently overwriting the first would leave one wrapper's
        // input routed nowhere. Falling back to a fresh id keeps this method total
        // instead of ever failing a session open.
        if (!_sessions.TryAdd(session.SessionId, session))
        {
            session = new TerminalSession(
                Guid.NewGuid().ToString("n"),
                program,
                args,
                cwd,
                cols,
                rows,
                displayName,
                channel,
                cliType,
                supportsReconnect,
                forceSnapshots: false);

            _sessions[session.SessionId] = session;
        }

        Changed?.Invoke();
        return session;
    }

    public bool Remove(string sessionId, bool preserveUploads = false)
    {
        lock (_lifecycleGate)
        {
            if (!_sessions.TryRemove(sessionId, out _))
            {
                return false;
            }

            if (!preserveUploads)
            {
                _uploads.RemoveSession(sessionId);
            }
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

    public TerminalUploadReply BeginUpload(BeginTerminalUploadNotification request)
    {
        lock (_lifecycleGate)
        {
            return _uploads.Begin(request, Get(request.SessionId));
        }
    }

    public TerminalUploadReply AppendUpload(TerminalUploadChunkNotification request)
    {
        Get(request.SessionId);
        return _uploads.Append(request);
    }

    public TerminalUploadReply CancelUpload(CancelTerminalUploadNotification request)
    {
        Get(request.SessionId);
        return _uploads.Cancel(request);
    }

    public void CancelUploadsForClient(string sessionId, string clientConnectionId) =>
        _uploads.CancelForClient(sessionId, clientConnectionId);

    public void CancelActiveUploads() => _uploads.CancelActive();

    public void Dispose() => _uploads.Dispose();
}
