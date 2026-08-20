using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Ops;
using OneRemoteCli.Hub.Projects;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Relay;

/// <summary>
/// The relay. Agents register machines and stream terminal output; clients list
/// machines, attach to a session, and drive it.
/// <para>
/// <b>Not one method here takes a user identifier as a parameter.</b> The user key is
/// resolved from the connection's validated principal every time, and every lookup
/// runs inside that user's partition. This is deliberate and worth preserving: it
/// means a new method cannot introduce a cross-user hole by forgetting an ownership
/// check, because the data it is allowed to reach is decided before the method body
/// runs.
/// </para>
/// <para>
/// Methods return their result instead of pushing an <c>Error</c> down a side channel,
/// so a caller can await the outcome of the thing it just asked for. Failures come
/// back as an <see cref="ErrorNotification"/> rather than an exception, because a
/// hub exception reaches the client as an opaque string that no UI can branch on.
/// </para>
/// </summary>
[Authorize]
public sealed class RelayHub(
    RelayRegistry registry,
    OutboundFanout fanout,
    ConnectionTokens tokens,
    IAccessTokenValidator tokenValidator,
    PushSubscriptionStore pushSubscriptions,
    IPushNotifier push,
    IUsageRecorder usage,
    ProjectStore projects,
    ILogger<RelayHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly RelayRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly OutboundFanout _fanout = fanout ?? throw new ArgumentNullException(nameof(fanout));
    private readonly ConnectionTokens _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly IAccessTokenValidator _tokenValidator =
        tokenValidator ?? throw new ArgumentNullException(nameof(tokenValidator));
    private readonly PushSubscriptionStore _pushSubscriptions =
        pushSubscriptions ?? throw new ArgumentNullException(nameof(pushSubscriptions));
    private readonly IPushNotifier _push = push ?? throw new ArgumentNullException(nameof(push));

    // Counts and durations for the operator's weekly digest. Every call takes a user
    // key and a number; nothing here can hand it a machine or session display name,
    // which is what keeps one user's session names out of another human's chat.
    private readonly IUsageRecorder _usage = usage ?? throw new ArgumentNullException(nameof(usage));

    private readonly ProjectStore _projects = projects ?? throw new ArgumentNullException(nameof(projects));

    private readonly ILogger<RelayHub> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task OnConnectedAsync()
    {
        string? userKey = UserKey.From(Context.User!);

        if (userKey is null)
        {
            // Authentication succeeded but the token carries no usable identity. There
            // is no partition to put this connection in, and a connection with no
            // partition could only ever be a routing accident, so it does not live.
            _logger.LogWarning("Refusing connection {ConnectionId}: token has no user key.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        _registry.Connect(userKey, Context.ConnectionId);

        // "A new user joined" in the only sense that is a signal rather than noise: an
        // allowlisted account connecting for the first time ever. Not the config change
        // that admitted them, which the operator had just made.
        _usage.AccountSeen(userKey, UserKey.PreferredUsername(Context.User!));

        // The token is checked once, at the handshake, and never again by SignalR. From
        // here the hub owns its lifetime.
        HubCallerContext context = Context;
        _tokens.Track(context.ConnectionId, userKey, TokenExpiry.Of(context.User!), context.Abort);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _fanout.Forget(Context.ConnectionId);
        _tokens.Forget(Context.ConnectionId);

        DisconnectResult result = _registry.Disconnect(Context.ConnectionId);

        if (result.UserKey is not null && result.MachineId is not null)
        {
            // The machine's sessions went with it, but the clients are told only that
            // the machine is offline: a per-session storm would say nothing extra, and
            // an offline machine has no sessions worth listing.
            await Clients.Clients(_registry.ClientsOf(result.UserKey)).SendAsync(
                HubMethods.Client.MachineOffline,
                new MachineOfflineNotification { MachineId = result.MachineId });
        }

        if (result.ClientAttachment is { AgentConnectionId: { } agentConnectionId } attachment)
        {
            // A phone that loses signal never sends DetachSession, so the hub sends it
            // on the phone's behalf. Without this the agent keeps producing output for
            // a viewer that no longer exists.
            await Clients.Client(agentConnectionId).SendAsync(
                HubMethods.Agent.DetachRequested,
                new DetachRequestedNotification
                {
                    SessionId = attachment.SessionId,
                    ClientConnectionId = Context.ConnectionId,
                });
        }

        await base.OnDisconnectedAsync(exception);
    }

    // Agent to hub.

    /// <summary>An agent announces its machine. Also the agent's protocol handshake.</summary>
    public async Task<ErrorNotification?> RegisterMachine(RegisterMachineRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.MachineId))
        {
            return Error(ErrorCodes.InvalidRequest, "RegisterMachine needs a machine id.");
        }

        if (!ProtocolVersion.IsSupported(request.ProtocolVersion))
        {
            return UnsupportedVersion(request.ProtocolVersion);
        }

        string userKey = RequireUserKey();
        MachineInfo machine = _registry.RegisterMachine(userKey, Context.ConnectionId, request);

        // The version, and only the version. An agent well behind the hub is how
        // protocol bugs start, and it is invisible until one produces a symptom.
        _usage.AgentSeen(request.AgentVersion);

        _logger.LogInformation(
            "Machine {MachineId} ({DisplayName}) online.",
            machine.MachineId,
            machine.DisplayName);

        await Clients.Clients(_registry.ClientsOf(userKey)).SendAsync(
            HubMethods.Client.MachineOnline,
            new MachineOnlineNotification { Machine = machine });

        return null;
    }

    /// <summary>An agent reports a new session on its machine.</summary>
    public async Task<ErrorNotification?> SessionOpened(AgentSessionOpenedNotification notification)
    {
        if (notification?.Session is null || string.IsNullOrWhiteSpace(notification.Session.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "SessionOpened needs a session.");
        }

        SessionAddress? address = _registry.AddSession(Context.ConnectionId, notification.Session);
        if (address is null)
        {
            return Error(ErrorCodes.MachineNotFound, "Register the machine first.");
        }

        RestorePersistedProjectOrCorrectStale(address, notification.Session);

        // The session id goes in so open and close can be paired into a duration; it is
        // hashed on the way in and never comes back out. The session *name*, which is
        // right there on the notification, is not passed and must not be.
        _usage.SessionOpened(address.UserKey, notification.Session.SessionId);

        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionOpened,
            new ClientSessionOpenedNotification
            {
                MachineId = address.MachineId,
                Session = notification.Session,
            });

        return null;
    }

    /// <summary>
    /// An agent reports that a live session's details changed.
    /// <para>
    /// Deliberately not routed through <see cref="SessionOpened"/>, which is an
    /// upsert and would do the right thing to the stored record. It would also record
    /// a second session open in the usage figures for every correction the user makes,
    /// which is how a metric quietly stops meaning what its name says.
    /// </para>
    /// </summary>
    public async Task<ErrorNotification?> SessionUpdated(AgentSessionUpdatedNotification notification)
    {
        if (notification?.Session is null || string.IsNullOrWhiteSpace(notification.Session.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "SessionUpdated needs a session.");
        }

        SessionAddress? address = _registry.UpdateSession(Context.ConnectionId, notification.Session);
        if (address is null)
        {
            return Error(
                ErrorCodes.SessionNotFound,
                "No such session on this machine.",
                notification.Session.SessionId);
        }

        RestorePersistedProjectOrCorrectStale(address, notification.Session);

        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionUpdated,
            new ClientSessionUpdatedNotification
            {
                MachineId = address.MachineId,
                Session = notification.Session,
            });

        return null;
    }

    /// <summary>An agent reports a session ended.</summary>
    public async Task<ErrorNotification?> SessionClosed(AgentSessionClosedNotification notification)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "SessionClosed needs a session id.");
        }

        SessionAddress? address = _registry.RemoveSession(Context.ConnectionId, notification.SessionId);
        if (address is null)
        {
            return Error(ErrorCodes.SessionNotFound, "No such session on this machine.", notification.SessionId);
        }

        _projects.TrySetSessionProject(
            address.UserKey,
            address.MachineId,
            notification.SessionId,
            projectId: null,
            out _);

        _usage.SessionClosed(address.UserKey, notification.SessionId);

        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionClosed,
            new ClientSessionClosedNotification
            {
                MachineId = address.MachineId,
                SessionId = notification.SessionId,
                ExitCode = notification.ExitCode,
            });

        // Not pushed to someone who was watching it end. They saw the exit code on the
        // screen a moment ago; a lock-screen copy of it is pure noise.
        if (address.AttachedClients == 0)
        {
            _push.Enqueue(
                address.UserKey,
                PushPayload.Finished(
                    Name(address.MachineName, address.MachineId),
                    Name(address.SessionName, notification.SessionId),
                    notification.ExitCode,
                    PushPayload.DeepLink(address.MachineId, notification.SessionId)));
        }

        return null;
    }

    /// <summary>
    /// Terminal bytes, fanned out to whoever is watching this session.
    /// <para>
    /// The hottest path in the system, and the only one that is pure passthrough: the
    /// hub does not decode, buffer or inspect <c>Data</c>. Keeping it opaque is what
    /// lets end-to-end encryption be added later without touching the relay.
    /// </para>
    /// <para>
    /// It hands off rather than awaiting the send. SignalR processes one invocation at
    /// a time per connection, so awaiting the fan-out here would let the slowest phone
    /// attached to any session stop output for every session on the machine.
    /// </para>
    /// </summary>
    public Task TerminalOutput(TerminalOutputNotification notification)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.SessionId))
        {
            return Task.CompletedTask;
        }

        SessionAddress? address = _registry.MarkAwaitingInput(
            Context.ConnectionId,
            notification.SessionId,
            awaiting: false);

        if (address is null)
        {
            return Task.CompletedTask;
        }

        // The hottest path in the hub, so this is an interlocked add and nothing more.
        // Length is a count of bytes on the wire — the payload is opaque here and is
        // never decoded, which is exactly why it can be measured without being read.
        _usage.BytesRelayed(address.UserKey, notification.Data?.Length ?? 0);

        IReadOnlyList<string> watchers = _registry.ClientsAttachedTo(
            address.UserKey,
            address.MachineId,
            address.SessionId);

        if (watchers.Count > 0)
        {
            _fanout.Publish(Context.ConnectionId, notification, watchers);
        }

        return Task.CompletedTask;
    }

    /// <summary>A typed ACP transcript frame, fanned out only to clients watching the chat.</summary>
    public async Task ChatTranscript(ChatTranscriptNotification notification)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.SessionId))
        {
            return;
        }

        SessionAddress? address = _registry.AddressOf(Context.ConnectionId, notification.SessionId);
        if (address is null)
        {
            return;
        }

        IReadOnlyList<string> watchers = _registry.ClientsAttachedTo(
            address.UserKey,
            address.MachineId,
            address.SessionId);

        if (notification.TargetConnectionId is { Length: > 0 } target)
        {
            watchers = watchers.Contains(target, StringComparer.Ordinal) ? [target] : [];
        }

        if (watchers.Count > 0)
        {
            await Clients.Clients(watchers).SendAsync(HubMethods.Client.ChatTranscript, notification);
        }
    }

    /// <summary>The agent's idle heuristic fired. Every client of this user hears it, attached or not.</summary>
    public async Task<ErrorNotification?> SessionAwaitingInput(SessionAwaitingInputNotification notification)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "SessionAwaitingInput needs a session id.");
        }

        SessionAddress? address = _registry.MarkAwaitingInput(
            Context.ConnectionId,
            notification.SessionId,
            awaiting: true);

        if (address is null)
        {
            return Error(ErrorCodes.SessionNotFound, "No such session on this machine.", notification.SessionId);
        }

        // Sent to every client, not just attached ones: the entire point is to reach a
        // phone that is not currently looking at this session.
        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionAwaitingInput,
            new ClientSessionAwaitingInputNotification
            {
                MachineId = address.MachineId,
                SessionId = notification.SessionId,
                Hint = notification.Hint,
            });

        // The push, however, is only for a phone that is *not* looking. Notifying
        // someone about a prompt already on their screen is how a user learns that
        // these notifications do not mean anything.
        if (address.AttachedClients == 0)
        {
            _push.Enqueue(
                address.UserKey,
                PushPayload.AwaitingInput(
                    Name(address.MachineName, address.MachineId),
                    Name(address.SessionName, notification.SessionId),
                    notification.Hint,
                    PushPayload.DeepLink(address.MachineId, notification.SessionId)));
        }

        return null;
    }

    /// <summary>Explicit attention state from a structured chat provider.</summary>
    public async Task<ErrorNotification?> SessionAttention(SessionAttentionNotification notification)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "SessionAttention needs a session id.");
        }

        SessionAddress? address = _registry.MarkAwaitingInput(
            Context.ConnectionId,
            notification.SessionId,
            notification.AwaitingInput);

        if (address is null)
        {
            return Error(ErrorCodes.SessionNotFound, "No such session on this machine.", notification.SessionId);
        }

        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionAttention,
            new ClientSessionAttentionNotification
            {
                MachineId = address.MachineId,
                SessionId = notification.SessionId,
                AwaitingInput = notification.AwaitingInput,
                Hint = notification.Hint,
            });

        if (notification.AwaitingInput && address.AttachedClients == 0)
        {
            _push.Enqueue(
                address.UserKey,
                PushPayload.AwaitingInput(
                    Name(address.MachineName, address.MachineId),
                    Name(address.SessionName, notification.SessionId),
                    notification.Hint,
                    PushPayload.DeepLink(address.MachineId, notification.SessionId)));
        }

        return null;
    }

    // Client to hub.

    /// <summary>
    /// A client offers a Web Push subscription for this user.
    /// <para>
    /// Stored against the user, not the connection. A phone gets a new connection
    /// every time it wakes; the subscription is meant to outlive all of them, since
    /// its whole purpose is to be reachable when nothing is connected.
    /// </para>
    /// </summary>
    public Task<ErrorNotification?> RegisterPush(RegisterPushRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.Endpoint) ||
            request.Keys is null ||
            string.IsNullOrWhiteSpace(request.Keys.P256dh) ||
            string.IsNullOrWhiteSpace(request.Keys.Auth))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "RegisterPush needs an endpoint and both keys."));
        }

        bool changed = _pushSubscriptions.Register(
            RequireUserKey(),
            new PushSubscription(request.Endpoint, request.Keys.P256dh, request.Keys.Auth));

        if (changed)
        {
            // The endpoint is a capability URL for waking somebody's phone, so it is
            // never logged - only the fact that one arrived.
            _logger.LogInformation("Registered a push subscription.");
        }

        return Task.FromResult<ErrorNotification?>(null);
    }

    /// <summary>
    /// A client's protocol handshake.
    /// <para>
    /// Separate from <c>ListMachines</c> so an incompatible client is turned away
    /// before it can issue anything else, and so the version mismatch is reported as
    /// itself rather than as a strange failure two messages later.
    /// </para>
    /// </summary>
    public Task<ErrorNotification?> ClientHandshake(ClientHandshakeRequest request)
    {
        if (request is null)
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "ClientHandshake needs a request."));
        }

        if (!ProtocolVersion.IsSupported(request.ProtocolVersion))
        {
            return Task.FromResult<ErrorNotification?>(UnsupportedVersion(request.ProtocolVersion));
        }

        _registry.RegisterClient(RequireUserKey(), Context.ConnectionId);

        return Task.FromResult<ErrorNotification?>(null);
    }

    /// <summary>This user's machines and their live sessions. Structurally cannot return anyone else's.</summary>
    public Task<MachineListNotification> ListMachines() =>
        Task.FromResult(new MachineListNotification { Machines = _registry.ListMachines(RequireUserKey()) });

    /// <summary>Start watching a session.</summary>
    public async Task<ErrorNotification?> AttachSession(AttachSessionRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.MachineId) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "AttachSession needs a machine id and a session id.");
        }

        if (!_registry.TryAttach(
                Context.ConnectionId,
                request.MachineId,
                request.SessionId,
                out AttachResult? attached,
                out ErrorNotification? error))
        {
            return error;
        }

        // Anything still queued belongs to the session this client just left, or to an
        // earlier view of this one. Either way the agent is about to send a snapshot
        // that supersedes it, and delivering the backlog first would draw the old
        // session over the new one for as long as it took to drain.
        _fanout.Reset(Context.ConnectionId);

        if (attached!.Displaced is { AgentConnectionId: { } previousAgent } displaced)
        {
            await Clients.Client(previousAgent).SendAsync(
                HubMethods.Agent.DetachRequested,
                new DetachRequestedNotification
                {
                    SessionId = displaced.SessionId,
                    ClientConnectionId = Context.ConnectionId,
                });
        }

        await Clients.Client(attached.Target.AgentConnectionId).SendAsync(
            HubMethods.Agent.AttachRequested,
            new AttachRequestedNotification
            {
                SessionId = request.SessionId,
                ClientConnectionId = Context.ConnectionId,
                Cols = request.Cols,
                Rows = request.Rows,
                LastSeq = request.LastSeq,
            });

        return null;
    }

    /// <summary>Stop watching a session.</summary>
    public async Task<ErrorNotification?> DetachSession(DetachSessionRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Error(ErrorCodes.InvalidRequest, "DetachSession needs a session id.");
        }

        Attachment? previous = _registry.Detach(Context.ConnectionId, request.SessionId);

        if (previous is null)
        {
            return Error(ErrorCodes.NotAttached, "Not attached to that session.", request.SessionId);
        }

        if (previous.AgentConnectionId is { } agentConnectionId)
        {
            await Clients.Client(agentConnectionId).SendAsync(
                HubMethods.Agent.DetachRequested,
                new DetachRequestedNotification
                {
                    SessionId = previous.SessionId,
                    ClientConnectionId = Context.ConnectionId,
                });
        }

        return null;
    }

    /// <summary>Keystrokes, verbatim. The hub never interprets them.</summary>
    public Task<ErrorNotification?> SendInput(SendInputRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "SendInput needs a session id."));
        }

        return ForwardAsync(
            request.SessionId,
            HubMethods.Agent.SendInput,
            target => new SendInputNotification { SessionId = target.SessionId, Data = request.Data },
            SessionKind.Terminal);
    }

    /// <summary>Reshapes the real pseudoconsole. The phone is authoritative while attached.</summary>
    public Task<ErrorNotification?> ResizeTerminal(ResizeTerminalRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "ResizeTerminal needs a session id."));
        }

        if (request.Cols <= 0 || request.Rows <= 0)
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "Terminal dimensions must be positive.", request.SessionId));
        }

        return ForwardAsync(
            request.SessionId,
            HubMethods.Agent.ResizeTerminal,
            target => new ResizeTerminalNotification
            {
                SessionId = target.SessionId,
                Cols = request.Cols,
                Rows = request.Rows,
            },
            SessionKind.Terminal);
    }

    /// <summary>Ctrl+C. The single most time-critical action in the product.</summary>
    public Task<ErrorNotification?> InterruptSession(InterruptSessionRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "InterruptSession needs a session id."));
        }

        return ForwardAsync(
            request.SessionId,
            HubMethods.Agent.InterruptSession,
            target => new InterruptSessionNotification { SessionId = target.SessionId },
            SessionKind.Terminal);
    }

    /// <summary>Sends one user message to the attached ACP session.</summary>
    public Task<ErrorNotification?> SendChatMessage(SendChatMessageRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.Text))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "SendChatMessage needs a session id and text."));
        }

        string text = request.Text.Trim();
        if (text.Length > 20_000)
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "A chat message is limited to 20,000 characters.", request.SessionId));
        }

        return ForwardAsync(
            request.SessionId,
            HubMethods.Agent.SendChatMessage,
            target => new SendChatMessageNotification { SessionId = target.SessionId, Text = text },
            SessionKind.AgentChat);
    }

    /// <summary>Selects an option from a pending ACP permission request.</summary>
    public Task<ErrorNotification?> RespondChatPermission(RespondChatPermissionRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.OptionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(
                    ErrorCodes.InvalidRequest,
                    "RespondChatPermission needs a session, request, and option.",
                    request?.SessionId));
        }

        return ForwardAsync(
            request.SessionId,
            HubMethods.Agent.RespondChatPermission,
            target => new RespondChatPermissionNotification
            {
                SessionId = target.SessionId,
                RequestId = request.RequestId,
                OptionId = request.OptionId,
            },
            SessionKind.AgentChat);
    }

    /// <summary>
    /// The user correcting what the agent guessed this session is running.
    /// <para>
    /// Forwarded to the agent rather than applied here, even though the hub holds the
    /// record the phone reads. The agent owns session state; a hub that could edit it
    /// directly would have two writers for one field, and the next thing the agent
    /// announced would silently undo the user's choice.
    /// </para>
    /// </summary>
    public Task<ErrorNotification?> SetSessionType(SetSessionTypeRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "SetSessionType needs a session id."));
        }

        if (!Enum.IsDefined(request.CliType))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "That is not a CLI type.", request.SessionId));
        }

        return ForwardAsync(
            request.SessionId,
            HubMethods.Agent.SetSessionTypeRequested,
            target => new SetSessionTypeRequestedNotification
            {
                SessionId = target.SessionId,
                CliType = request.CliType,
            },
            SessionKind.Terminal);
    }

    /// <summary>
    /// The user renaming a session to whatever they actually call it.
    /// <para>
    /// Answered here rather than forwarded to the agent, which is the opposite of
    /// <see cref="SetSessionType"/> and for the same reason. The agent owns what a
    /// session <i>is</i>, so a type correction has to go there or the next thing the
    /// agent announces would undo it. Nobody owns what you call it — the agent has
    /// never heard of the name, cannot send it, and so cannot overwrite it. Keeping it
    /// at the hub also means it reaches the user's other devices and, more to the
    /// point, the push notification: the name is worth having mostly because it is
    /// what a locked phone shows.
    /// </para>
    /// </summary>
    public Task<ErrorNotification?> SetSessionName(SetSessionNameRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.MachineId) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "SetSessionName needs a machine id and a session id."));
        }

        bool renamed = _registry.TryRenameSession(
            Context.ConnectionId,
            request.MachineId,
            request.SessionId,
            request.Name,
            out LabelledSession? labelled,
            out ErrorNotification? error);

        return AnnounceLabelAsync(renamed, labelled, error);
    }

    /// <summary>The user lifting a session to the top of the list, on every device they own.</summary>
    public Task<ErrorNotification?> SetSessionPinned(SetSessionPinnedRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.MachineId) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "SetSessionPinned needs a machine id and a session id."));
        }

        bool pinned = _registry.TryPinSession(
            Context.ConnectionId,
            request.MachineId,
            request.SessionId,
            request.Pinned,
            out LabelledSession? labelled,
            out ErrorNotification? error);

        return AnnounceLabelAsync(pinned, labelled, error);
    }

    /// <summary>This user's projects, General first. Auto-seeds General on first call.</summary>
    public Task<ProjectListNotification> ListProjects() =>
        Task.FromResult(new ProjectListNotification { Projects = _projects.List(RequireUserKey()) });

    /// <summary>Creates a project and tells every one of this user's devices about it.</summary>
    public async Task<ProjectResult> CreateProject(CreateProjectRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            _logger.LogInformation(
                "Rejected a project creation request with {ErrorCode}.",
                ErrorCodes.InvalidRequest);
            return new ProjectResult { Error = ErrorCodes.InvalidRequest };
        }

        string userKey = RequireUserKey();

        if (!_projects.TryCreate(
                userKey,
                request.Name,
                request.Description,
                request.SiteUrl,
                request.RepoUrl,
                out ProjectInfo? project,
                out string? error))
        {
            _logger.LogInformation("Rejected a project creation request with {ErrorCode}.", error);
            return new ProjectResult { Error = error };
        }

        await Clients.Clients(_registry.ClientsOf(userKey)).SendAsync(
            HubMethods.Client.ProjectCreated,
            new ProjectCreatedNotification { Project = project! });

        return new ProjectResult { Project = project };
    }

    /// <summary>
    /// Edits a project's fields, General included - only its id and its
    /// non-deletability are fixed. Fans out to every device the same as a create.
    /// </summary>
    public async Task<ProjectResult> UpdateProject(UpdateProjectRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.Name))
        {
            _logger.LogInformation(
                "Rejected a project update request with {ErrorCode}.",
                ErrorCodes.InvalidRequest);
            return new ProjectResult { Error = ErrorCodes.InvalidRequest };
        }

        string userKey = RequireUserKey();

        if (!_projects.TryUpdate(
                userKey,
                request.ProjectId,
                request.Name,
                request.Description,
                request.SiteUrl,
                request.RepoUrl,
                out ProjectInfo? project,
                out string? error))
        {
            _logger.LogInformation("Rejected a project update request with {ErrorCode}.", error);
            return new ProjectResult { Error = error };
        }

        await Clients.Clients(_registry.ClientsOf(userKey)).SendAsync(
            HubMethods.Client.ProjectUpdated,
            new ProjectUpdatedNotification { Project = project! });

        return new ProjectResult { Project = project };
    }

    /// <summary>
    /// Deletes a project (refused for General), reassigns whatever is left in it back
    /// to General - on every machine this user owns, online or not - and tells every
    /// device both facts: the project is gone, and each affected session moved.
    /// </summary>
    public async Task<ErrorNotification?> DeleteProject(DeleteProjectRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProjectId))
        {
            return Error(ErrorCodes.InvalidRequest, "DeleteProject needs a project id.");
        }

        string userKey = RequireUserKey();

        if (!_projects.TryDelete(userKey, request.ProjectId, out string? error))
        {
            return Error(error!, "The project could not be deleted.");
        }

        IReadOnlyList<LabelledSession> reassigned = _registry.ClearProjectAssignments(userKey, request.ProjectId);
        IReadOnlyList<string> recipients = _registry.ClientsOf(userKey);

        foreach (LabelledSession labelled in reassigned)
        {
            await Clients.Clients(recipients).SendAsync(
                HubMethods.Client.SessionUpdated,
                new ClientSessionUpdatedNotification
                {
                    MachineId = labelled.MachineId,
                    Session = labelled.Session,
                });
        }

        await Clients.Clients(recipients).SendAsync(
            HubMethods.Client.ProjectDeleted,
            new ProjectDeletedNotification { ProjectId = request.ProjectId });

        return null;
    }

    /// <summary>Moves a live session to a different project, or back to General with null.</summary>
    public Task<ErrorNotification?> SetSessionProject(SetSessionProjectRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.MachineId) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.InvalidRequest, "SetSessionProject needs a machine id and a session id."));
        }

        string userKey = RequireUserKey();

        if (request.ProjectId is not null && !_projects.Exists(userKey, request.ProjectId))
        {
            return Task.FromResult<ErrorNotification?>(
                Error(ErrorCodes.ProjectNotFound, "No such project.", request.SessionId));
        }

        bool moved = _registry.TryMoveSession(
            Context.ConnectionId,
            request.MachineId,
            request.SessionId,
            request.ProjectId,
            out LabelledSession? labelled,
            out string? previousProjectId,
            out ErrorNotification? error);

        if (!moved)
        {
            return Task.FromResult(error);
        }

        if (!_projects.TrySetSessionProject(
                userKey,
                request.MachineId,
                request.SessionId,
                request.ProjectId,
                out string? persistenceError))
        {
            _registry.TryMoveSession(
                Context.ConnectionId,
                request.MachineId,
                request.SessionId,
                previousProjectId,
                out _,
                out _,
                out _);

            return Task.FromResult<ErrorNotification?>(
                Error(persistenceError!, "The session project could not be saved.", request.SessionId));
        }

        return AnnounceLabelAsync(moved, labelled, error);
    }

    // Internals.

    /// <summary>
    /// Defensive backstop for <c>DeleteProject</c>'s sweep: a machine that was
    /// offline when a project was deleted still carries the old <c>ProjectId</c> in
    /// its label, and re-announces it verbatim the moment it reconnects. Caught here,
    /// on the two paths an agent can (re-)announce a session, rather than trusted to
    /// have been swept already.
    /// </summary>
    private void RestorePersistedProjectOrCorrectStale(SessionAddress address, SessionInfo session)
    {
        if (_projects.ProjectOfSession(address.UserKey, address.MachineId, session.SessionId) is { } persisted)
        {
            _registry.ApplyPersistedProject(
                address.UserKey,
                address.MachineId,
                session.SessionId,
                persisted);
            session.ProjectId = persisted;
            return;
        }

        if (session.ProjectId is not { } projectId || _projects.Exists(address.UserKey, projectId))
        {
            return;
        }

        _registry.CorrectStaleProject(address.UserKey, address.MachineId, session.SessionId);
        session.ProjectId = null;
    }

    /// <summary>
    /// Tells every one of this user's clients what a session is now called.
    /// <para>
    /// Every client, not only the one that asked and not only those attached. Two
    /// devices renaming at once settle on the last write, which is fine — but only if
    /// the loser hears about it, and a phone looking at the list is by definition not
    /// attached to anything.
    /// </para>
    /// <para>
    /// Nothing about the name is logged, here or anywhere. A session's display name is
    /// already treated as sensitive (<c>docs/logging.md</c>), and one the user typed
    /// is no different.
    /// </para>
    /// </summary>
    private async Task<ErrorNotification?> AnnounceLabelAsync(
        bool changed,
        LabelledSession? labelled,
        ErrorNotification? error)
    {
        if (!changed)
        {
            return error;
        }

        await Clients.Clients(_registry.ClientsOf(labelled!.UserKey)).SendAsync(
            HubMethods.Client.SessionUpdated,
            new ClientSessionUpdatedNotification
            {
                MachineId = labelled.MachineId,
                Session = labelled.Session,
            });

        return null;
    }

    /// <summary>
    /// The one place a client request crosses to an agent. Every caller resolves the
    /// target through the caller's own attachment, so there is a single spot to read
    /// when asking how a message could possibly reach the wrong machine.
    /// </summary>
    private async Task<ErrorNotification?> ForwardAsync<TNotification>(
        string sessionId,
        string method,
        Func<RelayTarget, TNotification> build,
        SessionKind? requiredKind = null)
    {
        if (!_registry.TryResolveAttached(
                Context.ConnectionId,
                sessionId,
                out RelayTarget? target,
                out ErrorNotification? error))
        {
            return error;
        }

        if (requiredKind is not null && target!.Kind != requiredKind)
        {
            return Error(
                ErrorCodes.InvalidRequest,
                requiredKind == SessionKind.AgentChat
                    ? "That action requires an agent chat."
                    : "That action requires a terminal session.",
                sessionId);
        }

        await Clients.Client(target!.AgentConnectionId).SendAsync(method, build(target));

        return null;
    }

    // Either end to hub.

    /// <summary>
    /// Replaces the token a live connection was admitted with.
    /// <para>
    /// The re-check is deliberately the full one, not a signature check: a refresh that
    /// validated less than the handshake would be a way to launder a weak token onto a
    /// connection that was opened with a strong one.
    /// </para>
    /// <para>
    /// The two failures are not treated alike, and the difference is deliberate.
    /// </para>
    /// <para>
    /// A token the hub cannot accept is <b>refused, not fatal</b>. The connection's
    /// existing token may still have minutes on it, and killing the connection would
    /// destroy the one channel over which the holder could be told what went wrong —
    /// an abort cancels the invocation before its result can be flushed, so the caller
    /// would see only a disconnection with no reason. Refusing lets it acquire a proper
    /// token and try again; if it never does, the sweeper ends the connection at expiry,
    /// which was always the deadline.
    /// </para>
    /// <para>
    /// A token belonging to <b>someone else is fatal</b>, and the lost error message is
    /// a price worth paying. A connection carries attachments; walking one from one
    /// account to another would hand whatever it is watching to a person who was never
    /// granted it. There is no state on such a connection that is safe to keep once its
    /// owner is in question, so it does not survive long enough to be told why.
    /// </para>
    /// </summary>
    public async Task<ErrorNotification?> RefreshToken(RefreshTokenRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Token))
        {
            return Error(ErrorCodes.InvalidRequest, "RefreshToken needs a token.");
        }

        TokenReview review = await _tokenValidator.ReviewAsync(request.Token, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (!review.IsValid)
        {
            _logger.LogWarning(
                "Connection {ConnectionId} presented a token that is not valid: {Reason}",
                Context.ConnectionId,
                review.Reason);

            return Error(ErrorCodes.TokenExpired, review.Reason ?? "The refreshed token is not valid.");
        }

        string current = RequireUserKey();

        if (!string.Equals(review.UserKey, current, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Connection {ConnectionId} tried to refresh from one identity to another.",
                Context.ConnectionId);

            Context.Abort();
            return Error(ErrorCodes.IdentityChanged, "The refreshed token belongs to a different account.");
        }

        if (review.ExpiresAt is { } expiresAt)
        {
            _tokens.Renew(Context.ConnectionId, expiresAt);
        }

        return null;
    }

    /// <summary>
    /// The connection's identity. Never null in practice: a connection without a user
    /// key is aborted in <see cref="OnConnectedAsync"/> before any method can run.
    /// </summary>
    private string RequireUserKey() =>
        UserKey.From(Context.User!)
        ?? throw new HubException("Connection has no user key.");

    private ErrorNotification UnsupportedVersion(int presented)
    {
        _logger.LogWarning(
            "Connection {ConnectionId} speaks protocol {Presented}; this hub speaks {Minimum}–{Current}.",
            Context.ConnectionId,
            presented,
            ProtocolVersion.MinimumSupported,
            ProtocolVersion.Current);

        return Error(
            ErrorCodes.UnsupportedProtocolVersion,
            $"This hub speaks protocol {ProtocolVersion.MinimumSupported}–{ProtocolVersion.Current}, " +
            $"not {presented}. Update 1RemoteCLI.");
    }

    private static ErrorNotification Error(string code, string message, string? sessionId = null) =>
        new() { Code = code, Message = message, SessionId = sessionId };

    /// <summary>
    /// A display name if there is one, otherwise the id.
    /// <para>
    /// An id on a lock screen is useless, but it is still better than a blank line -
    /// it at least tells the user which notification this is not a duplicate of.
    /// </para>
    /// </summary>
    private static string Name(string displayName, string id) =>
        string.IsNullOrWhiteSpace(displayName) ? id : displayName;
}
