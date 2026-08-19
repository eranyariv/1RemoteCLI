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
public sealed record RelayTarget(
    string AgentConnectionId,
    string MachineId,
    string SessionId,
    SessionKind Kind);

/// <summary>
/// A session after the user changed what they call it, with everything needed to
/// tell the rest of their devices.
/// </summary>
public sealed record LabelledSession(string UserKey, string MachineId, SessionInfo Session);

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
/// How much the hub is currently holding. Numbers only, deliberately.
/// <para>
/// This is what the operator channel is allowed to see, and the shape is the point: it
/// answers "how many machines" without ever being able to answer "which machines". An
/// admin console is exactly where naming them would feel like a reasonable next
/// addition, so the type that would carry the names is not reachable from there at all.
/// </para>
/// </summary>
public readonly record struct RelayCounts(int Accounts, int Machines, int Sessions, int Connections);

/// <summary>
/// What one user decided to call a session, and whether they lifted it to the top.
/// <para>
/// Held apart from the <see cref="SessionInfo"/> it decorates, and that separation is
/// the whole design. A machine that drops its hub connection has its session records
/// cleared — they are re-announced when the agent reconnects — so a name kept on the
/// record alone would be lost to any wifi blip, and the user would watch their rename
/// revert for a reason nothing on screen could explain. The label outlives the record
/// and is re-applied to whatever comes back under the same session id.
/// </para>
/// <para>
/// It does not outlive the session itself. There is no expiry to get right and
/// nothing to clean up on a schedule, because the thing it is keyed to disappears on
/// its own — which is what makes "for as long as the session runs" a property of the
/// design rather than a rule somebody has to maintain.
/// </para>
/// </summary>
public sealed class SessionLabel
{
    /// <summary>Null once the user clears it, which reveals the agent's name again.</summary>
    public string? Name { get; set; }

    public bool Pinned { get; set; }

    /// <summary>
    /// The project this session is grouped under. Null means General - the same
    /// null-means-default convention as <see cref="Name"/>, and read by the same
    /// re-application on reconnect that keeps a rename alive across one.
    /// </summary>
    public string? ProjectId { get; set; }

    /// <summary>When this was last written. Decides which label goes first when the cap bites.</summary>
    public DateTimeOffset TouchedAt { get; set; }

