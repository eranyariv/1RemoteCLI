using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Relay;

/// <summary>
/// How much a client may fall behind before the hub stops trying to catch it up.
/// </summary>
public sealed class OutboundLimits
{
    /// <summary>
    /// The most that may be waiting for one client.
    /// <para>
    /// A screen is a few kilobytes, so this is dozens of screens: far more than anyone
    /// will ever read, and small enough that a hundred stalled phones cost megabytes
    /// rather than gigabytes.
    /// </para>
    /// </summary>
    public int MaxQueuedBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// The longest a frame may sit unsent before the backlog is declared worthless.
    /// <para>
    /// Two seconds is roughly where a terminal stops feeling like a terminal. Past it,
    /// what is queued is history rather than a view, and history is exactly what this
    /// product does not promise to deliver.
    /// </para>
    /// </summary>
    public TimeSpan MaxQueuedAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The least time between two forced repaints of the same client.
    /// <para>
    /// A persistently slow link would otherwise overflow, be sent a snapshot, overflow
    /// on the snapshot, and be sent another — spending the whole link on repaints that
    /// never arrive. With a floor, a client that cannot keep up settles into a slow
    /// cadence of complete screens, which is the most useful thing a bad link can
    /// deliver.
    /// </para>
    /// </summary>
    public TimeSpan MinimumRepaintInterval { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Sends terminal output to clients without letting any one of them hold up the rest.
/// <para>
/// The hub used to await the fan-out inside the agent's invocation. SignalR processes
/// one invocation per connection at a time, so a single phone whose transport buffer was
/// full stopped output for every session on that machine — the worst possible coupling,
/// since the phone with the bad link is the one least able to signal that it is the
/// problem.
/// </para>
/// <para>
/// Each client therefore gets its own queue and its own pump. When a queue grows past
/// what a live view could justify, it is thrown away in favour of a fresh screen from
/// the agent. That trade is only available because of the screen-state model: with no
/// scrollback to preserve, a snapshot already contains everything the discarded frames
/// would have produced, so a client on a bad link converges on the current screen
/// instead of falling ever further behind replaying history nobody will read.
/// </para>
/// </summary>
public sealed class OutboundFanout
{
    private readonly IHubContext<RelayHub> _hub;
    private readonly ILogger<OutboundFanout> _log;
    private readonly OutboundLimits _limits;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, ClientQueue> _queues = new(StringComparer.Ordinal);

    public OutboundFanout(
        IHubContext<RelayHub> hub,
        ILogger<OutboundFanout> log,
        OutboundLimits? limits = null,
        TimeProvider? time = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _limits = limits ?? new OutboundLimits();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many times a backlog has been thrown away, for the metrics in 5.4.</summary>
    public long Repaints => Interlocked.Read(ref _repaints);

    private long _repaints;

    /// <summary>
    /// Queues a frame for everyone watching, and returns immediately.
    /// <para>
    /// Returning without waiting is the point: the agent's connection must not be held
    /// open by the slowest reader on it.
    /// </para>
    /// </summary>
    public void Publish(
        string agentConnectionId,
        TerminalOutputNotification frame,
        IReadOnlyList<string> watchers)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(watchers);

        foreach (string watcher in watchers)
        {
            if (frame.TargetConnectionId is { Length: > 0 } target && target != watcher)
            {
                // A repaint or a resume replay: an answer to one client's question,
                // not news about the session.
                continue;
            }

            Queue(agentConnectionId, watcher, frame);
        }
    }

    /// <summary>
    /// Throws away what a client has waiting, but keeps its pump.
    /// <para>
    /// Called when a client attaches, since a snapshot is about to supersede whatever
    /// was queued. The queue object is deliberately kept: it owns the only task that
    /// sends to this client, so a frame already in flight is guaranteed to be finished
    /// with before the snapshot goes out. Replacing the queue instead would leave two
    /// senders racing, and the stale one could land last and paint the old session over
    /// the new screen.
    /// </para>
    /// </summary>
    public void Reset(string clientConnectionId)
    {
        if (_queues.TryGetValue(clientConnectionId, out ClientQueue? queue))
        {
            queue.Clear();
        }
    }

    /// <summary>Drops a client's queue entirely. Called when it disconnects.</summary>
    public void Forget(string clientConnectionId)
    {
        if (_queues.TryRemove(clientConnectionId, out ClientQueue? queue))
        {
            queue.Stop();
        }
    }

    private void Queue(string agentConnectionId, string clientConnectionId, TerminalOutputNotification frame)
    {
        ClientQueue queue = _queues.GetOrAdd(
            clientConnectionId,
            id => new ClientQueue(id, this));

        queue.Enqueue(agentConnectionId, frame);
    }

    private Task SendAsync(string clientConnectionId, TerminalOutputNotification frame) =>
        _hub.Clients.Client(clientConnectionId).SendAsync(HubMethods.Client.TerminalOutput, frame);

    /// <summary>
    /// Asks the agent to repaint one client, and only that client.
    /// <para>
    /// Sent as an attach with no last-sequence, which is already the agent's "I have
    /// nothing, show me the screen" path. Geometry is left at zero so the agent does
    /// not reshape a pseudoconsole that nobody asked to reshape — the client is where
    /// it always was, it is simply behind.
    /// </para>
    /// </summary>
    private Task RequestRepaintAsync(string agentConnectionId, string clientConnectionId, string sessionId)
    {
        Interlocked.Increment(ref _repaints);

        _log.LogInformation(
            "Discarded the backlog for client {ClientConnectionId} on session {SessionId} and asked for a repaint.",
            clientConnectionId,
            sessionId);

        return _hub.Clients.Client(agentConnectionId).SendAsync(
            HubMethods.Agent.AttachRequested,
            new AttachRequestedNotification
            {
                SessionId = sessionId,
                ClientConnectionId = clientConnectionId,
                Cols = 0,
                Rows = 0,
                LastSeq = null,
            });
    }

    /// <summary>
    /// One client's backlog, and the single task that drains it.
    /// <para>
    /// One task rather than a send per frame, because SignalR guarantees ordering only
    /// for sends that are awaited in sequence. Terminal output is a stream of deltas;
    /// out of order, it is not a slightly wrong screen but a meaningless one.
    /// </para>
    /// </summary>
    private sealed class ClientQueue(string clientConnectionId, OutboundFanout owner)
    {
        private readonly object _gate = new();
        private readonly Queue<Pending> _pending = new();

        private int _bytes;
        private bool _draining;
        private bool _stopped;
        private DateTimeOffset _lastRepaint = DateTimeOffset.MinValue;

        public void Enqueue(string agentConnectionId, TerminalOutputNotification frame)
        {
            bool repaint = false;
            string sessionId = frame.SessionId;

            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                DateTimeOffset now = owner._time.GetUtcNow();

                _pending.Enqueue(new Pending(frame, now));
                _bytes += frame.Data.Length;

                if (IsBehind(now))
                {
                    _pending.Clear();
                    _bytes = 0;

                    // Throttled rather than skipped: a client that is behind must be
                    // repainted eventually or it sits on a screen with a hole in it.
                    // Deferring to the next overflow is fine because output keeps
                    // arriving; if it stops, the client is no longer behind.
                    if (now - _lastRepaint >= owner._limits.MinimumRepaintInterval)
                    {
                        _lastRepaint = now;
                        repaint = true;
                    }
                }

                if (!_draining && _pending.Count > 0)
                {
                    _draining = true;
                    _ = Task.Run(DrainAsync);
                }
            }

            if (repaint)
            {
                _ = owner.RequestRepaintAsync(agentConnectionId, clientConnectionId, sessionId);
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
                _pending.Clear();
                _bytes = 0;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _pending.Clear();
                _bytes = 0;
            }
        }

        private bool IsBehind(DateTimeOffset now) =>
            _bytes > owner._limits.MaxQueuedBytes
            || (_pending.Count > 0 && now - _pending.Peek().QueuedAt > owner._limits.MaxQueuedAge);

        private async Task DrainAsync()
        {
            while (true)
            {
                TerminalOutputNotification frame;

                lock (_gate)
                {
                    if (_stopped || _pending.Count == 0)
                    {
                        _draining = false;
                        return;
                    }

                    Pending next = _pending.Dequeue();
                    _bytes -= next.Frame.Data.Length;
                    frame = next.Frame;
                }

                try
                {
                    await owner.SendAsync(clientConnectionId, frame).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A client that cannot be written to is gone or going. Its queue is
                    // abandoned rather than retried: there is nothing to retry onto,
                    // and OnDisconnectedAsync will clean up the rest.
                    owner._log.LogDebug(
                        ex,
                        "Dropping the queue for client {ClientConnectionId}, which cannot be written to.",
                        clientConnectionId);

                    Stop();

                    lock (_gate)
                    {
                        _draining = false;
                    }

                    return;
                }
            }
        }

        private readonly record struct Pending(TerminalOutputNotification Frame, DateTimeOffset QueuedAt);
    }
}
