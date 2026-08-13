using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Hub;
using OneRemoteCli.Daemon.Ipc;
using OneRemoteCli.Daemon.Pty;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Protocol.Hub;
using OneRemoteCli.Terminal.Screen;
using OneRemoteCli.Terminal.Vt;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Stands the whole product up inside one test process: the real hub on a real
/// socket, the real agent connected to it, a real pseudoconsole running a real shell,
/// and a SignalR client standing in for the phone.
/// <para>
/// The <b>only</b> thing faked is the JWT signature check, replaced by
/// <see cref="TestTokenHandler"/>. Everything downstream of it is production code —
/// including <see cref="UserKey"/> derivation and the <see cref="AccountAllowlist"/> —
/// so the isolation guarantee is exercised rather than assumed. Minting real Entra
/// tokens would be testing Microsoft's crypto, which is not this test's job and which
/// <c>TokenValidationTests</c> already covers at the right level.
/// </para>
/// </summary>
internal sealed class EndToEndHarness : IAsyncDisposable
{
    /// <summary>How long to wait for something that ought to take milliseconds.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stopping = new();
    private readonly List<PhoneClient> _phones = [];
    private readonly List<WrappedShell> _shells = [];
    private readonly List<Task> _running = [];
    private readonly string _identityPath =
        Path.Combine(Path.GetTempPath(), $"1remote-e2e-{Guid.NewGuid():n}.json");

    private WebApplication? _hub;
    private AgentHost? _host;
    private AgentHubClient? _agentClient;
    private string _pipeName = string.Empty;
    private int _hubPort;

    private EndToEndHarness()
    {
    }

    public Uri HubUri { get; private set; } = null!;

    /// <summary>The machine the agent published.</summary>
    public string MachineId { get; private set; } = null!;

    /// <summary>
    /// The agent's own sessions, so a test can compare what the phone was sent against
    /// the screen the agent believes it has — the only ground truth available here.
    /// </summary>
    public SessionRegistry Sessions { get; private set; } = null!;

