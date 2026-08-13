using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Relay;

/// <summary>
/// Where a session lives: the user who owns it, the machine hosting it, and the
/// session itself. Every routing decision in the hub produces one of these, and it
/// always carries the user key so a caller cannot accidentally act across partitions.
/// </summary>
/// <param name="MachineName">
/// The machine's display name at the moment of the lookup, for anything that has to
/// be shown to a human - a push notification, principally.
/// </param>
/// <param name="SessionName">The session's display name at the moment of the lookup.</param>
/// <param name="AttachedClients">
/// How many of this user's clients were watching this session when the lookup ran.
/// <para>
/// Reported here rather than fetched separately because it is used to decide whether
/// to send a push, and a second lookup could see a different answer - someone attaches
/// or their phone locks in the gap. Deciding from the same locked read as the routing
/// is what makes "do not notify about a screen they are already looking at" true
/// rather than usually true.
/// </para>
/// </param>
public sealed record SessionAddress(
    string UserKey,
    string MachineId,
    string SessionId,
    string MachineName = "",
    string SessionName = "",
    int AttachedClients = 0);

/// <summary>A client's current view of one session, and the agent that serves it.</summary>
/// <param name="AgentConnectionId">Null when the machine has gone offline since the attach.</param>
public sealed record Attachment(string MachineId, string SessionId, string? AgentConnectionId);

/// <summary>The agent connection a client request should be forwarded to.</summary>
public sealed record RelayTarget(string AgentConnectionId, string MachineId, string SessionId);

/// <summary>What the hub must clean up after a connection drops.</summary>
public sealed record DisconnectResult
{
    /// <summary>Null when the connection never identified itself.</summary>
    public string? UserKey { get; init; }

    /// <summary>Set when the dropped connection was an agent.</summary>
    public string? MachineId { get; init; }

    /// <summary>Set when the dropped connection was a client that was attached to something.</summary>
    public Attachment? ClientAttachment { get; init; }

    public static readonly DisconnectResult Nothing = new();
}

/// <summary>The outcome of a client attaching, including the attachment it displaced.</summary>
public sealed record AttachResult(RelayTarget Target, Attachment? Displaced);

/// <summary>
/// The hub's entire routing state, partitioned by user key.
/// <para>
/// <b>The partitioning is the security property, not a lookup optimisation.</b> Every
/// method here takes the user key that was resolved from the connection's validated
/// principal and searches only inside that partition. A machine belonging to someone
/// else is not merely rejected — it is not present, so a spoofed <c>machineId</c>
/// finds nothing. Cross-user access is structurally impossible rather than a check
/// that a future method might forget to make.
/// </para>
/// <para>
/// Everything is in memory and reconstructed by agents and clients reconnecting; see
/// spec §4.6 and §9. State is guarded by one lock rather than concurrent collections
/// because the interesting operations are compound — attaching displaces a previous
/// attachment, a disconnect removes a machine and all its sessions — and those must
/// be atomic. At this scale every operation touches a handful of entries and holds
/// the lock for microseconds, so a single lock costs nothing and removes an entire
/// class of interleaving bugs.
/// </para>
/// </summary>
public sealed class RelayRegistry
{
    private readonly object _gate = new();

    /// <summary>User key → everything that user owns.</summary>
    private readonly Dictionary<string, UserPartition> _partitions = new(StringComparer.Ordinal);

    /// <summary>Connection id → which partition it belongs to and what it is.</summary>
    private readonly Dictionary<string, ConnectionRecord> _connections = new(StringComparer.Ordinal);

    private readonly TimeProvider _time;

    public RelayRegistry()
        : this(TimeProvider.System)
    {
    }

