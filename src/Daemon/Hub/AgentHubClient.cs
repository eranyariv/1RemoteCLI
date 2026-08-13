using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Daemon.Hub;

/// <summary>
/// The agent's end of the relay: it publishes this machine and its sessions, streams
/// terminal output out, and delivers input, resizes and interrupts back to the
/// wrapper that owns each session.
/// <para>
/// It is also the <see cref="ISessionSink"/>, because the sink and the connection are
/// the same thing — splitting them would only add a queue between two objects with
/// identical lifetimes.
/// </para>
/// <para>
/// Output arrives here already framed, is numbered and retained by the session's
/// <see cref="SessionTail"/>, and is then put on the wire. An attach is answered from
/// that tail when the client's last sequence is still in it, and with a snapshot of the
/// emulator's screen when it is not. What is still missing is backpressure: a client
/// that cannot keep up is not yet detected, so its frames queue in SignalR rather than
/// being collapsed into a fresh snapshot. Task 3.3 covers it.
/// </para>
/// </summary>
public sealed class AgentHubClient : ISessionSink, IAsyncDisposable
{
    /// <summary>Backoff bounds for reconnecting. Short enough to feel instant, capped so a dead hub is cheap.</summary>
    private static readonly TimeSpan MinimumRetry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetry = TimeSpan.FromSeconds(30);

    /// <summary>Longer, because the repair is a human running <c>1remote login</c>.</summary>
    private static readonly TimeSpan SignedOutRetry = TimeSpan.FromSeconds(30);

    private readonly HubConnection _connection;
    private readonly MachineIdentity _identity;
    private readonly SessionRegistry _sessions;
    private readonly Func<CancellationToken, Task<string?>> _tokenProvider;
    private readonly Action<string>? _log;

    /// <summary>Last message logged, so a hub that is down for an hour does not produce an hour of identical lines.</summary>
    private string? _lastComplaint;