    /// <summary>
    /// Brings up the hub and the agent, but no sessions.
    /// <para>
    /// Sessions are the test's to open, because half of what needs proving is what a
    /// phone is told about a session that starts <em>while it is watching</em> — and a
    /// session the harness opened before the phone connected can never show that.
    /// </para>
    /// </summary>
    public static async Task<EndToEndHarness> StartAsync()
    {
        var harness = new EndToEndHarness();

        try
        {
            await harness.StartHubAsync().ConfigureAwait(false);
            await harness.StartAgentAsync().ConfigureAwait(false);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartHubAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // A port chosen once and reused, so the hub can be restarted at the same
        // address. Picked from the ephemeral range rather than fixed, because tests
        // running side by side must not fight over it.
        _hubPort = _hubPort == 0 ? FreePort() : _hubPort;

        builder.WebHost.UseUrls($"http://127.0.0.1:{_hubPort}");
        builder.Logging.ClearProviders();

        builder.Services.Configure<EntraOptions>(options =>
        {
            options.RequiredScope = TestIdentities.Scope;
            options.Allowlist = [TestIdentities.Owner.UserKey, TestIdentities.Stranger.UserKey];
        });

        builder.Services.AddSingleton(sp =>
            new AccountAllowlist(sp.GetRequiredService<IOptions<EntraOptions>>().Value.Allowlist));

        builder.Services
            .AddAuthentication(TestTokenHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestTokenHandler>(TestTokenHandler.SchemeName, _ => { });

        builder.Services.AddAuthorization();

        // The real registry and the real hub, wired the way Program.cs wires them.
        builder.Services.AddSingleton<RelayRegistry>();
        builder.Services.AddSingleton<OutboundLimits>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<OutboundFanout>();

        // The token lifetime machinery, with the one seam a test can hold: the
        // validator, because the harness deliberately does not sign real JWTs.
        builder.Services.AddSingleton<ConnectionTokens>();
        builder.Services.AddSingleton<IAccessTokenValidator>(sp => new TestTokenValidator(
            sp.GetRequiredService<AccountAllowlist>(),
            sp.GetRequiredService<IOptions<EntraOptions>>().Value.RequiredScope));
        builder.Services.AddSingleton<TokenExpirySweeper>();

        // Push, which the hub depends on but this harness never exercises: the
        // notification path has its own tests, and a real push service has no place
        // in an end-to-end test of the terminal. The queue is left unread, which is
        // exactly what a hub with no VAPID keys does in production.
        builder.Services.AddSingleton<PushSubscriptionStore>();
        builder.Services.AddSingleton<IPushNotifier, DiscardingNotifier>();

        builder.Services.AddSignalR(RelayLiveness.Apply).AddMessagePackProtocol();

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<RelayHub>("/hub");

        await app.StartAsync().ConfigureAwait(false);

        _hub = app;
        HubUri = new Uri(new Uri(app.Urls.First()), "hub");

        // Held so a test can drive one sweep rather than wait thirty seconds for the
        // hosted one — which the harness deliberately does not start.
        Tokens = app.Services.GetRequiredService<ConnectionTokens>();
        Sweeper = app.Services.GetRequiredService<TokenExpirySweeper>();
    }

    /// <summary>The hub's view of when each connection's token runs out.</summary>
    public ConnectionTokens Tokens { get; private set; } = null!;

    /// <summary>The thing that warns and then disconnects. Driven a pass at a time.</summary>
    public TokenExpirySweeper Sweeper { get; private set; } = null!;

    /// <summary>
    /// Takes the hub down and brings a new one up at the same address.
    /// <para>
    /// The registry is in memory, so the replacement knows nothing about anybody. That
    /// is the point: it is exactly the state a deployment leaves behind, and the agent
    /// has to put itself back without anyone touching the desk.
    /// </para>
    /// </summary>
    public async Task RestartHubAsync()
    {
        WebApplication? old = _hub;
        _hub = null;

        if (old is not null)
        {
            await old.StopAsync().ConfigureAwait(false);
            await old.DisposeAsync().ConfigureAwait(false);
        }

        await StartHubAsync().ConfigureAwait(false);
    }

    /// <summary>Asks the operating system for a port nobody is using.</summary>
    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    private async Task StartAgentAsync()
    {
        var identity = new MachineIdentity(Guid.NewGuid().ToString("n"), "desk");
        identity.Save(_identityPath);
        MachineId = identity.MachineId;

        var sessions = new SessionRegistry();
        Sessions = sessions;

        _agentClient = new AgentHubClient(
            HubUri,
            identity,
            sessions,
            _ => Task.FromResult<string?>(TestIdentities.Owner.Token),
            log: message => Console.WriteLine($"[agent] {message}"));

        // A unique pipe name, because the production one is per user and two of these
        // running at once would collide on FirstPipeInstance.
        var server = new AgentPipeServer($"1remote-e2e-{Guid.NewGuid():n}");
        _pipeName = server.PipeName;
        _host = new AgentHost(identity, sessions, _agentClient, server);

        _running.Add(_host.RunAsync(_stopping.Token));
        _running.Add(_agentClient.RunAsync(_stopping.Token));

        await WaitUntilAsync(() => _agentClient.IsConnected, "the agent to reach the hub").ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a real shell under a real pseudoconsole, wrapped and connected to the
    /// agent over the real named pipe — the same path <c>1remote run</c> takes.
    /// </summary>
    public async Task<WrappedShell> StartShellAsync(string commandLine = "cmd.exe", string? displayName = null)
    {
        var desk = new FakeLocalTerminal(cols: 80, rows: 25);

        // cmd.exe rather than PowerShell: it starts in a fraction of the time and
        // prints a prompt without loading a profile, which keeps these tests about the
        // transport rather than about shell startup.
        PseudoConsoleSession pty = PseudoConsoleSession.Start(
            commandLine,
            Path.GetTempPath(),
            desk.Cols,
            desk.Rows);

        AgentPipeClient pipe = await AgentPipeClient.ConnectAsync(
            _pipeName,
            cancellationToken: _stopping.Token).ConfigureAwait(false);

        string sessionId = await pipe.OpenSessionAsync(
            new SessionStartInfo(commandLine, [], Path.GetTempPath(), desk.Cols, desk.Rows, displayName),
            _stopping.Token).ConfigureAwait(false);

        var wrapper = new WrapperSession(pty, desk, pipe, message => Console.WriteLine($"[wrapper] {message}"));
        Task run = wrapper.RunAsync(_stopping.Token);

        var shell = new WrappedShell(sessionId, MachineId, pty, pipe, desk, run);
        _shells.Add(shell);

        return shell;
    }

    /// <summary>Connects a client the way the PWA does, handshake included.</summary>
    public async Task<PhoneClient> ConnectPhoneAsync(TestIdentity? identity = null)
    {
        PhoneClient phone = NewPhone(identity ?? TestIdentities.Owner);
        await phone.StartAsync().ConfigureAwait(false);
        return phone;
    }

    /// <summary>Creates a client without connecting, for tests about admission rather than protocol.</summary>
    public PhoneClient NewPhone(TestIdentity identity) => NewPhone(identity.Token);

    /// <summary>Creates a client holding a specific token, for tests about its lifetime.</summary>
    public PhoneClient NewPhone(string token)
    {
        var phone = new PhoneClient(HubUri, token);
        _phones.Add(phone);
        return phone;
    }

    /// <summary>Ends the shell the way a user would, and reports the exit code it produced.</summary>
    public static async Task<int> ExitShellAsync(WrappedShell shell, int exitCode)
    {
        ArgumentNullException.ThrowIfNull(shell);

        await shell.Pty.WriteAsync($"exit {exitCode}\r").ConfigureAwait(false);
        return await shell.Pty.Exited.WaitAsync(Patience).ConfigureAwait(false);
    }

    /// <summary>Polls until a condition holds, and says what it was waiting for if it never does.</summary>
    public static Task WaitUntilAsync(Func<bool> condition, string what)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return WaitUntilAsync(() => Task.FromResult(condition()), what);
    }

    /// <summary>Polls an asynchronous condition, for checks that need a round trip to the hub.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        ArgumentNullException.ThrowIfNull(condition);

        DateTime deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out after {Patience.TotalSeconds:0}s waiting for {what}.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (PhoneClient phone in _phones)
        {
            await Quietly(async () => await phone.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        // The pseudoconsoles go first and deliberately so: each wrapper's run loop is
        // parked on its child's exit, and cancellation alone will not wake it. Killing
        // the terminal is what lets everything above it unwind.
        foreach (WrappedShell shell in _shells)
        {
            await Quietly(async () => await shell.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        if (_host is not null)
        {
            await Quietly(async () => await _host.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        if (_agentClient is not null)
        {
            await Quietly(async () => await _agentClient.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        await Task.WhenAny(Task.WhenAll(_running), Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);

        if (_hub is not null)
        {
            await Quietly(async () => await _hub.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        _stopping.Dispose();

        try
        {
            File.Delete(_identityPath);
        }
        catch (IOException)
        {
        }
    }

    private static async Task Quietly(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown of a harness whose test has already reported its result is not
            // worth failing over; an error here would only mask the real outcome.
        }
    }
}

/// <summary>
/// One wrapped shell: the pseudoconsole, the pipe to the agent, and the desk terminal
/// it is teeing to.
/// </summary>
internal sealed record WrappedShell(
    string SessionId,
    string MachineIdHint,
    PseudoConsoleSession Pty,
    AgentPipeClient Pipe,
    FakeLocalTerminal Desk,
    Task Run) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        // Order matters: killing the pseudoconsole is what ends the wrapper's output
        // pump, and the pipe must outlive it long enough for the close notification.
        await Pty.DisposeAsync().ConfigureAwait(false);
        await Task.WhenAny(Run, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        await Pipe.DisposeAsync().ConfigureAwait(false);
        Desk.Dispose();
    }
}

/// <summary>An identity the test can sign in as.</summary>
internal sealed record TestIdentity(string TenantId, string ObjectId, string Username, string Scopes)
{
    public string UserKey => $"{TenantId}:{ObjectId}";

    /// <summary>
    /// The claims, packed into an opaque string. Deliberately not a JWT: this is the
    /// seam where a real deployment checks a signature, and forging one here would
    /// prove nothing the hub's own token tests do not already prove.
    /// </summary>
    public string Token => $"{TenantId}|{ObjectId}|{Username}|{Scopes}";

    /// <summary>The same identity, in a token that runs out at a stated moment.</summary>
    public string TokenExpiringAt(DateTimeOffset expiresAt) =>
        $"{Token}|{expiresAt.ToUnixTimeSeconds()}";
}

/// <summary>
/// Stands in for the Entra check on the refresh path.
/// <para>
/// It runs the <b>real</b> allowlist against the claims the test token carries, so what
/// it proves about identity is what production proves; only the signature check, which
/// the hub's own token tests cover, is absent.
/// </para>
/// </summary>
internal sealed class TestTokenValidator(AccountAllowlist allowlist, string requiredScope) : IAccessTokenValidator
{
    public Task<TokenReview> ReviewAsync(string token, CancellationToken cancellationToken = default)
    {
        string[] parts = (token ?? string.Empty).Split('|');

        if (parts.Length is not (4 or 5))
        {
            return Task.FromResult(TokenReview.Rejected("Malformed test token."));
        }

        List<Claim> claims =
        [
            new Claim(UserKey.TenantIdClaim, parts[0]),
            new Claim(UserKey.ObjectIdClaim, parts[1]),
            new Claim(UserKey.PreferredUsernameClaim, parts[2]),
            new Claim(UserKey.ScopeClaim, parts[3]),
        ];

        if (parts.Length == 5)
        {
            claims.Add(new Claim(TokenExpiry.ExpiryClaim, parts[4]));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, TestTokenHandler.SchemeName));
        AccessResult result = allowlist.Check(principal, requiredScope);

        return Task.FromResult(result.IsAllowed
            ? TokenReview.Accepted(result.Key!, TokenExpiry.Of(principal))
            : TokenReview.Rejected(result.Reason ?? "Refused."));
    }
}

internal static class TestIdentities
{
    public const string Scope = "Session.Access";

    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    /// <summary>Whoever is running the agent.</summary>
    public static readonly TestIdentity Owner =
        new(TenantA, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "owner@example.com", Scope);

    /// <summary>A different person, allowlisted, who must still see nothing of the owner's.</summary>
    public static readonly TestIdentity Stranger =
        new(TenantB, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "stranger@example.com", Scope);

    /// <summary>A genuine identity that is simply not on this hub's list.</summary>
    public static readonly TestIdentity Uninvited =
        new(TenantA, "cccccccc-cccc-cccc-cccc-cccccccccccc", "uninvited@example.com", Scope);

    /// <summary>Allowlisted, but holding a token that does not carry the scope the hub requires.</summary>
    public static readonly TestIdentity Unscoped =
        new(TenantA, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "owner@example.com", "User.Read");
}

/// <summary>
/// Turns the test's opaque token into claims, then runs the <b>real</b> admission check.
/// <para>
/// It mirrors the production handler's two token sources on purpose: the Authorization
/// header, and the <c>access_token</c> query parameter SignalR falls back to because a
/// browser cannot set headers on a WebSocket handshake. That fallback is a routine
/// source of "works locally, 401 in production", so a test that only exercised the
/// header would be testing the easy half.
/// </para>
/// </summary>
internal sealed class TestTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccountAllowlist allowlist,
    IOptions<EntraOptions> entra) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();

        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : Request.Query["access_token"].FirstOrDefault();

        if (string.IsNullOrEmpty(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string[] parts = token.Split('|');

        if (parts.Length is not (4 or 5))
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed test token."));
        }

        List<Claim> claims =
        [
            new Claim(UserKey.TenantIdClaim, parts[0]),
            new Claim(UserKey.ObjectIdClaim, parts[1]),
            new Claim(UserKey.PreferredUsernameClaim, parts[2]),
            new Claim(UserKey.ScopeClaim, parts[3]),
        ];

        if (parts.Length == 5)
        {
            claims.Add(new Claim(TokenExpiry.ExpiryClaim, parts[4]));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));

        AccessResult result = allowlist.Check(principal, entra.Value.RequiredScope);

        return Task.FromResult(result.IsAllowed
            ? AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName))
            : AuthenticateResult.Fail(result.Reason));
    }
}

/// <summary>
/// The phone: a plain SignalR client speaking the protocol the PWA speaks, so what it
/// proves about the hub is what a browser would find.
/// </summary>
internal sealed class PhoneClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly StringBuilder _screen = new();
    private readonly List<long> _sequences = [];
    private readonly List<OutputFrame> _frames = [];
    private readonly List<ClientSessionOpenedNotification> _opened = [];
    private readonly List<ClientSessionClosedNotification> _closed = [];
    private readonly List<DateTimeOffset> _expiryWarnings = [];
    private readonly object _gate = new();

    public PhoneClient(Uri hubUri, TestIdentity identity)
        : this(hubUri, (identity ?? throw new ArgumentNullException(nameof(identity))).Token)
    {
    }

    public PhoneClient(Uri hubUri, string token)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options => options.AccessTokenProvider =
                () => Task.FromResult<string?>(token))
            .AddMessagePackProtocol()
            .Build();

        _connection.On<TokenExpiringNotification>(HubMethods.Client.TokenExpiring, notification =>
        {
            lock (_gate)
            {
                _expiryWarnings.Add(notification.ExpiresAt);
            }
        });

        _connection.On<TerminalOutputNotification>(HubMethods.Client.TerminalOutput, notification =>
        {
            lock (_gate)
            {
                _sequences.Add(notification.Seq);
                _frames.Add(new OutputFrame(notification.Seq, notification.Kind, notification.Data));
                _screen.Append(Encoding.UTF8.GetString(notification.Data));
            }
        });

        _connection.On<ClientSessionOpenedNotification>(HubMethods.Client.SessionOpened, notification =>
        {
            lock (_gate)
            {
                _opened.Add(notification);
            }
        });

        _connection.On<ClientSessionClosedNotification>(HubMethods.Client.SessionClosed, notification =>
        {
            lock (_gate)
            {
                _closed.Add(notification);
            }
        });
    }