    public RelayRegistry(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    // Connection lifecycle.

    /// <summary>
    /// Records a connection before it has said whether it is an agent or a client.
    /// <para>
    /// Which it is only becomes known at the first call — <c>RegisterMachine</c> or
    /// <c>ClientHandshake</c> — but the user key is known at the handshake, and
    /// binding it here means no later method has to trust a parameter for it.
    /// </para>
    /// </summary>
    public void Connect(string userKey, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            _connections[connectionId] = new ConnectionRecord(userKey);
        }
    }

    /// <summary>Removes a connection and reports what the hub still has to tell people about.</summary>
    public DisconnectResult Disconnect(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out ConnectionRecord? record))
            {
                return DisconnectResult.Nothing;
            }

            if (!_partitions.TryGetValue(record.UserKey, out UserPartition? partition))
            {
                return new DisconnectResult { UserKey = record.UserKey };
            }

            if (record.MachineId is not null &&
                partition.Machines.TryGetValue(record.MachineId, out RegisteredMachine? machine) &&
                machine.ConnectionId == connectionId)
            {
                // The machine record survives its agent so the phone can still see
                // "offline" rather than the machine silently vanishing, and so
                // MachineOffline stays distinguishable from MachineNotFound. Its
                // sessions do not: a session cannot outlive the wrapper hosting it.
                machine.ConnectionId = null;
                machine.Sessions.Clear();
                machine.LastSeen = _time.GetUtcNow();

                DetachEveryoneFrom(partition, record.MachineId);

                return new DisconnectResult { UserKey = record.UserKey, MachineId = record.MachineId };
            }

            if (partition.Clients.Remove(connectionId, out ClientRecord? client))
            {
                return new DisconnectResult
                {
                    UserKey = record.UserKey,
                    ClientAttachment = Resolve(partition, client.Attachment),
                };
            }

            return new DisconnectResult { UserKey = record.UserKey };
        }
    }

    // Agent side.

    /// <summary>
    /// Registers or re-registers a machine for the connection's user.
    /// <para>
    /// Re-registration is the normal path, not an error: an agent that restarts or
    /// reconnects presents the same machine id, and its previous connection may not
    /// have been reaped yet. The newest connection wins and the sessions of the old
    /// one are dropped, because they died with the process that hosted them.
    /// </para>
    /// </summary>
    public MachineInfo RegisterMachine(string userKey, string connectionId, RegisterMachineRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MachineId);

        lock (_gate)
        {
            UserPartition partition = PartitionOf(userKey);

            if (!partition.Machines.TryGetValue(request.MachineId, out RegisteredMachine? machine))
            {
                machine = new RegisteredMachine(request.MachineId);
                partition.Machines[request.MachineId] = machine;
            }

            if (machine.ConnectionId is not null && machine.ConnectionId != connectionId)
            {
                // Drop the stale connection's claim so its later disconnect does not
                // mark the freshly registered machine offline.
                if (_connections.TryGetValue(machine.ConnectionId, out ConnectionRecord? stale))
                {
                    stale.MachineId = null;
                }

                machine.Sessions.Clear();
                DetachEveryoneFrom(partition, machine.MachineId);
            }

            machine.ConnectionId = connectionId;
            machine.DisplayName = request.DisplayName;
            machine.Os = request.Os;
            machine.AgentVersion = request.AgentVersion;
            machine.LastSeen = _time.GetUtcNow();

            if (_connections.TryGetValue(connectionId, out ConnectionRecord? record))
            {
                record.MachineId = request.MachineId;
            }

            return machine.ToInfo();
        }
    }

    /// <summary>Adds a session to the machine owned by this agent connection.</summary>
    public SessionAddress? AddSession(string connectionId, SessionInfo session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            RegisteredMachine? machine = MachineOf(connectionId, out string? userKey);
            if (machine is null || userKey is null)
            {
                return null;
            }

            machine.Sessions[session.SessionId] = session;
            machine.LastSeen = _time.GetUtcNow();

            return new SessionAddress(userKey, machine.MachineId, session.SessionId);
        }
    }

    /// <summary>Removes a session, and detaches any client watching it.</summary>
    public SessionAddress? RemoveSession(string connectionId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            RegisteredMachine? machine = MachineOf(connectionId, out string? userKey);
            if (machine is null || userKey is null ||
                !machine.Sessions.Remove(sessionId, out SessionInfo? removed))
            {
                return null;
            }

            UserPartition partition = PartitionOf(userKey);
            int watchers = 0;

            foreach (ClientRecord client in partition.Clients.Values)
            {
                if (client.Attachment?.SessionId == sessionId &&
                    client.Attachment.MachineId == machine.MachineId)
                {
                    // Counted before it is cleared: whether to tell someone their build
                    // finished depends on whether they were watching it finish.
                    watchers++;
                    client.Attachment = null;
                }
            }

            return new SessionAddress(
                userKey,
                machine.MachineId,
                sessionId,
                machine.DisplayName,
                SessionName(removed),
                watchers);
        }
    }

    /// <summary>Records the agent's idle heuristic so a later <c>ListMachines</c> still reports it.</summary>
    public SessionAddress? MarkAwaitingInput(string connectionId, string sessionId, bool awaiting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            RegisteredMachine? machine = MachineOf(connectionId, out string? userKey);
            if (machine is null || userKey is null ||
                !machine.Sessions.TryGetValue(sessionId, out SessionInfo? session))
            {
                return null;
            }

            session.AwaitingInput = awaiting;

            return new SessionAddress(
                userKey,
                machine.MachineId,
                sessionId,
                machine.DisplayName,
                SessionName(session),
                CountWatchers(userKey, machine.MachineId, sessionId));
        }
    }

    // Client side.

    /// <summary>Marks a connection as a client of this user.</summary>
    public void RegisterClient(string userKey, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            PartitionOf(userKey).Clients[connectionId] = new ClientRecord();
        }
    }

    /// <summary>Everything this user owns. Never reaches into another partition.</summary>
    public MachineInfo[] ListMachines(string userKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_gate)
        {
            return _partitions.TryGetValue(userKey, out UserPartition? partition)
                ? partition.Machines.Values
                    .OrderBy(machine => machine.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(machine => machine.ToInfo())
                    .ToArray()
                : [];
        }
    }

    /// <summary>
    /// Points a client at a session, displacing whatever it was watching before.
    /// <para>
    /// A client watches one session at a time, so attaching implicitly detaches. If
    /// it did not, a phone that navigated between sessions would leave agents
    /// streaming output nobody renders — invisible, unbounded, and paid for in
    /// battery and bandwidth on the very device that has least of both.
    /// </para>
    /// </summary>
    public bool TryAttach(
        string clientConnectionId,
        string machineId,
        string sessionId,
        out AttachResult? attached,
        out ErrorNotification? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientConnectionId);

        attached = null;

        lock (_gate)
        {
            if (!TryClient(clientConnectionId, out UserPartition? partition, out ClientRecord? client))
            {
                error = Error(ErrorCodes.InvalidRequest, "Handshake before attaching.", sessionId);
                return false;
            }

            if (!TryLiveSession(partition!, machineId, sessionId, out RegisteredMachine? machine, out error))
            {
                return false;
            }

            Attachment? displaced = Resolve(partition!, client!.Attachment);
            client.Attachment = new AttachmentSlot(machineId, sessionId);

            // Re-attaching to the same session is not a displacement; telling the
            // agent to detach a client it is about to serve again would race.
            if (displaced?.MachineId == machineId && displaced.SessionId == sessionId)
            {
                displaced = null;
            }

            attached = new AttachResult(
                new RelayTarget(machine!.ConnectionId!, machineId, sessionId),
                displaced);

            error = null;
            return true;
        }
    }

    /// <summary>Stops a client watching a session. Returns what it was watching, if anything.</summary>
    public Attachment? Detach(string clientConnectionId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientConnectionId);

        lock (_gate)
        {
            if (!TryClient(clientConnectionId, out UserPartition? partition, out ClientRecord? client) ||
                client!.Attachment is null ||
                client.Attachment.SessionId != sessionId)
            {
                return null;
            }

            Attachment? previous = Resolve(partition!, client.Attachment);
            client.Attachment = null;

            return previous;
        }
    }

    /// <summary>
    /// Resolves the agent a client's input, resize or interrupt should reach.
    /// <para>
    /// Deliberately keyed off the client's own attachment rather than a machine id in
    /// the request. The client only sends a session id for these calls, and driving a
    /// session you are not watching has no legitimate use — so an unattached caller
    /// is refused rather than served.
    /// </para>
    /// </summary>
    public bool TryResolveAttached(
        string clientConnectionId,
        string sessionId,
        out RelayTarget? target,
        out ErrorNotification? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientConnectionId);

        target = null;

        lock (_gate)
        {
            if (!TryClient(clientConnectionId, out UserPartition? partition, out ClientRecord? client))
            {
                error = Error(ErrorCodes.InvalidRequest, "Handshake before sending input.", sessionId);
                return false;
            }

            AttachmentSlot? slot = client!.Attachment;
            if (slot is null || slot.SessionId != sessionId)
            {
                error = Error(ErrorCodes.NotAttached, "Attach to the session first.", sessionId);
                return false;
            }

            if (!TryLiveSession(partition!, slot.MachineId, sessionId, out RegisteredMachine? machine, out error))
            {
                return false;
            }

            target = new RelayTarget(machine!.ConnectionId!, slot.MachineId, sessionId);
            error = null;
            return true;
        }
    }

    // Fan-out.

    /// <summary>Every client connection belonging to this user.</summary>
    public IReadOnlyList<string> ClientsOf(string userKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_gate)
        {
            return _partitions.TryGetValue(userKey, out UserPartition? partition)
                ? partition.Clients.Keys.ToArray()
                : [];
        }
    }

    /// <summary>Every client of this user currently watching this session.</summary>
    public IReadOnlyList<string> ClientsAttachedTo(string userKey, string machineId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);

        lock (_gate)
        {
            if (!_partitions.TryGetValue(userKey, out UserPartition? partition))
            {
                return [];
            }

            return partition.Clients
                .Where(pair => pair.Value.Attachment is { } slot &&
                               slot.MachineId == machineId &&
                               slot.SessionId == sessionId)
                .Select(pair => pair.Key)
                .ToArray();
        }
    }

    // Internals.

    /// <summary>
    /// What to call a session on a lock screen.
    /// <para>
    /// The program, not the session id, when the agent gave it no name of its own.
    /// The id is a machine-local handle that means nothing to the person reading the
    /// notification; "claude is waiting" is the whole message, and
    /// "9f3c-…-21 is waiting" is noise they have to open the app to decode.
    /// </para>
    /// </summary>
    private static string SessionName(SessionInfo session) =>
        string.IsNullOrWhiteSpace(session.DisplayName) ? session.Program : session.DisplayName;

    /// <summary>Caller must hold the gate.</summary>
    private int CountWatchers(string userKey, string machineId, string sessionId) =>
        _partitions.TryGetValue(userKey, out UserPartition? partition)
            ? partition.Clients.Values.Count(client =>
                client.Attachment is { } slot && slot.MachineId == machineId && slot.SessionId == sessionId)
            : 0;

    private UserPartition PartitionOf(string userKey)
    {
        if (!_partitions.TryGetValue(userKey, out UserPartition? partition))
        {
            partition = new UserPartition();
            _partitions[userKey] = partition;
        }

        return partition;
    }

    private RegisteredMachine? MachineOf(string connectionId, out string? userKey)
    {
        userKey = null;

        if (!_connections.TryGetValue(connectionId, out ConnectionRecord? record) ||
            record.MachineId is null ||
            !_partitions.TryGetValue(record.UserKey, out UserPartition? partition) ||
            !partition.Machines.TryGetValue(record.MachineId, out RegisteredMachine? machine) ||
            machine.ConnectionId != connectionId)
        {
            return null;
        }

        userKey = record.UserKey;
        return machine;
    }

    private bool TryClient(string connectionId, out UserPartition? partition, out ClientRecord? client)
    {
        partition = null;
        client = null;

        if (!_connections.TryGetValue(connectionId, out ConnectionRecord? record) ||
            !_partitions.TryGetValue(record.UserKey, out UserPartition? found) ||
            !found.Clients.TryGetValue(connectionId, out ClientRecord? existing))
        {
            return false;
        }

        partition = found;
        client = existing;
        return true;
    }

    /// <summary>
    /// Looks a session up inside one partition only, and distinguishes the three ways
    /// it can be absent so the phone can say something useful.
    /// </summary>
    private static bool TryLiveSession(
        UserPartition partition,
        string machineId,
        string sessionId,
        out RegisteredMachine? machine,
        out ErrorNotification? error)
    {
        if (!partition.Machines.TryGetValue(machineId, out machine))
        {
            error = Error(ErrorCodes.MachineNotFound, "No such machine.", sessionId);
            return false;
        }

        if (machine.ConnectionId is null)
        {
            error = Error(ErrorCodes.MachineOffline, "That machine's agent is not connected.", sessionId);
            return false;
        }

        if (!machine.Sessions.ContainsKey(sessionId))
        {
            error = Error(ErrorCodes.SessionNotFound, "No such session on that machine.", sessionId);
            return false;
        }

        error = null;
        return true;
    }

    private static void DetachEveryoneFrom(UserPartition partition, string machineId)
    {
        foreach (ClientRecord client in partition.Clients.Values)
        {
            if (client.Attachment?.MachineId == machineId)
            {
                client.Attachment = null;
            }
        }
    }

    private static Attachment? Resolve(UserPartition partition, AttachmentSlot? slot)
    {
        if (slot is null)
        {
            return null;
        }

        partition.Machines.TryGetValue(slot.MachineId, out RegisteredMachine? machine);

        return new Attachment(slot.MachineId, slot.SessionId, machine?.ConnectionId);
    }

    private static ErrorNotification Error(string code, string message, string? sessionId) =>
        new() { Code = code, Message = message, SessionId = sessionId };

    private sealed class UserPartition
    {
        public Dictionary<string, RegisteredMachine> Machines { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ClientRecord> Clients { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ConnectionRecord(string userKey)
    {
        public string UserKey { get; } = userKey;

        /// <summary>Set once the connection registers a machine.</summary>
        public string? MachineId { get; set; }
    }

    private sealed class ClientRecord
    {
        public AttachmentSlot? Attachment { get; set; }
    }

    private sealed record AttachmentSlot(string MachineId, string SessionId);
}

/// <summary>A machine known to the hub. Outlives its agent connection so it can be reported offline.</summary>
public sealed class RegisteredMachine(string machineId)
{
    public string MachineId { get; } = machineId;

    /// <summary>Null when no agent is currently connected for this machine.</summary>
    public string? ConnectionId { get; internal set; }

    public string DisplayName { get; internal set; } = string.Empty;

    public string Os { get; internal set; } = string.Empty;

    public string AgentVersion { get; internal set; } = string.Empty;

    public DateTimeOffset LastSeen { get; internal set; }

    internal Dictionary<string, SessionInfo> Sessions { get; } = new(StringComparer.Ordinal);

    public MachineInfo ToInfo() => new()
    {
        MachineId = MachineId,
        DisplayName = DisplayName,
        Os = Os,
        AgentVersion = AgentVersion,
        Online = ConnectionId is not null,
        Sessions = Sessions.Values.OrderBy(session => session.StartedAt).ToArray(),
    };
}