    /// <summary>A label that says nothing is not worth keeping.</summary>
    public bool IsEmpty => Name is null && !Pinned && ProjectId is null;
}

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

            // A session the user had already named, arriving again after a reconnect.
            // The agent has never heard of the label and cannot send it, so this is
            // the only place it can be restored.
            machine.ApplyLabel(session);

            return new SessionAddress(userKey, machine.MachineId, session.SessionId);
        }
    }

    /// <summary>
    /// Replaces a session's details in place.
    /// <para>
    /// Refuses to create one. An update for a session that is not here is either a
    /// race with its own close or an agent working from a stale list, and in both
    /// cases inserting would put a session on the user's list that no longer exists
    /// and that nothing will ever remove.
    /// </para>
    /// </summary>
    public SessionAddress? UpdateSession(string connectionId, SessionInfo session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            RegisteredMachine? machine = MachineOf(connectionId, out string? userKey);
            if (machine is null || userKey is null ||
                !machine.Sessions.TryGetValue(session.SessionId, out SessionInfo? existing))
            {
                return null;
            }

            // The agent does not track the idle heuristic per session, so a whole-record
            // update would clear a flag the hub is the one holding. Carried across
            // rather than trusted from the sender.
            session.AwaitingInput = existing.AwaitingInput;

            machine.Sessions[session.SessionId] = session;
            machine.LastSeen = _time.GetUtcNow();

            // Same argument, one step further: the name and the pin are not merely
            // held by the hub, they are unknown to the agent. An update that carried
            // them across from the record would work; re-applying from the label is
            // what also covers a record that has just been re-created by a reconnect.
            machine.ApplyLabel(session);

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

            // The session is over, so the name the user gave it is over too. This is
            // the ordinary end of a label's life and the reason nothing here needs an
            // expiry sweep.
            machine.ForgetLabel(sessionId);

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
                NameOf(removed),
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
                NameOf(session),
                CountWatchers(userKey, machine.MachineId, sessionId));
        }
    }

    /// <summary>Resolves a session owned by an agent connection without changing it.</summary>
    public SessionAddress? AddressOf(string connectionId, string sessionId)
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

            return new SessionAddress(
                userKey,
                machine.MachineId,
                sessionId,
                machine.DisplayName,
                NameOf(session),
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

    /// <summary>
    /// Renames a session for as long as it runs, or clears the name with null.
    /// <para>
    /// The agent's own name is never overwritten, only shadowed, which is what makes
    /// clearing possible at all: there is something underneath to reveal.
    /// </para>
    /// </summary>
    public bool TryRenameSession(
        string clientConnectionId,
        string machineId,
        string sessionId,
        string? name,
        out LabelledSession? result,
        out ErrorNotification? error) =>
        TryEditLabel(
            clientConnectionId,
            machineId,
            sessionId,
            label => label.Name = SessionName.Sanitize(name),
            out result,
            out error);

    /// <summary>Lifts a session above the rest of this user's list, or puts it back.</summary>
    public bool TryPinSession(
        string clientConnectionId,
        string machineId,
        string sessionId,
        bool pinned,
        out LabelledSession? result,
        out ErrorNotification? error) =>
        TryEditLabel(
            clientConnectionId,
            machineId,
            sessionId,
            label => label.Pinned = pinned,
            out result,
            out error);

    /// <summary>Moves a session to a different project, or back to General with null.</summary>
    public bool TryMoveSession(
        string clientConnectionId,
        string machineId,
        string sessionId,
        string? projectId,
        out LabelledSession? result,
        out ErrorNotification? error) =>
        TryEditLabel(
            clientConnectionId,
            machineId,
            sessionId,
            label => label.ProjectId = projectId,
            out result,
            out error);

    /// <summary>
    /// Reassigns every session under this project, on every machine this user owns -
    /// online or not - back to General, and reports which of them are live right now
    /// so the caller can tell the user's other devices.
    /// <para>
    /// Sweeps offline machines too, deliberately: their labels are exactly the ones
    /// that would otherwise resurface a deleted project the moment that agent
    /// reconnects and re-announces its sessions. See <c>RelayHub</c>'s defensive
    /// check on <c>SessionOpened</c>/<c>SessionUpdated</c> for the backstop this is
    /// meant to make redundant, not rely on.
    /// </para>
    /// </summary>
    public IReadOnlyList<LabelledSession> ClearProjectAssignments(string userKey, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        lock (_gate)
        {
            if (!_partitions.TryGetValue(userKey, out UserPartition? partition))
            {
                return [];
            }

            List<LabelledSession> affected = [];
            DateTimeOffset now = _time.GetUtcNow();

            foreach (RegisteredMachine machine in partition.Machines.Values)
            {
                foreach (string sessionId in machine.ClearProjectAssignments(projectId, now))
                {
                    if (machine.Sessions.TryGetValue(sessionId, out SessionInfo? session))
                    {
                        machine.ApplyLabel(session);
                        affected.Add(new LabelledSession(userKey, machine.MachineId, session));
                    }
                }
            }

            return affected;
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
    /// How much is connected right now, across every partition.
    /// <para>
    /// The one method here that crosses partitions, which is safe precisely because it
    /// returns nothing but counts — there is no identifier in a <see cref="RelayCounts"/>
    /// to attribute to the wrong person. It exists for the operator channel, which is
    /// allowed to know how busy the hub is and is not allowed to know whose machines
    /// those are.
    /// </para>
    /// <para>
    /// Machines are counted only while an agent is connected. An offline machine is
    /// remembered so it can be reported offline, but counting it here would tell the
    /// operator the hub is holding more than it is.
    /// </para>
    /// </summary>
    public RelayCounts Counts()
    {
        lock (_gate)
        {
            int machines = 0;
            int sessions = 0;

            foreach (UserPartition partition in _partitions.Values)
            {
                foreach (RegisteredMachine machine in partition.Machines.Values)
                {
                    if (machine.ConnectionId is null)
                    {
                        continue;
                    }

                    machines++;
                    sessions += machine.Sessions.Count;
                }
            }

            return new RelayCounts(
                Accounts: _connections.Values.Select(record => record.UserKey).Distinct(StringComparer.Ordinal).Count(),
                Machines: machines,
                Sessions: sessions,
                Connections: _connections.Count);
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
                new RelayTarget(
                    machine!.ConnectionId!,
                    machineId,
                    sessionId,
                    machine.Sessions[sessionId].Kind),
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

            target = new RelayTarget(
                machine!.ConnectionId!,
                slot.MachineId,
                sessionId,
                machine.Sessions[sessionId].Kind);
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
    /// The program, not the session id, when nobody has given it a name. The id is a
    /// machine-local handle that means nothing to the person reading the
    /// notification; "claude is waiting" is the whole message, and
    /// "9f3c-…-21 is waiting" is noise they have to open the app to decode.
    /// </para>
    /// <para>
    /// The user's own name wins, which is most of the point of letting them set one:
    /// the notification that wakes a phone should say "the deploy is waiting", not
    /// "pwsh is waiting".
    /// </para>
    /// </summary>
    private static string NameOf(SessionInfo session) =>
        SessionName.Best(session.CustomName, session.DisplayName, session.Program);

    /// <summary>
    /// Finds the session, edits its label, and puts the result back on the record.
    /// <para>
    /// The one place a label changes. Resolved from the caller's own partition rather
    /// than through an attachment, unlike everything else a client can ask for by
    /// session id — because renaming is done from the list, where nothing is attached.
    /// That is safe here and would not be for the others: this message never crosses
    /// to a machine, so the most a forged machine id achieves is finding nothing.
    /// </para>
    /// </summary>
    private bool TryEditLabel(
        string clientConnectionId,
        string machineId,
        string sessionId,
        Action<SessionLabel> edit,
        out LabelledSession? result,
        out ErrorNotification? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientConnectionId);

        result = null;

        lock (_gate)
        {
            if (!TryClient(clientConnectionId, out UserPartition? partition, out _) ||
                !_connections.TryGetValue(clientConnectionId, out ConnectionRecord? record))
            {
                error = Error(ErrorCodes.InvalidRequest, "Handshake before changing a session.", sessionId);
                return false;
            }

            if (!TryLiveSession(partition!, machineId, sessionId, out RegisteredMachine? machine, out error))
            {
                return false;
            }

            SessionInfo session = machine!.Sessions[sessionId];

            machine.EditLabel(sessionId, edit, _time.GetUtcNow());
            machine.ApplyLabel(session);

            result = new LabelledSession(record.UserKey, machineId, session);
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Hub-internal correction, not a user action: clears a session's label back to
    /// General when the project it names has been deleted out from under it. See
    /// <c>RelayHub</c>'s self-check on <c>SessionOpened</c>/<c>SessionUpdated</c>,
    /// which is the only caller.
    /// <para>
    /// Resolved directly by user key and machine id rather than through a client's
    /// own connection like <see cref="TryEditLabel"/>, because the caller here is
    /// reacting to an agent's announcement, not a client's request - there is no
    /// client connection to resolve from.
    /// </para>
    /// </summary>
    public void CorrectStaleProject(string userKey, string machineId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            if (!_partitions.TryGetValue(userKey, out UserPartition? partition) ||
                !partition.Machines.TryGetValue(machineId, out RegisteredMachine? machine))
            {
                return;
            }

            machine.EditLabel(sessionId, label => label.ProjectId = null, _time.GetUtcNow());

            if (machine.Sessions.TryGetValue(sessionId, out SessionInfo? session))
            {
                machine.ApplyLabel(session);
            }
        }
    }

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
    /// <summary>
    /// How many session labels one machine may hold.
    /// <para>
    /// A label is normally removed when its session closes, so this never bites in
    /// ordinary use. It exists for the one path where the close never arrives: an
    /// agent that drops off the network has its sessions cleared without a
    /// <c>SessionClosed</c> for any of them, and their labels have nothing left to
    /// tell them the session is over. Without a ceiling, a machine that reconnects
    /// often enough would accumulate them for the lifetime of the process.
    /// </para>
    /// </summary>
    private const int MaxLabels = 64;

    private readonly Dictionary<string, SessionLabel> _labels = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Writes this machine's label for a session onto the record clients will read.
    /// <para>
    /// Always both fields, including when there is no label: a record that arrives
    /// from an agent carrying a stale name — impossible today, but only because the
    /// agent has never been given one to send — must not be able to introduce one.
    /// The hub is the only writer of these two fields, and this is where it writes.
    /// </para>
    /// </summary>
    internal void ApplyLabel(SessionInfo session)
    {
        _labels.TryGetValue(session.SessionId, out SessionLabel? label);

        session.CustomName = label?.Name;
        session.Pinned = label?.Pinned ?? false;
        session.ProjectId = label?.ProjectId;
    }

    /// <summary>Changes a session's label, creating it on first use and dropping it once it says nothing.</summary>
    internal void EditLabel(string sessionId, Action<SessionLabel> edit, DateTimeOffset now)
    {
        if (!_labels.TryGetValue(sessionId, out SessionLabel? label))
        {
            label = new SessionLabel();
            _labels[sessionId] = label;
        }

        edit(label);
        label.TouchedAt = now;

        // A cleared name on an unpinned session is not an empty label to be kept, it
        // is the absence of one. Removing it is what stops "rename, then rename back"
        // from leaving anything behind.
        if (label.IsEmpty)
        {
            _labels.Remove(sessionId);
            return;
        }

        Evict();
    }

    internal void ForgetLabel(string sessionId) => _labels.Remove(sessionId);

    /// <summary>
    /// Clears one project's assignment from every label that carries it on this
    /// machine, and reports which session ids changed.
    /// <para>
    /// Caller must hold the registry's gate, like every other internal method here.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> ClearProjectAssignments(string projectId, DateTimeOffset now)
    {
        List<string>? changed = null;

        foreach ((string sessionId, SessionLabel label) in _labels)
        {
            if (label.ProjectId != projectId)
            {
                continue;
            }

            label.ProjectId = null;
            label.TouchedAt = now;
            (changed ??= []).Add(sessionId);
        }

        if (changed is null)
        {
            return [];
        }

        // A label that now says nothing is not worth keeping, same rule as EditLabel.
        foreach (string sessionId in changed)
        {
            if (_labels.TryGetValue(sessionId, out SessionLabel? label) && label.IsEmpty)
            {
                _labels.Remove(sessionId);
            }
        }

        return changed;
    }

    /// <summary>
    /// Makes room, orphans first.
    /// <para>
    /// A label whose session is no longer on this machine cannot ever be seen again,
    /// so it is always the better thing to lose. Only if every label is in use does
    /// this fall back to the least recently set one — and a user with more than
    /// <see cref="MaxLabels"/> live named sessions on one machine has a different
    /// problem than the one this cap is for.
    /// </para>
    /// </summary>
    private void Evict()
    {
        while (_labels.Count > MaxLabels)
        {
            KeyValuePair<string, SessionLabel> victim = _labels
                .OrderBy(pair => Sessions.ContainsKey(pair.Key))
                .ThenBy(pair => pair.Value.TouchedAt)
                .First();

            _labels.Remove(victim.Key);
        }
    }
}