    public IReadOnlyList<ClientSessionOpenedNotification> Opened
    {
        get
        {
            lock (_gate)
            {
                return [.. _opened];
            }
        }
    }

    public IReadOnlyList<ClientSessionClosedNotification> Closed
    {
        get
        {
            lock (_gate)
            {
                return [.. _closed];
            }
        }
    }

    /// <summary>Everything this client has been shown, decoded as text.</summary>
    public string Screen
    {
        get
        {
            lock (_gate)
            {
                return _screen.ToString();
            }
        }
    }

    public IReadOnlyList<long> Sequences
    {
        get
        {
            lock (_gate)
            {
                return [.. _sequences];
            }
        }
    }

    /// <summary>
    /// Every frame in the order it arrived, kind included.
    /// <para>
    /// Order is the point. A snapshot is only correct relative to a position in the
    /// delta stream, so a test that only checked the bytes arrived would pass on a
    /// stream that renders wrongly.
    /// </para>
    /// </summary>
    public IReadOnlyList<OutputFrame> Frames
    {
        get
        {
            lock (_gate)
            {
                return [.. _frames];
            }
        }
    }

    /// <summary>Replays everything received into a screen, the way the phone's emulator does.</summary>
    public string Render(int cols, int rows)
    {
        TerminalScreen screen = new(rows, cols);
        VtParser parser = new();

        foreach (OutputFrame frame in Frames)
        {
            if (frame.Kind == TerminalOutputKind.Snapshot)
            {
                // Matches the client: a snapshot replaces the screen rather than
                // drawing over it. Compositing would leave old text showing wherever
                // the new screen is blank.
                screen.FullReset();
            }

            parser.Parse(frame.Data, screen);
        }

        return screen.GetText();
    }

