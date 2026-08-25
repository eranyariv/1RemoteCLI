using System.Security.Claims;
using System.Threading.Channels;
using OneRemoteCli.Hub.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OneRemoteCli.Hub.Push;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// When the hub decides to wake a phone.
/// <para>
/// Runs against the real hub with only the push sender replaced, because the
/// decision under test is about hub state - is anybody watching this session, and
/// whose phone is it - and that state is not reachable except through the hub. The
/// delivery itself is a third party's HTTP endpoint and is not what is being tested.
/// </para>
/// </summary>
public sealed class PushRoutingTests : IAsyncLifetime
{
    private const string AliceTenant = "11111111-1111-1111-1111-111111111111";
    private const string AliceObject = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string BobTenant = "22222222-2222-2222-2222-222222222222";
    private const string BobObject = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private WebApplicationFactory<Program> _factory = null!;
    private RecordingNotifier _pushes = null!;
    private PushSubscriptionStore _subscriptions = null!;
    private readonly List<HubConnection> _connections = [];

    public Task InitializeAsync()
    {
        _pushes = new RecordingNotifier();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services
                    .AddAuthentication(HeaderIdentityHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityHandler>(
                        HeaderIdentityHandler.SchemeName,
                        _ => { });

                services.AddSingleton<IPushNotifier>(_pushes);
            }));

        _factory.CreateClient().Dispose();
        _subscriptions = _factory.Services.GetRequiredService<PushSubscriptionStore>();

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
    public async Task AWaitingSessionNobodyIsWatchingWakesThePhone()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a", "Alice's desktop");
        await OpenSessionAsync(agent, "session-1", "claude");

        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Allow file edit?" });

        PushJob job = await Next();
        // Named by its program, because "claude is waiting" is the whole message and
        // the session id would mean nothing on a lock screen.
        Assert.Equal("claude is waiting", job.Payload.Title);
        Assert.Equal("Allow file edit?", job.Payload.Body);
        Assert.Equal("/?machine=machine-a&session=session-1", job.Payload.Url);
        Assert.True(job.Payload.Perishable);
    }

    [Fact]
    public async Task NobodyIsWokenAboutAScreenTheyAreAlreadyLookingAt()
    {
        // The whole justification for the feature is a phone in a pocket. Buzzing
        // about the terminal already open in the user's hand teaches them to ignore
        // notifications, which costs the ones that matter.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "claude");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");

        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Allow file edit?" });

        await AssertNoPush();
    }

    [Fact]
    public async Task DetachingMakesTheUserReachableAgain()
    {
        // Locking the phone detaches. Getting this wrong in the sticky direction
        // means notifications stop for the rest of the process's life.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "claude");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");
        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DetachSession,
            new DetachSessionRequest { SessionId = "session-1" }));

        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Still there?" });

        Assert.Equal("Still there?", (await Next()).Payload.Body);
    }

    [Fact]
    public async Task AFinishedSessionIsWorthKnowingAboutToo()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "build");

        await agent.InvokeAsync(
            HubMethods.Server.SessionClosed,
            new AgentSessionClosedNotification { SessionId = "session-1", ExitCode = 1 });

        PushJob job = await Next();
        Assert.Equal("build failed", job.Payload.Title);
        // Not perishable: a program that finished stays finished, however late the
        // phone gets around to showing it.
        Assert.False(job.Payload.Perishable);
    }

    [Fact]
    public async Task ActionRequiredSuppressesCompletionButKeepsPrompts()
    {
        HubConnection agent = await ConnectAgentAsync(
            AliceTenant,
            AliceObject,
            "machine-a",
            notificationLevel: NotificationLevel.ActionRequired);
        await OpenSessionAsync(agent, "session-1", "build");

        await agent.InvokeAsync(
            HubMethods.Server.SessionClosed,
            new AgentSessionClosedNotification { SessionId = "session-1", ExitCode = 0 });
        await AssertNoPush();

        await OpenSessionAsync(agent, "session-2", "claude");
        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-2", Hint = "Approve?" });

        Assert.Equal("Approve?", (await Next()).Payload.Body);
    }

    [Fact]
    public async Task OffSuppressesPushWithoutHidingLiveAttention()
    {
        HubConnection agent = await ConnectAgentAsync(
            AliceTenant,
            AliceObject,
            "machine-a",
            notificationLevel: NotificationLevel.Off);
        await OpenSessionAsync(agent, "session-1", "claude");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        var attention = Channel.CreateUnbounded<ClientSessionAwaitingInputNotification>();
        client.On<ClientSessionAwaitingInputNotification>(
            HubMethods.Client.SessionAwaitingInput,
            notification => attention.Writer.TryWrite(notification));

        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Approve?" });

        using var timeout = new CancellationTokenSource(Patience);
        ClientSessionAwaitingInputNotification live =
            await attention.Reader.ReadAsync(timeout.Token);
        Assert.Equal("machine-a", live.MachineId);
        Assert.Equal("Approve?", live.Hint);
        await AssertNoPush();
    }

    [Fact]
    public async Task ANotificationLevelChangeTakesEffectImmediately()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "claude");

        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetMachineNotificationLevel,
            new SetMachineNotificationLevelRequest { NotificationLevel = NotificationLevel.Off }));

        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Muted" });
        await AssertNoPush();

        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetMachineNotificationLevel,
            new SetMachineNotificationLevelRequest
            {
                NotificationLevel = NotificationLevel.AllAttentionEvents,
            }));
        await agent.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-1", Hint = "Audible" });

        Assert.Equal("Audible", (await Next()).Payload.Body);
    }

    [Fact]
    public async Task NotificationLevelsAreScopedPerMachine()
    {
        HubConnection quiet = await ConnectAgentAsync(
            AliceTenant,
            AliceObject,
            "quiet-machine",
            notificationLevel: NotificationLevel.Off);
        await OpenSessionAsync(quiet, "quiet-session", "claude");

        HubConnection loud = await ConnectAgentAsync(
            AliceTenant,
            AliceObject,
            "loud-machine",
            notificationLevel: NotificationLevel.AllAttentionEvents);
        await OpenSessionAsync(loud, "loud-session", "claude");

        await quiet.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification
            {
                SessionId = "quiet-session",
                Hint = "Muted",
            });
        await AssertNoPush();

        await loud.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification
            {
                SessionId = "loud-session",
                Hint = "Audible",
            });

        PushJob job = await Next();
        Assert.Equal("Audible", job.Payload.Body);
        Assert.Contains("loud-machine", job.Payload.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaitingSessionWakesItsOwnerAndNobodyElse()
    {
        // The user key on the job is what decides whose phone rings. A bug here is
        // the worst one in the product: somebody else's prompt on your lock screen.
        HubConnection alice = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(alice, "session-1", "claude");

        HubConnection bob = await ConnectAgentAsync(BobTenant, BobObject, "machine-b");
        await OpenSessionAsync(bob, "session-2", "claude");

        await bob.InvokeAsync(
            HubMethods.Server.SessionAwaitingInput,
            new SessionAwaitingInputNotification { SessionId = "session-2", Hint = "Bob's question" });

        PushJob job = await Next();
        Assert.Contains(BobObject, job.UserKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AliceObject, job.UserKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASubscriptionIsStoredAgainstTheCallerNotAnythingTheySent()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        Assert.Null(await RegisterPushAsync(client, "https://push.example/alice"));

        Assert.Equal(1, _subscriptions.UserCount);
        PushSubscription only = Assert.Single(_subscriptions.For(UserKeyFor(AliceTenant, AliceObject)));
        Assert.Equal("https://push.example/alice", only.Endpoint);
    }

    [Fact]
    public async Task TwoUsersSubscriptionsDoNotMix()
    {
        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);
        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        Assert.Null(await RegisterPushAsync(alice, "https://push.example/alice"));
        Assert.Null(await RegisterPushAsync(bob, "https://push.example/bob"));

        Assert.Equal(
            "https://push.example/alice",
            Assert.Single(_subscriptions.For(UserKeyFor(AliceTenant, AliceObject))).Endpoint);
        Assert.Equal(
            "https://push.example/bob",
            Assert.Single(_subscriptions.For(UserKeyFor(BobTenant, BobObject))).Endpoint);
    }

    [Fact]
    public async Task ASubscriptionMissingItsKeysIsRefused()
    {
        // Encryption is not optional in Web Push. A subscription without keys would
        // fail on every delivery attempt for the life of the process.
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ErrorNotification? error = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RegisterPush,
            new RegisterPushRequest
            {
                Endpoint = "https://push.example/alice",
                Keys = new PushKeys { P256dh = string.Empty, Auth = string.Empty },
            });

        Assert.NotNull(error);
        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
        Assert.Equal(0, _subscriptions.UserCount);
    }

    // Helpers.

    /// <summary>
    /// How the hub names a user, built the way the hub builds it.
    /// <para>
    /// Through <c>UserKey.From</c> rather than by string-formatting the pair here:
    /// the format is the hub's business, and a test that hard-coded it would keep
    /// passing while quietly checking the wrong thing if it ever changed.
    /// </para>
    /// </summary>
    private static string UserKeyFor(string tenantId, string objectId) =>
        UserKey.From(new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(UserKey.TenantIdClaim, tenantId),
                new Claim(UserKey.ObjectIdClaim, objectId),
            ],
            "Test")))!;

    private static Task<ErrorNotification?> RegisterPushAsync(HubConnection client, string endpoint) =>
        client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RegisterPush,
            new RegisterPushRequest
            {
                Endpoint = endpoint,
                Keys = new PushKeys { P256dh = "p256dh", Auth = "auth" },
            });

    private async Task<PushJob> Next()
    {
        using var timeout = new CancellationTokenSource(Patience);

        try
        {
            return await _pushes.Jobs.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No push was queued within {Patience}.");
        }
    }

    private async Task AssertNoPush()
    {
        // Long enough that one that was going to be queued would have been: the hub
        // enqueues synchronously inside the invocation it is reacting to.
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        Assert.False(_pushes.Jobs.Reader.TryRead(out _));
    }

    private async Task<HubConnection> ConnectAgentAsync(
        string tenantId,
        string objectId,
        string machineId,
        string displayName = "Machine",
        NotificationLevel notificationLevel = NotificationLevel.AllAttentionEvents)
    {
        HubConnection connection = await ConnectAsync(tenantId, objectId);

        Assert.Null(await connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RegisterMachine,
            new RegisterMachineRequest
            {
                MachineId = machineId,
                DisplayName = displayName,
                Os = "Windows",
                AgentVersion = "1.0.0",
                ProtocolVersion = ProtocolVersion.Current,
                NotificationLevel = notificationLevel,
            }));

        return connection;
    }

    private async Task<HubConnection> ConnectClientAsync(string tenantId, string objectId)
    {
        HubConnection connection = await ConnectAsync(tenantId, objectId);

        Assert.Null(await connection.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.ClientHandshake,
            new ClientHandshakeRequest { ProtocolVersion = ProtocolVersion.Current, ClientVersion = "1.0.0" }));

        return connection;
    }

    private async Task<HubConnection> ConnectAsync(string tenantId, string objectId)
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[HeaderIdentityHandler.TenantHeader] = tenantId;
                options.Headers[HeaderIdentityHandler.ObjectHeader] = objectId;
            })
            .AddMessagePackProtocol()
            .Build();

        _connections.Add(connection);
        await connection.StartAsync();

        return connection;
    }

    private static async Task OpenSessionAsync(HubConnection agent, string sessionId, string program) =>
        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionOpened,
            new AgentSessionOpenedNotification
            {
                Session = new SessionInfo
                {
                    SessionId = sessionId,
                    Program = program,
                    Args = [],
                    Cwd = @"C:\Projects\1RemoteCLI",
                    Cols = 120,
                    Rows = 30,
                    StartedAt = DateTimeOffset.UtcNow,
                },
            }));

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

    /// <summary>Stands in for the queue, so the decision is observable without a push service.</summary>
    private sealed class RecordingNotifier : IPushNotifier
    {
        public Channel<PushJob> Jobs { get; } = Channel.CreateUnbounded<PushJob>();

        public void Enqueue(string userKey, PushPayload payload) =>
            Jobs.Writer.TryWrite(new PushJob(userKey, payload));
    }
}