    private volatile TaskCompletionSource _closed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentHubClient(
        Uri hubUri,
        MachineIdentity identity,
        SessionRegistry sessions,
        Func<CancellationToken, Task<string?>> tokenProvider,
        Action<string>? log = null,
        Action<HttpConnectionOptions>? configureConnection = null)
    {
        ArgumentNullException.ThrowIfNull(hubUri);

        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _log = log;

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                // Called on every connect *and* every reconnect, which is the whole
                // point: a socket that reconnects after an hour asleep must not
                // present the token it was born with.
                options.AccessTokenProvider = () => _tokenProvider(CancellationToken.None);

                configureConnection?.Invoke(options);
            })
            // MessagePack because terminal output is binary; JSON would base64 every
            // frame on the one path that is hot.
            .AddMessagePackProtocol()
            .WithAutomaticReconnect()
            .Build();

        // The hub's registry is per connection and in memory, so a new connection id
        // means the hub has never heard of this machine. Re-registering is not a
        // repair, it is the normal path.
        _connection.Reconnected += async _ => await RegisterAsync(CancellationToken.None).ConfigureAwait(false);

        _connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };

        RegisterHandlers();
    }

    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>
    /// Connects and stays connected until cancelled.
    /// <para>
    /// A hub that cannot be reached is never fatal. Local sessions keep working at the
    /// desk whether or not the phone can see them, so an unreachable relay degrades
    /// the product rather than breaking it, and the agent simply keeps trying.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        TimeSpan retry = MinimumRetry;

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait = retry;

            try
            {
                string? token = await _tokenProvider(cancellationToken).ConfigureAwait(false);

                if (token is null)
                {
                    Complain("hub: not signed in, so this machine is not reachable from your phone. Run '1remote login'.");
                    wait = SignedOutRetry;
                }
                else
                {
                    _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                    await RegisterAsync(cancellationToken).ConfigureAwait(false);

                    retry = MinimumRetry;

                    // Returns when automatic reconnect has given up entirely.
                    await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                    Complain("hub: disconnected, reconnecting.");
                    wait = MinimumRetry;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Complain($"hub: {ex.Message}");
                retry = Backoff(retry);
            }

            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopQuietlyAsync().ConfigureAwait(false);
    }

    // ISessionSink. Every method is best-effort: a session must never fail at the desk
    // because the relay is unreachable.

    public async ValueTask OnOpenedAsync(TerminalSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await TryInvokeAsync(
            HubMethods.Server.SessionOpened,
            new AgentSessionOpenedNotification { Session = Describe(session) },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OnOutputAsync(
        TerminalSession session,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (bytes.IsEmpty)
        {
            return;
        }

        await SendOutputAsync(session, bytes, TerminalOutputKind.Delta, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Numbers one chunk of output, keeps it, and puts it on the wire.
    /// <para>
    /// Callers are expected to be inside the session's exclusive region, which is what
    /// makes the sequence numbers match the order the bytes actually arrive in. That
    /// matters most for a snapshot: it is generated from a screen, so it is only
    /// correct relative to a specific point in the delta stream.
    /// </para>
    /// <para>
    /// The frame is recorded before the connection is checked. Output produced while
    /// the hub is unreachable is still part of this session's history, and a returning
    /// client that resumes across such a gap has to be given it or told to start again;
    /// silently skipping it would leave a contiguous run of sequence numbers with a
    /// hole in the picture.
    /// </para>
    /// </summary>
    private async ValueTask SendOutputAsync(
        TerminalSession session,
        ReadOnlyMemory<byte> bytes,
        TerminalOutputKind kind,
        CancellationToken cancellationToken)
    {
        byte[] data = bytes.ToArray();
        long seq = session.Tail.Record(kind, data);

        await TransmitAsync(session.SessionId, seq, kind, data, target: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Puts an already-numbered frame on the wire, unchanged.</summary>
    private async ValueTask TransmitAsync(
        string sessionId,
        long seq,
        TerminalOutputKind kind,
        byte[] data,
        string? target,
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            // Sent, not invoked: awaiting an acknowledgement for every chunk would put
            // a network round trip between the program and the screen.
            await _connection.SendAsync(
                HubMethods.Server.TerminalOutput,
                new TerminalOutputNotification
                {
                    SessionId = sessionId,
                    Seq = seq,
                    Kind = kind,
                    Data = data,
                    TargetConnectionId = target,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Complain($"hub: output dropped ({ex.Message}).");
        }
    }

    public async ValueTask OnClosedAsync(
        TerminalSession session,
        int exitCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await TryInvokeAsync(
            HubMethods.Server.SessionClosed,
            new AgentSessionClosedNotification { SessionId = session.SessionId, ExitCode = exitCode },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask OnAwaitingInputAsync(
        TerminalSession session,
        string? hint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await TryInvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = session.SessionId, Hint = hint },
            cancellationToken).ConfigureAwait(false);
    }

    // Internals.

    private void RegisterHandlers()
    {
        _connection.On<SendInputNotification>(
            HubMethods.Agent.SendInput,
            async notification => await RouteAsync(
                notification.SessionId,
                () => _sessions.SendInputAsync(notification.SessionId, notification.Data)).ConfigureAwait(false));

        _connection.On<ResizeTerminalNotification>(
            HubMethods.Agent.ResizeTerminal,
            async notification => await RouteAsync(
                notification.SessionId,
                () => _sessions.ResizeAsync(
                    notification.SessionId,
                    notification.Cols,
                    notification.Rows)).ConfigureAwait(false));

        _connection.On<InterruptSessionNotification>(
            HubMethods.Agent.InterruptSession,
            async notification => await RouteAsync(
                notification.SessionId,
                () => _sessions.InterruptAsync(notification.SessionId)).ConfigureAwait(false));

        _connection.On<AttachRequestedNotification>(
            HubMethods.Agent.AttachRequested,
            async notification => await OnAttachRequestedAsync(notification).ConfigureAwait(false));

        _connection.On<DetachRequestedNotification>(
            HubMethods.Agent.DetachRequested,
            notification => Log($"hub: client detached from session {notification.SessionId}."));

        _connection.On<TokenExpiringNotification>(
            HubMethods.Agent.TokenExpiring,
            async notification => await RefreshTokenAsync(notification).ConfigureAwait(false));

        _connection.On<ErrorNotification>(
            HubMethods.Agent.Error,
            notification => Complain($"hub: {notification.Code} — {notification.Message}"));
    }

    /// <summary>
    /// Answers an attach with either what the client missed or the screen as it stands.
    /// <para>
    /// The resize comes first and on purpose. The phone is authoritative while
    /// attached (spec §4.7), so its geometry reaches the real pseudoconsole before
    /// the screen is captured — capturing first would send a snapshot at the desk's
    /// width that the client is about to render at its own, and every wrapped line
    /// would sit in the wrong place until the program next redrew.
    /// </para>
    /// <para>
    /// A resize also disqualifies the fast path. The frames a returning client missed
    /// were produced for the old geometry, so replaying them onto a terminal that has
    /// since been reshaped would put wrapped lines in the wrong place just as surely.
    /// Reshaping is rare and repainting is cheap, so the simple rule wins.
    /// </para>
    /// </summary>
    private async Task OnAttachRequestedAsync(AttachRequestedNotification notification)
    {
        if (!_sessions.TryGet(notification.SessionId, out TerminalSession session))
        {
            Complain($"hub: cannot attach session {notification.SessionId}, which is not running here.");
            return;
        }

        bool reshaped = notification.Cols > 0
            && notification.Rows > 0
            && (session.Cols != notification.Cols || session.Rows != notification.Rows);

        if (notification.Cols > 0 && notification.Rows > 0)
        {
            await RouteAsync(
                notification.SessionId,
                () => _sessions.ResizeAsync(
                    notification.SessionId,
                    notification.Cols,
                    notification.Rows)).ConfigureAwait(false);
        }

        bool resumed = false;

        await RouteAsync(
            notification.SessionId,
            () => session.RunExclusiveAsync(async () =>
            {
                if (!reshaped
                    && notification.LastSeq is long lastSeq
                    && session.Tail.TryReplayFrom(lastSeq, out IReadOnlyList<TailFrame> missed))
                {
                    resumed = true;

                    foreach (TailFrame frame in missed)
                    {
                        // Resent with their original numbers, so the client sees an
                        // unbroken run and does not report a gap for output it is in
                        // the middle of receiving. Addressed to the client that asked,
                        // because anyone else watching never missed them and would
                        // apply them a second time.
                        await TransmitAsync(
                            session.SessionId,
                            frame.Seq,
                            frame.Kind,
                            frame.Data,
                            notification.ClientConnectionId,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    return;
                }

                await SendSnapshotAsync(session, notification.ClientConnectionId).ConfigureAwait(false);
            })).ConfigureAwait(false);

        Log(resumed
            ? $"hub: client resumed session {notification.SessionId}."
            : $"hub: client attached to session {notification.SessionId}.");
    }

    /// <summary>
    /// Sends one client the current screen, in as many frames as it takes.
    /// <para>
    /// A snapshot is produced in one piece and can be larger than a single message may
    /// be, so it is cut at points where a terminal is between sequences. Only the first
    /// frame is a <see cref="TerminalOutputKind.Snapshot"/>: that is what tells the
    /// client to clear what it has, and the rest paint on top of the cleared screen.
    /// </para>
    /// <para>
    /// The frames carry the session's current sequence number rather than new ones.
    /// They are not part of the shared stream — nobody else receives them — so
    /// numbering them would leave every other watcher with a gap it would report as
    /// lost output.
    /// </para>
    /// <para>
    /// The caller holds the session's exclusive region, so live output cannot arrive
    /// between two frames of the same snapshot.
    /// </para>
    /// </summary>
    private async ValueTask SendSnapshotAsync(TerminalSession session, string? targetConnectionId)
    {
        // Everything already applied to the screen being captured goes out first, as
        // ordinary numbered output. Holding it back would strand the other watchers,
        // and sending it afterwards would draw it twice on top of a screen that
        // already shows it. What stays behind is the tail of a sequence the emulator
        // has not dispatched yet, which is therefore not in the snapshot and is
        // correctly delivered later.
        while (session.Output.TryTake(out byte[] pending))
        {
            await SendOutputAsync(session, pending, TerminalOutputKind.Delta, CancellationToken.None)
                .ConfigureAwait(false);
        }

        byte[] snapshot = session.Screen.Snapshot();
        IReadOnlyList<byte[]> frames = VtChunker.Split(snapshot, OutputCoalescer.MaxFrameBytes);
        long seq = session.Tail.LastSeq;

        for (int i = 0; i < frames.Count; i++)
        {
            await TransmitAsync(
                session.SessionId,
                seq,
                i == 0 ? TerminalOutputKind.Snapshot : TerminalOutputKind.Delta,
                frames[i],
                targetConnectionId,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Announces this machine and everything already running on it.
    /// <para>
    /// The session republish is not belt and braces: sessions opened while the hub was
    /// unreachable were never reported, and the hub forgets a machine's sessions the
    /// moment its agent drops. Without this, a reconnect would show an online machine
    /// with nothing on it.
    /// </para>
    /// </summary>
    private async Task RegisterAsync(CancellationToken cancellationToken)
    {
        try
        {
            ErrorNotification? error = await _connection.InvokeAsync<ErrorNotification?>(
                HubMethods.Server.RegisterMachine,
                new RegisterMachineRequest
                {
                    MachineId = _identity.MachineId,
                    DisplayName = _identity.DisplayName,
                    Os = RuntimeInformation.OSDescription,
                    AgentVersion = AgentVersion,
                    ProtocolVersion = ProtocolVersion.Current,
                },
                cancellationToken).ConfigureAwait(false);

            if (error is not null)
            {
                Complain($"hub: refused this machine ({error.Code}) — {error.Message}");
                return;
            }

            _lastComplaint = null;
            Log($"hub: registered as {_identity.DisplayName}.");

            foreach (TerminalSession session in _sessions.Snapshot())
            {
                await TryInvokeAsync(
                    HubMethods.Server.SessionOpened,
                    new AgentSessionOpenedNotification { Session = Describe(session) },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Complain($"hub: registration failed ({ex.Message}).");
        }
    }

    /// <summary>
    /// Answers the hub's warning with a fresh token.
    /// <para>
    /// The agent is the end that must never need a person: it runs unattended on a
    /// machine whose owner is, by definition, somewhere else. A token it failed to
    /// renew would drop the connection and take every session on the machine off the
    /// air until somebody walked back to the desk — so a failure here is reported
    /// loudly rather than silently, even though the reconnect loop will also try.
    /// </para>
    /// </summary>
    private async Task RefreshTokenAsync(TokenExpiringNotification notification)
    {
        string? token;

        try
        {
            token = await _tokenProvider(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Complain($"hub: could not renew the access token ({ex.Message}). Run '1remote login'.");
            return;
        }

        if (string.IsNullOrEmpty(token))
        {
            Complain(
                $"hub: the access token expires at {notification.ExpiresAt.ToLocalTime():HH:mm} " +
                "and could not be renewed. Run '1remote login'.");
            return;
        }

        await TryInvokeAsync(
            HubMethods.Server.RefreshToken,
            new RefreshTokenRequest { Token = token },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TryInvokeAsync<T>(string method, T argument, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            ErrorNotification? error = await _connection
                .InvokeAsync<ErrorNotification?>(method, argument, cancellationToken)
                .ConfigureAwait(false);

            if (error is not null)
            {
                Complain($"hub: {method} refused ({error.Code}) — {error.Message}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Complain($"hub: {method} failed ({ex.Message}).");
        }
    }

    /// <summary>
    /// Delivers one inbound request to a session.
    /// <para>
    /// An exception thrown here would tear down the connection for every other
    /// session on the machine, so an unknown session is reported and swallowed. It is
    /// a normal race: a phone can press a key in the instant after a program exits.
    /// </para>
    /// </summary>
    private async Task RouteAsync(string sessionId, Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (UnknownSessionException)
        {
            Complain($"hub: ignoring a request for session {sessionId}, which is not running here.");
        }
        catch (Exception ex)
        {
            Complain($"hub: could not deliver to session {sessionId} ({ex.Message}).");
        }
    }

    private static SessionInfo Describe(TerminalSession session) => new()
    {
        SessionId = session.SessionId,
        Program = session.Program,
        Args = [.. session.Args],
        Cwd = session.Cwd,
        Cols = session.Cols,
        Rows = session.Rows,
        StartedAt = session.StartedUtc,
        DisplayName = session.DisplayName,
    };

    private static string AgentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static TimeSpan Backoff(TimeSpan current) =>
        TimeSpan.FromTicks(Math.Min(current.Ticks * 2, MaximumRetry.Ticks));

    private void Log(string message) => _log?.Invoke(message);

    /// <summary>Logs a problem, but only when it is not the one already being complained about.</summary>
    private void Complain(string message)
    {
        if (_lastComplaint == message)
        {
            return;
        }

        _lastComplaint = message;
        _log?.Invoke(message);
    }

    private async Task StopQuietlyAsync()
    {
        try
        {
            await _connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down. A hub that will not say goodbye politely is not a problem
            // worth reporting to someone who has already pressed Ctrl+C.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopQuietlyAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