    /// <summary>Opens the socket without handshaking.</summary>
    public Task ConnectAsync() => _connection.StartAsync();

    public async Task StartAsync()
    {
        await ConnectAsync().ConfigureAwait(false);

        ErrorNotification? refusal = await HandshakeAsync().ConfigureAwait(false);

        if (refusal is not null)
        {
            throw new InvalidOperationException($"Handshake refused: {refusal.Code} — {refusal.Message}");
        }
    }

    /// <summary>Presents a new token on the live connection.</summary>
    public Task<ErrorNotification?> RefreshTokenAsync(string token) =>
        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RefreshToken,
            new RefreshTokenRequest { Token = token });

    /// <summary>Every expiry warning the hub has sent, in order.</summary>
    public IReadOnlyList<DateTimeOffset> ExpiryWarnings
    {
        get { lock (_gate) { return [.. _expiryWarnings]; } }
    }

    /// <summary>Whether the socket is still up. Aborts show here.</summary>
    public HubConnectionState ConnectionState => _connection.State;

    /// <summary>The hub's id for this connection, which is how the hub keys everything.</summary>
    public string ConnectionId => _connection.ConnectionId
        ?? throw new InvalidOperationException("The phone is not connected.");

    public Task<ErrorNotification?> HandshakeAsync(int protocolVersion = Protocol.ProtocolVersion.Current) =>        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ClientHandshake,
            new ClientHandshakeRequest
            {
                ProtocolVersion = protocolVersion,
                ClientVersion = "test/1.0",
            });

    public Task<MachineListNotification> ListMachinesAsync() =>
        _connection.InvokeAsync<MachineListNotification>(HubMethods.Server.ListMachines);

    public Task<ErrorNotification?> AttachAsync(
        string machineId,
        string sessionId,
        int cols = 80,
        int rows = 25,
        long? lastSeq = null) =>
        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.AttachSession,
            new AttachSessionRequest
            {
                MachineId = machineId,
                SessionId = sessionId,
                Cols = cols,
                Rows = rows,
                LastSeq = lastSeq,
            });

    /// <summary>The highest sequence this client has seen, which is what it resumes from.</summary>
    public long? LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _sequences.Count == 0 ? null : _sequences[^1];
            }
        }
    }

    public Task<ErrorNotification?> DetachAsync(string sessionId) =>
        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DetachSession,
            new DetachSessionRequest { SessionId = sessionId });

    public Task<ErrorNotification?> TypeAsync(string sessionId, string text) =>
        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendInput,
            new SendInputRequest { SessionId = sessionId, Data = Encoding.UTF8.GetBytes(text) });

    public Task<ErrorNotification?> ResizeAsync(string sessionId, int cols, int rows) =>
        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ResizeTerminal,
            new ResizeTerminalRequest { SessionId = sessionId, Cols = cols, Rows = rows });

    public Task<ErrorNotification?> InterruptAsync(string sessionId) =>
        _connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.InterruptSession,
            new InterruptSessionRequest { SessionId = sessionId });

    /// <summary>Waits for text to appear on this client's screen.</summary>
    public Task WaitForScreenAsync(string text) =>
        EndToEndHarness.WaitUntilAsync(
            () => Screen.Contains(text, StringComparison.OrdinalIgnoreCase),
            $"'{text}' to appear on the phone");

    /// <summary>Forgets everything shown so far, so the next assertion is unambiguous.</summary>
    public void ClearScreen()
    {
        lock (_gate)
        {
            _screen.Clear();
            _frames.Clear();
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);
}

