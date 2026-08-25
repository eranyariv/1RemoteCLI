using System.Reflection;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OneRemoteCli.Hub.Auth;
using OneRemoteCli.Hub.Relay;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Hub.Tests;

/// <summary>
/// The adversarial suite: what a valid, fully signed-in user cannot do to somebody
/// else's machine.
/// <para>
/// This product hands whoever holds a session full control of a developer's computer,
/// and the hub is the only thing between an attacker and that. So the interesting
/// tests are not the ones where a stranger is turned away at the door — those are in
/// <see cref="TokenValidationTests"/> and <see cref="AccountAllowlistTests"/> — but
/// the ones where somebody who is genuinely admitted, with a real token and a real
/// account, then reaches for something that is not theirs while holding the correct
/// ids for it. A leaked machine id, a screenshot of a session list, a colleague who
/// used to have access: all of these produce exactly this situation.
/// </para>
/// <para>
/// Two of these tests are structural rather than behavioural, and they are the ones
/// that will still be working in a year. A test that Bob cannot attach to Alice's
/// session proves that today's methods are safe; it says nothing about the method
/// somebody adds next month. The reflection tests walk the hub's whole surface, so a
/// new method that takes an identity from its caller, or forgets to authorize, fails
/// the build rather than shipping.
/// </para>
/// </summary>
public sealed class AuthorizationTests : IAsyncLifetime
{
    private const string AliceTenant = "11111111-1111-1111-1111-111111111111";
    private const string AliceObject = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string BobTenant = "22222222-2222-2222-2222-222222222222";
    private const string BobObject = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    private readonly List<HubConnection> _connections = [];
    private readonly StubTokenValidator _validator = new();

    private WebApplicationFactory<Program> _factory = null!;

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

