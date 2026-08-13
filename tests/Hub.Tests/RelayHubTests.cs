using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The relay end to end: a real hub, real SignalR connections, real MessagePack.
/// <para>
/// Everything below runs against the actual <c>Program</c>, with only the token
/// validation swapped for a header — because what is being tested here is routing,
/// and token validation has its own tests. The identity still arrives the same way it
/// would in production: as claims on the connection's principal, never as a method
/// parameter.
/// </para>
/// </summary>
public sealed class RelayHubTests : IAsyncLifetime
{
    private const string AliceTenant = "11111111-1111-1111-1111-111111111111";
    private const string AliceObject = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string BobTenant = "22222222-2222-2222-2222-222222222222";
    private const string BobObject = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private WebApplicationFactory<Program> _factory = null!;
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityHandler>(
                        HeaderIdentityHandler.SchemeName,
                        _ => { });
            }));

        // Forces the server to start so Server.BaseAddress and CreateHandler work.
        _factory.CreateClient().Dispose();

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (HubConnection connection in _connections)
        {
            await connection.DisposeAsync();
        }

        _factory.Dispose();
    }

    [Fact]
    public async Task AnAgentRegistersAMachineAndTheOwnerSeesIt()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a", "Alice's desktop");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        MachineInfo machine = Assert.Single(list.Machines);
        Assert.Equal("machine-a", machine.MachineId);
        Assert.Equal("Alice's desktop", machine.DisplayName);
        Assert.True(machine.Online);

        SessionInfo session = Assert.Single(machine.Sessions);
        Assert.Equal("session-1", session.SessionId);
        Assert.Equal("pwsh", session.Program);
    }

    [Fact]
    public async Task AnotherUserSeesNothingAndCannotAddressWhatTheyCannotSee()
    {
        // The acceptance test for the whole partitioning design. Bob is a perfectly
        // valid, fully signed-in user who has been handed Alice's real machine and
        // session ids — the situation a leaked id or a curious user actually creates.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        MachineListNotification list = await bob.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);
        Assert.Empty(list.Machines);

        ErrorNotification? attach = await bob.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.AttachSession,
            new AttachSessionRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                Cols = 80,
                Rows = 24,
            });

        Assert.Equal(ErrorCodes.MachineNotFound, attach!.Code);

        ErrorNotification? input = await bob.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendInput,
            new SendInputRequest { SessionId = "session-1", Data = "rm -rf /\r"u8.ToArray() });

        Assert.Equal(ErrorCodes.NotAttached, input!.Code);
    }

    [Fact]
    public async Task TheSameObjectIdInADifferentTenantIsADifferentPerson()
    {
        // oid alone is unique only within a tenant. If the hub keyed on it, these two
        // would share machines.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection impostor = await ConnectClientAsync(BobTenant, AliceObject);

        Assert.Empty(
            (await impostor.InvokeAsync<MachineListNotification>(HubMethods.Server.ListMachines)).Machines);
    }

    [Fact]
    public async Task InputTravelsFromTheClientToTheAgentThatOwnsTheSession()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<SendInputNotification> inputs = Listen<SendInputNotification>(agent, HubMethods.Agent.SendInput);
        Channel<AttachRequestedNotification> attaches =
            Listen<AttachRequestedNotification>(agent, HubMethods.Agent.AttachRequested);
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");

        AttachRequestedNotification attached = await Next(attaches);
        Assert.Equal("session-1", attached.SessionId);
        Assert.Equal(120, attached.Cols);

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendInput,
            new SendInputRequest { SessionId = "session-1", Data = "dir\r"u8.ToArray() }));

        SendInputNotification received = await Next(inputs);
        Assert.Equal("session-1", received.SessionId);
        Assert.Equal("dir\r"u8.ToArray(), received.Data);
    }

    [Fact]
    public async Task OutputReachesTheWatcherAndNobodyElse()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection watcher = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<TerminalOutputNotification> watched =
            Listen<TerminalOutputNotification>(watcher, HubMethods.Client.TerminalOutput);
        await AttachAsync(watcher, "machine-a", "session-1");

        // Same user, same hub, not attached: still must not receive terminal bytes.
        HubConnection bystander = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<TerminalOutputNotification> ignored =
            Listen<TerminalOutputNotification>(bystander, HubMethods.Client.TerminalOutput);

        await agent.InvokeAsync(
            HubMethods.Server.TerminalOutput,
            new TerminalOutputNotification
            {
                SessionId = "session-1",
                Seq = 1,
                Kind = TerminalOutputKind.Delta,
                Data = "hello"u8.ToArray(),
            });

        TerminalOutputNotification output = await Next(watched);
        Assert.Equal("hello"u8.ToArray(), output.Data);
        Assert.Equal(1, output.Seq);

        await AssertSilent(ignored);
    }

    [Fact]
    public async Task InterruptReachesTheAgent()
    {
        // The one action the product exists for.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<InterruptSessionNotification> interrupts =
            Listen<InterruptSessionNotification>(agent, HubMethods.Agent.InterruptSession);
        await OpenSessionAsync(agent, "session-1", "claude");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.InterruptSession,
            new InterruptSessionRequest { SessionId = "session-1" }));

        Assert.Equal("session-1", (await Next(interrupts)).SessionId);
    }

    [Fact]
    public async Task ResizeReachesTheAgentAndNonsenseDoesNot()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<ResizeTerminalNotification> resizes =
            Listen<ResizeTerminalNotification>(agent, HubMethods.Agent.ResizeTerminal);
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ResizeTerminal,
            new ResizeTerminalRequest { SessionId = "session-1", Cols = 60, Rows = 20 }));

        ResizeTerminalNotification resize = await Next(resizes);
        Assert.Equal(60, resize.Cols);
        Assert.Equal(20, resize.Rows);

        // A zero-column PTY is not a resize, it is a crash waiting for somewhere to
        // happen, so it is refused here rather than forwarded.
        ErrorNotification? refused = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ResizeTerminal,
            new ResizeTerminalRequest { SessionId = "session-1", Cols = 0, Rows = 20 });

        Assert.Equal(ErrorCodes.InvalidRequest, refused!.Code);
    }

    [Fact]
    public async Task ClientsLearnWhenSessionsAppearAndDisappear()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionOpenedNotification> opened =
            Listen<ClientSessionOpenedNotification>(client, HubMethods.Client.SessionOpened);
        Channel<ClientSessionClosedNotification> closed =
            Listen<ClientSessionClosedNotification>(client, HubMethods.Client.SessionClosed);

        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        ClientSessionOpenedNotification appeared = await Next(opened);
        Assert.Equal("machine-a", appeared.MachineId);
        Assert.Equal("session-1", appeared.Session.SessionId);

        await agent.InvokeAsync(
            HubMethods.Server.SessionClosed,
            new AgentSessionClosedNotification { SessionId = "session-1", ExitCode = 0 });

        Assert.Equal("session-1", (await Next(closed)).SessionId);
    }

    [Fact]
    public async Task ClientsLearnWhenAMachineComesOnlineAndGoesAway()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<MachineOnlineNotification> online =
            Listen<MachineOnlineNotification>(client, HubMethods.Client.MachineOnline);
        Channel<MachineOfflineNotification> offline =
            Listen<MachineOfflineNotification>(client, HubMethods.Client.MachineOffline);

        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");

        Assert.Equal("machine-a", (await Next(online)).Machine.MachineId);

        await agent.StopAsync();

        Assert.Equal("machine-a", (await Next(offline)).MachineId);

        MachineInfo machine = Assert.Single(
            (await client.InvokeAsync<MachineListNotification>(HubMethods.Server.ListMachines)).Machines);
        Assert.False(machine.Online);
    }

    [Fact]
    public async Task AnAwaitingInputAlertReachesEvenAnUnattachedClient()
    {
        // The point of the notification is to reach a phone that is not looking.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "claude");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionAwaitingInputNotification> alerts =
            Listen<ClientSessionAwaitingInputNotification>(client, HubMethods.Client.SessionAwaitingInput);

        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Allow file edit?" });

        ClientSessionAwaitingInputNotification alert = await Next(alerts);
        Assert.Equal("session-1", alert.SessionId);
        Assert.Equal("Allow file edit?", alert.Hint);
    }

    [Fact]
    public async Task AnAgentLeavingTellsTheAttachedClientOnce()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<MachineOfflineNotification> offline =
            Listen<MachineOfflineNotification>(client, HubMethods.Client.MachineOffline);
        await AttachAsync(client, "machine-a", "session-1");

        await agent.StopAsync();

        Assert.Equal("machine-a", (await Next(offline)).MachineId);

        // The attachment died with the machine, so driving it is refused rather than
        // silently dropped.
        ErrorNotification? refused = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendInput,
            new SendInputRequest { SessionId = "session-1", Data = "y\r"u8.ToArray() });

        Assert.Equal(ErrorCodes.NotAttached, refused!.Code);
    }

    [Fact]
    public async Task ADisconnectingClientIsDetachedOnItsBehalf()
    {
        // A phone in a tunnel never sends DetachSession.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<DetachRequestedNotification> detaches =
            Listen<DetachRequestedNotification>(agent, HubMethods.Agent.DetachRequested);
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");
        await client.StopAsync();

        Assert.Equal("session-1", (await Next(detaches)).SessionId);
    }

    [Fact]
    public async Task MovingBetweenSessionsDetachesTheOldOne()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<DetachRequestedNotification> detaches =
            Listen<DetachRequestedNotification>(agent, HubMethods.Agent.DetachRequested);
        await OpenSessionAsync(agent, "session-1", "pwsh");
        await OpenSessionAsync(agent, "session-2", "claude");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");
        await AttachAsync(client, "machine-a", "session-2");

        Assert.Equal("session-1", (await Next(detaches)).SessionId);
    }

    [Fact]
    public async Task ExplicitDetachStopsTheStream()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<DetachRequestedNotification> detaches =
            Listen<DetachRequestedNotification>(agent, HubMethods.Agent.DetachRequested);
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DetachSession,
            new DetachSessionRequest { SessionId = "session-1" }));

        Assert.Equal("session-1", (await Next(detaches)).SessionId);

        ErrorNotification? again = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DetachSession,
            new DetachSessionRequest { SessionId = "session-1" });

        Assert.Equal(ErrorCodes.NotAttached, again!.Code);
    }

    [Fact]
    public async Task AFutureProtocolIsTurnedAwayWithAnExplanation()
    {
        // The alternative is a confusing failure several messages later, when a field
        // this build has never heard of fails to deserialize.
        HubConnection agent = await ConnectAsync(AliceTenant, AliceObject);

        ErrorNotification? error = await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RegisterMachine,
            new RegisterMachineRequest
            {
                MachineId = "machine-a",
                DisplayName = "From the future",
                Os = "Windows",
                AgentVersion = "9.9.9",
                ProtocolVersion = ProtocolVersion.Current + 1,
            });

        Assert.Equal(ErrorCodes.UnsupportedProtocolVersion, error!.Code);
        Assert.Contains("Update 1RemoteCLI", error.Message, StringComparison.Ordinal);

        HubConnection client = await ConnectAsync(AliceTenant, AliceObject);
        ErrorNotification? handshake = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ClientHandshake,
            new ClientHandshakeRequest { ProtocolVersion = 99, ClientVersion = "9.9.9" });

        Assert.Equal(ErrorCodes.UnsupportedProtocolVersion, handshake!.Code);
    }

    [Fact]
    public async Task ASessionFromAnUnregisteredAgentIsRefused()
    {
        HubConnection agent = await ConnectAsync(AliceTenant, AliceObject);

        ErrorNotification? error = await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionOpened,
            new AgentSessionOpenedNotification { Session = NewSession("session-1", "pwsh") });

        Assert.Equal(ErrorCodes.MachineNotFound, error!.Code);
    }

    [Fact]
    public async Task AnUnauthenticatedConnectionIsRefused()
    {
        HubConnection connection = Build(headers => headers.Clear());

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    // Helpers.

    private async Task<HubConnection> ConnectAgentAsync(
        string tenantId,
        string objectId,
        string machineId,
        string displayName = "Machine")
    {
        HubConnection connection = await ConnectAsync(tenantId, objectId);

        ErrorNotification? error = await connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RegisterMachine,
            new RegisterMachineRequest
            {
                MachineId = machineId,
                DisplayName = displayName,
                Os = "Windows",
                AgentVersion = "1.0.0",
                ProtocolVersion = ProtocolVersion.Current,
            });

        Assert.Null(error);

        return connection;
    }

    private async Task<HubConnection> ConnectClientAsync(string tenantId, string objectId)
    {
        HubConnection connection = await ConnectAsync(tenantId, objectId);

        ErrorNotification? error = await connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ClientHandshake,
            new ClientHandshakeRequest { ProtocolVersion = ProtocolVersion.Current, ClientVersion = "1.0.0" });

        Assert.Null(error);

        return connection;
    }

    private async Task<HubConnection> ConnectAsync(string tenantId, string objectId)
    {
        HubConnection connection = Build(headers =>
        {
            headers[HeaderIdentityHandler.TenantHeader] = tenantId;
            headers[HeaderIdentityHandler.ObjectHeader] = objectId;
        });

        await connection.StartAsync();

        return connection;
    }

    private HubConnection Build(Action<IDictionary<string, string>> configureHeaders)
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();

                // Long polling, because the in-memory test server's WebSocket support
                // needs a bespoke factory and the transport is not what is under test.
                options.Transports = HttpTransportType.LongPolling;

                configureHeaders(options.Headers);
            })
            .AddMessagePackProtocol()
            .Build();

        _connections.Add(connection);

        return connection;
    }

    private static async Task OpenSessionAsync(HubConnection agent, string sessionId, string program) =>
        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionOpened,
            new AgentSessionOpenedNotification { Session = NewSession(sessionId, program) }));

    private static async Task AttachAsync(HubConnection client, string machineId, string sessionId) =>
        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.AttachSession,
            new AttachSessionRequest
            {
                MachineId = machineId,
                SessionId = sessionId,
                Cols = 120,
                Rows = 30,
            }));

    private static SessionInfo NewSession(string sessionId, string program) => new()
    {
        SessionId = sessionId,
        Program = program,
        Args = [],
        Cwd = @"C:\Projects\1RemoteCLI",
        Cols = 120,
        Rows = 30,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static Channel<T> Listen<T>(HubConnection connection, string method)
    {
        Channel<T> channel = Channel.CreateUnbounded<T>();
        connection.On<T>(method, message => channel.Writer.TryWrite(message));

        return channel;
    }

    private static async Task<T> Next<T>(Channel<T> channel)
    {
        using var timeout = new CancellationTokenSource(Patience);

        try
        {
            return await channel.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No {typeof(T).Name} arrived within {Patience}.");
        }
    }

    private static async Task AssertSilent<T>(Channel<T> channel)
    {
        // Long enough that a message that was going to arrive would have; the relay
        // does no batching, so anything routed here is dispatched immediately.
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        Assert.False(channel.Reader.TryRead(out _));
    }

    /// <summary>
    /// Stands in for Entra so the tests can be two different real users without a
    /// tenant. The claims it produces are exactly the ones a v2.0 access token carries
    /// and the hub reads, so the code under test cannot tell the difference.
    /// </summary>
    private sealed class HeaderIdentityHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestIdentity";
        public const string TenantHeader = "X-Test-Tid";
        public const string ObjectHeader = "X-Test-Oid";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(TenantHeader, out var tenantId) ||
                !Request.Headers.TryGetValue(ObjectHeader, out var objectId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim("tid", tenantId.ToString()),
                    new Claim("oid", objectId.ToString()),
                    new Claim("scp", "Session.Access"),
                    new Claim("preferred_username", $"{objectId}@example.test"),
                ],
                SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