/// <summary>One frame as the phone received it.</summary>
internal sealed record OutputFrame(long Seq, TerminalOutputKind Kind, byte[] Data);

/// <summary>
/// The desk terminal, in memory. Real enough for the tee to be observable: the
/// product's central promise is that the phone and the desk see the same bytes, and
/// that cannot be checked if one of the two ends is a null sink.
/// </summary>
internal sealed class FakeLocalTerminal(int cols, int rows) : ILocalTerminal
{
    private readonly CapturingStream _output = new();

    public int Cols { get; } = cols;

    public int Rows { get; } = rows;

    /// <summary>
    /// Never produces anything. A real console read blocks on a keypress that will
    /// never come in a test, and the wrapper's input pump is a background thread it
    /// deliberately abandons — so a stream that simply parks is the faithful shape.
    /// Returning EOF instead would exercise a path the product never takes.
    /// </summary>
    public Stream Input { get; } = new ParkedStream();

    public Stream Output => _output;

    /// <summary>What the user at the desk would be looking at.</summary>
    public string Screen => _output.Text;

    public void Dispose()
    {
        Input.Dispose();
        _output.Dispose();
    }

    private sealed class CapturingStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly object _gate = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return Encoding.UTF8.GetString(_buffer.ToArray());
                }
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                _buffer.Write(buffer, offset, count);
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    _buffer.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ParkedStream : Stream
    {
        private readonly ManualResetEventSlim _released = new(false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _released.Wait();
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Released but never disposed: the pump thread is still parked inside
                // Wait, and disposing the event out from under it would throw on a
                // thread whose only remaining job is to notice that it should stop.
                _released.Set();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Accepts notifications and drops them: the harness has no phone to wake.</summary>
internal sealed class DiscardingNotifier : IPushNotifier
{
    public void Enqueue(string userKey, PushPayload payload)
    {
    }
}