                services.AddSingleton<IAccessTokenValidator>(_validator);
            }));

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

    // Structural: the shape of the surface, rather than the behaviour of one method.

    [Fact]
    public void NoRequestTheHubAcceptsCanCarryAnIdentity()
    {
        // The strongest guarantee available, because it removes the mistake rather
        // than detecting it. If no request type has a field for a user, tenant or
        // account, then no hub method — present or future — can read the caller's
        // identity from the caller. It has to come from the connection principal,
        // which the caller cannot forge.
        string[] forbidden =
        [
            "userkey", "userid", "user", "tenantid", "tenant", "objectid",
            "oid", "tid", "upn", "account", "principal", "subject", "owner",
        ];

        foreach (MethodInfo method in Surface())
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                foreach (PropertyInfo property in parameter.ParameterType.GetProperties())
                {
                    Assert.DoesNotContain(
                        property.Name.ToLowerInvariant(),
                        forbidden);
                }
            }
        }
    }

    [Fact]
    public void EveryHubMethodResolvesTheIdentityFromTheConnection()
    {
        // The complement of the test above. That one proves the identity cannot be
        // sent; this one proves each method actually goes and gets it. A method that
        // did neither would route by an id alone, which is how you build a hub where
        // knowing a machine id is the same as owning the machine.
        //
        // There are exactly two legitimate ways to do it, and both are visible in the
        // source: call RequireUserKey(), which reads the connection principal, or hand
        // Context.ConnectionId to the registry, which looks the partition up from the
        // connection it was bound to at handshake. Anything else — routing by a
        // machine id or session id straight out of the request — is the bug.
        //
        // Read from the source rather than the IL, because what matters is that a
        // reviewer can see it too.
        string source = File.ReadAllText(SourcePath("Relay", "RelayHub.cs"));

        string[] exempt =
        [
            // Runs before any identity exists; it is what establishes one.
            nameof(RelayHub.OnConnectedAsync),

            // Runs after the connection is gone; the registry cleans up by connection
            // id, which is the only thing still true at that point.
            nameof(RelayHub.OnDisconnectedAsync),
        ];

        // A method may also delegate, which several of the client methods do. That is
        // fine only if the helper resolves too — so the helpers are checked by the same
        // rule rather than trusted, and adding one that does not resolve fails here.
        //
        // A helper may itself delegate to another helper on this list, which is what
        // the terminal-upload and chat-attachment families do: both are the same
        // request/result call with a different reply shape, so the resolution lives
        // once in InvokeKindedAsync rather than being copied per family.
        string[] helpers =
        [
            "ForwardAsync",
            "InvokeTerminalUploadAsync",
            "InvokeChatAttachmentAsync",
            "InvokeKindedAsync",
        ];

        foreach (string helper in helpers)
        {
            string body = Body(source, helper);

            Assert.True(
                body.Contains("Context.ConnectionId", StringComparison.Ordinal)
                || helpers.Any(other =>
                    other != helper && body.Contains(other + "(", StringComparison.Ordinal)),
                $"{helper} neither resolves the caller's identity nor delegates to a helper that does.");
        }

        foreach (MethodInfo method in Surface())
        {
            if (exempt.Contains(method.Name, StringComparer.Ordinal))
            {
                continue;
            }

            string body = Body(source, method.Name);

            Assert.True(
                body.Contains("RequireUserKey", StringComparison.Ordinal)
                || body.Contains("Context.ConnectionId", StringComparison.Ordinal)
                || helpers.Any(helper => body.Contains(helper + "(", StringComparison.Ordinal)),
                $"{method.Name} does not resolve the caller's identity from the connection.");
        }
    }

    [Fact]
    public void ThereAreHubMethodsToFind() =>
        // Guards the two tests above: reflection that matches nothing passes forever.
        Assert.True(Surface().Count >= 12, "the hub's method surface was not found");

    // Isolation: a real user, real ids, somebody else's machine.

    [Theory]
    [InlineData(HubMethods.Server.AttachSession)]
    [InlineData(HubMethods.Server.SendInput)]
    [InlineData(HubMethods.Server.ResizeTerminal)]
    [InlineData(HubMethods.Server.InterruptSession)]
    [InlineData(HubMethods.Server.DetachSession)]
    [InlineData(HubMethods.Server.SetSessionProject)]
    public async Task BobCannotReachAlicesSessionThroughAnyMethod(string method)
    {
        // One case per method rather than one test with five asserts, so a regression
        // names the method that broke.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1");

        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        ErrorNotification? error = await bob.InvokeAsync<ErrorNotification?>(method, RequestFor(method));

        Assert.NotNull(error);
        Assert.Contains(
            error.Code,
            new[] { ErrorCodes.MachineNotFound, ErrorCodes.SessionNotFound, ErrorCodes.NotAttached },
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task BobCannotEavesdropByAttachingBeforeAliceDoes()
    {
        // Attach is the one that would matter most: everything else changes state,
        // but this one would quietly stream somebody's screen. Bob asks first, and
        // must still be refused rather than racing into the stream.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        await OpenSessionAsync(agent, "session-1");

        ErrorNotification? error = await bob.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.AttachSession,
            new AttachSessionRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                Cols = 80,
                Rows = 24,
            });

        Assert.Equal(ErrorCodes.MachineNotFound, error!.Code);
    }

    [Fact]
    public async Task AnAgentCannotRegisterIntoSomebodyElsesPartition()
    {
        // The mirror image of the client case, and the one that would be catastrophic
        // if it worked: an agent that could register into Alice's partition would put
        // an attacker's machine in her list, where she might attach to it. There is no
        // field on the request to try it with — which is the point of the structural
        // test above — so the attack available is to claim her machine id.
        HubConnection alice = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a", "Alice's desktop");
        await OpenSessionAsync(alice, "session-1");

        HubConnection impostor = await ConnectAgentAsync(BobTenant, BobObject, "machine-a", "Not Alice's desktop");
        await OpenSessionAsync(impostor, "session-evil");

        HubConnection watching = await ConnectClientAsync(AliceTenant, AliceObject);
        MachineListNotification list = await watching.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        MachineInfo machine = Assert.Single(list.Machines);

        Assert.Equal("Alice's desktop", machine.DisplayName);
        Assert.Equal("session-1", Assert.Single(machine.Sessions).SessionId);
    }

    [Fact]
    public async Task AlicesOwnMachineIdIsNotAKeyToBobsMachineOfTheSameName()
    {
        // Same id, two partitions, and each side sees only its own. Without this the
        // registry would be a single global dictionary and the first agent to claim a
        // name would own it everywhere.
        HubConnection aliceAgent = await ConnectAgentAsync(AliceTenant, AliceObject, "desktop", "Alice's");
        HubConnection bobAgent = await ConnectAgentAsync(BobTenant, BobObject, "desktop", "Bob's");

        await OpenSessionAsync(aliceAgent, "session-alice");
        await OpenSessionAsync(bobAgent, "session-bob");

        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);
        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        Assert.Equal(
            "session-alice",
            Assert.Single(Assert.Single(
                (await alice.InvokeAsync<MachineListNotification>(HubMethods.Server.ListMachines)).Machines)
                .Sessions).SessionId);

        Assert.Equal(
            "session-bob",
            Assert.Single(Assert.Single(
                (await bob.InvokeAsync<MachineListNotification>(HubMethods.Server.ListMachines)).Machines)
                .Sessions).SessionId);
    }

    [Fact]
    public async Task AnUnauthenticatedConnectionCannotEvenStart()
    {
        // Not "every method is refused" — the connection never opens, so there is no
        // surface to walk. Asserting on the failure to connect is the honest test.
        HubConnection anonymous = Build(_ => { });

        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    // Session lifetime: an identity that changes underneath a live connection.

    [Fact]
    public async Task RefreshingIntoADifferentAccountEndsTheConnection()
    {
        // The one case where the hub kills a connection rather than answering it. A
        // live connection carries attachments; walking it from Alice to Bob would hand
        // Bob whatever Alice was watching. There is no state on such a connection that
        // is safe to keep, so it does not survive to be told why.
        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);

        _validator.Next = TokenReview.Accepted(
            $"{BobTenant}:{BobObject}",
            DateTimeOffset.UtcNow.AddHours(1));

        await Assert.ThrowsAnyAsync<Exception>(() => alice.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RefreshToken,
            new RefreshTokenRequest { Token = "bobs-token" }));

        await WaitUntilAsync(
            () => alice.State == HubConnectionState.Disconnected,
            "the connection to be aborted");
    }

    [Fact]
    public async Task RefreshingWithAnUnacceptableTokenIsRefusedButNotFatal()
    {
        // The opposite decision, for a reason worth keeping: the existing token may
        // still have minutes on it, and aborting would destroy the only channel over
        // which the holder could be told what went wrong. If they never refresh
        // properly, the sweeper ends the connection at expiry — which was always the
        // deadline anyway.
        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);

        _validator.Next = TokenReview.Rejected("Expired.");

        ErrorNotification? error = await alice.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RefreshToken,
            new RefreshTokenRequest { Token = "stale" });

        Assert.Equal(ErrorCodes.TokenExpired, error!.Code);
        Assert.Equal(HubConnectionState.Connected, alice.State);

        // And still usable, which is the whole point of not aborting.
        Assert.NotNull(await alice.InvokeAsync<MachineListNotification>(HubMethods.Server.ListMachines));
    }

    [Fact]
    public async Task RefreshingWithTheSameAccountSucceedsQuietly()
    {
        // The happy path, here rather than in RelayHubTests because it is the control
        // for the two tests above: without it, "refresh always fails" would pass both.
        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);

        _validator.Next = TokenReview.Accepted(
            $"{AliceTenant}:{AliceObject}",
            DateTimeOffset.UtcNow.AddHours(1));

        Assert.Null(await alice.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RefreshToken,
            new RefreshTokenRequest { Token = "fresh" }));

        Assert.Equal(HubConnectionState.Connected, alice.State);
    }

    [Fact]
    public async Task ARefreshWithNoTokenIsRejectedWithoutConsultingTheValidator()
    {
        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);

        _validator.Next = TokenReview.Accepted($"{BobTenant}:{BobObject}", DateTimeOffset.UtcNow.AddHours(1));

        ErrorNotification? error = await alice.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RefreshToken,
            new RefreshTokenRequest { Token = "  " });

        Assert.Equal(ErrorCodes.InvalidRequest, error!.Code);
        Assert.Equal(0, _validator.Consulted);
    }

    // Helpers.

    private static IReadOnlyList<MethodInfo> Surface() =>
    [
        .. typeof(RelayHub)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(RelayHub)),
    ];

    /// <summary>Everything between a method's signature and the next one's.</summary>
    private static string Body(string source, string name)
    {
        // The declaration, not the first call site: several of these methods call each
        // other, and matching a call would read the caller's body instead.
        int start = -1;

        for (int at = source.IndexOf($" {name}", StringComparison.Ordinal);
             at >= 0;
             at = source.IndexOf($" {name}", at + 1, StringComparison.Ordinal))
        {
            char next = source[at + name.Length + 1];

            // '<' as well as '(', because ForwardAsync is generic and its declaration
            // reads `ForwardAsync<TNotification>(`.
            if (next != '(' && next != '<')
            {
                continue;
            }

            int lineStart = source.LastIndexOf('\n', at) + 1;
            string prefix = source[lineStart..at];

            if (prefix.Contains("private", StringComparison.Ordinal)
                || prefix.Contains("public", StringComparison.Ordinal)
                || prefix.Contains("internal", StringComparison.Ordinal))
            {
                start = at;
                break;
            }
        }

        Assert.True(start >= 0, $"{name} was not declared in RelayHub.cs");

        int depth = 0;
        bool opened = false;

        for (int i = start; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
                opened = true;
            }
            else if (source[i] == '}')
            {
                depth--;

                if (opened && depth == 0)
                {
                    return source[start..i];
                }
            }
            else if (source[i] == ';' && !opened)
            {
                // An expression-bodied member, which ends at its semicolon.
                return source[start..i];
            }
        }

        return source[start..];
    }

    private static string SourcePath(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "1RemoteCLI.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return Path.Combine([directory.FullName, "src", "Hub", .. parts]);
    }

    private static object RequestFor(string method) => method switch
    {
        HubMethods.Server.AttachSession => new AttachSessionRequest
        {
            MachineId = "machine-a",
            SessionId = "session-1",
            Cols = 80,
            Rows = 24,
        },
        HubMethods.Server.SendInput => new SendInputRequest
        {
            SessionId = "session-1",
            Data = "rm -rf /\r"u8.ToArray(),
        },
        HubMethods.Server.ResizeTerminal => new ResizeTerminalRequest
        {
            SessionId = "session-1",
            Cols = 40,
            Rows = 10,
        },
        HubMethods.Server.InterruptSession => new InterruptSessionRequest { SessionId = "session-1" },
        HubMethods.Server.DetachSession => new DetachSessionRequest { SessionId = "session-1" },
        HubMethods.Server.SetSessionProject => new SetSessionProjectRequest
        {
            MachineId = "machine-a",
            SessionId = "session-1",
            ProjectId = null,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "No request shape for this method."),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    private async Task<HubConnection> ConnectAgentAsync(
        string tenantId,
        string objectId,
        string machineId,
        string displayName = "Machine")
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
                options.Transports = HttpTransportType.LongPolling;

                configureHeaders(options.Headers);
            })
            .AddMessagePackProtocol()
            .Build();

        _connections.Add(connection);

        return connection;
    }

    private static Task OpenSessionAsync(HubConnection agent, string sessionId) =>
        agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionOpened,
            new AgentSessionOpenedNotification
            {
                Session = new SessionInfo
                {
                    SessionId = sessionId,
                    Program = "pwsh",
                    Args = [],
                    Cwd = @"C:\Projects",
                    Cols = 120,
                    Rows = 30,
                    StartedAt = DateTimeOffset.UtcNow,
                },
            });

    /// <summary>
    /// A validator the test drives directly, because the refresh path cannot be
    /// exercised against Entra and a path that is never exercised is a path that
    /// eventually stops working.
    /// </summary>
    private sealed class StubTokenValidator : IAccessTokenValidator
    {
        private int _consulted;

        public TokenReview Next { get; set; } = TokenReview.Rejected("Nothing configured.");

        public int Consulted => _consulted;

        public Task<TokenReview> ReviewAsync(string token, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _consulted);

            return Task.FromResult(Next);
        }
    }
}
