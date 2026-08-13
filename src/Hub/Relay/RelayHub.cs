using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OneRemoteCli.Hub.Auth;
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
    ILogger<RelayHub> logger) : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly RelayRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly OutboundFanout _fanout = fanout ?? throw new ArgumentNullException(nameof(fanout));
    private readonly ConnectionTokens _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly IAccessTokenValidator _tokenValidator =
        tokenValidator ?? throw new ArgumentNullException(nameof(tokenValidator));
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

        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionOpened,
            new ClientSessionOpenedNotification
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

        await Clients.Clients(_registry.ClientsOf(address.UserKey)).SendAsync(
            HubMethods.Client.SessionClosed,
            new ClientSessionClosedNotification
            {
                MachineId = address.MachineId,
                SessionId = notification.SessionId,
                ExitCode = notification.ExitCode,
            });

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

        return null;
    }

    // Client to hub.

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
            target => new SendInputNotification { SessionId = target.SessionId, Data = request.Data });
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
            });
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
            target => new InterruptSessionNotification { SessionId = target.SessionId });
    }

    // Internals.

    /// <summary>
    /// The one place a client request crosses to an agent. Every caller resolves the
    /// target through the caller's own attachment, so there is a single spot to read
    /// when asking how a message could possibly reach the wrong machine.
    /// </summary>
    private async Task<ErrorNotification?> ForwardAsync<TNotification>(
        string sessionId,
        string method,
        Func<RelayTarget, TNotification> build)
    {
        if (!_registry.TryResolveAttached(
                Context.ConnectionId,
                sessionId,
                out RelayTarget? target,
                out ErrorNotification? error))
        {
            return error;
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
}
