using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OneRemoteCli.Daemon.Ipc;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.Protocol.Hub;
using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Daemon.Tests;

[SupportedOSPlatform("windows")]
public class AgentPipeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void PutsTheUsersSidInThePipeName()
    {
        string name = AgentPipe.NameForCurrentUser();

        Assert.StartsWith("1remotecli-agent-", name);
        Assert.EndsWith(WindowsIdentity.GetCurrent().User!.Value, name);
    }

    /// <summary>
    /// Two people signed in to the same machine must not share a pipe, or one could
    /// read the other's terminal.
    /// </summary>
    [Fact]
    public void GivesDifferentUsersDifferentPipeNames()
    {
        var someoneElse = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        Assert.NotEqual(AgentPipe.NameForCurrentUser(), AgentPipe.NameFor(someoneElse));
    }

    /// <summary>
    /// The ACL is the control that stops another local process from reading a
    /// session's output or typing into it, so it is asserted directly rather than
    /// inferred from behaviour.
    /// </summary>
    [Fact]
    public void GrantsAccessToNobodyButTheOwningUser()
    {
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        var rules = AgentPipe.SecurityForCurrentUser()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        PipeAccessRule only = Assert.Single(rules);

        Assert.Equal(me, only.IdentityReference);
        Assert.Equal(AccessControlType.Allow, only.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, only.PipeAccessRights & PipeAccessRights.FullControl);
    }

    /// <summary>The same assertion, but against the descriptor a live pipe actually has.</summary>
    [Fact]
    public async Task AppliesThatAclToTheRealPipe()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        await using AgentPipeConnection connection = await accepting.WaitAsync(Timeout);

        var live = ((PipeStream)GetStream(connection)).GetAccessControl();
        var rules = live.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rules[0].IdentityReference);

        foreach (WellKnownSidType unwanted in new[]
        {
            WellKnownSidType.WorldSid,
            WellKnownSidType.AuthenticatedUserSid,
            WellKnownSidType.BuiltinAdministratorsSid,
        })
        {
            var sid = new SecurityIdentifier(unwanted, null);
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(sid));
        }
    }

    [Fact]
    public async Task CarriesASessionHandshakeInBothDirections()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        await using AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        Task<string> opening = client.OpenSessionAsync(
            new SessionStartInfo("pwsh", ["-NoLogo"], @"C:\work", 120, 30, "nightly build"),
            CancellationToken.None);

        PipeEnvelope opened = await Receive(agentSide);
        Assert.Equal(PipeMessageKind.SessionOpened, opened.Kind);

        var payload = PipeFraming.DecodePayload<SessionOpenedMessage>(opened);
        Assert.Equal("pwsh", payload.Program);
        Assert.Equal(["-NoLogo"], payload.Args);
        Assert.Equal(@"C:\work", payload.Cwd);
        Assert.Equal(120, payload.Cols);
        Assert.Equal(30, payload.Rows);
        Assert.Equal("nightly build", payload.DisplayName);

        await agentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-42" });

        Assert.Equal("s-42", await opening.WaitAsync(Timeout));
    }

    [Fact]
    public async Task CarriesAChatCreationRequestInBothDirections()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        await using AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        Task<ChatCreatedMessage> creating = client.CreateChatAsync(
            @"C:\repo",
            "My repo",
            CliType.CopilotCli);

        PipeEnvelope requested = await Receive(agentSide);
        Assert.Equal(PipeMessageKind.ChatCreate, requested.Kind);
        ChatCreateMessage request = PipeFraming.DecodePayload<ChatCreateMessage>(requested);
        Assert.Equal(@"C:\repo", request.Cwd);
        Assert.Equal("My repo", request.DisplayName);
        Assert.Equal(CliType.CopilotCli, request.CliType);

        await agentSide.SendAsync(
            PipeMessageKind.ChatCreated,
            new ChatCreatedMessage { MachineId = "machine", SessionId = "chat" });

        ChatCreatedMessage created = await creating.WaitAsync(Timeout);
        Assert.True(created.Ok);
        Assert.Equal("machine", created.MachineId);
        Assert.Equal("chat", created.SessionId);
    }

    [Fact]
    public async Task ForwardsOutputAndTheFinalExitCodeToTheAgent()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        await using AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        await client.SendOutputAsync(Encoding.UTF8.GetBytes("hello"), CancellationToken.None);
        await client.CloseSessionAsync(7, CancellationToken.None);

        PipeEnvelope output = await Receive(agentSide);
        Assert.Equal(PipeMessageKind.Output, output.Kind);
        Assert.Equal("hello", Encoding.UTF8.GetString(PipeFraming.DecodePayload<OutputMessage>(output).Bytes));

        PipeEnvelope closed = await Receive(agentSide);
        Assert.Equal(PipeMessageKind.SessionClosed, closed.Kind);
        Assert.Equal(7, PipeFraming.DecodePayload<SessionClosedMessage>(closed).ExitCode);
    }

    [Fact]
    public async Task SurfacesAgentCommandsToTheWrapper()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        await using AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        await agentSide.SendAsync(PipeMessageKind.Input, new InputMessage { Bytes = Encoding.UTF8.GetBytes("dir\r") });
        await agentSide.SendAsync(PipeMessageKind.Resize, new ResizeMessage { Cols = 100, Rows = 40 });
        await agentSide.SendAsync(PipeMessageKind.Interrupt, new InterruptMessage());

        var input = Assert.IsType<AgentCommand.Input>(await Next(client));
        Assert.Equal("dir\r", Encoding.UTF8.GetString(input.Bytes));

        var resize = Assert.IsType<AgentCommand.Resize>(await Next(client));
        Assert.Equal((100, 40), (resize.Cols, resize.Rows));

        Assert.IsType<AgentCommand.Interrupt>(await Next(client));
    }

    /// <summary>One agent, many sessions: every wrapper gets its own connection.</summary>
    [Fact]
    public async Task ServesSeveralWrappersAtOnce()
    {
        await using var server = new AgentPipeServer(UniquePipeName());

        var agentSides = new List<AgentPipeConnection>();
        var clients = new List<AgentPipeClient>();

        try
        {
            for (int i = 0; i < 3; i++)
            {
                Task<AgentPipeConnection> accepting = server.AcceptAsync();
                clients.Add(await AgentPipeClient.ConnectAsync(server.PipeName));
                agentSides.Add(await accepting.WaitAsync(Timeout));
            }

            for (int i = 0; i < clients.Count; i++)
            {
                await clients[i].SendOutputAsync(Encoding.UTF8.GetBytes($"session-{i}"), CancellationToken.None);
            }

            for (int i = 0; i < agentSides.Count; i++)
            {
                PipeEnvelope frame = await Receive(agentSides[i]);
                Assert.Equal($"session-{i}", Encoding.UTF8.GetString(PipeFraming.DecodePayload<OutputMessage>(frame).Bytes));
            }
        }
        finally
        {
            foreach (AgentPipeClient client in clients)
            {
                await client.DisposeAsync();
            }

            foreach (AgentPipeConnection connection in agentSides)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// No agent means no sharing, and the wrapper says so rather than pretending.
    /// </summary>
    [Fact]
    public async Task FailsLoudlyWhenNoAgentIsListening()
    {
        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(
            () => AgentPipeClient.ConnectAsync(UniquePipeName(), TimeSpan.FromMilliseconds(300)));

        Assert.Contains("1remote agent", ex.Message);
    }

    /// <summary>
    /// A wrapper that starts a moment before the agent should wait, not fail: at
    /// logon the two race, and losing that race must not cost the user their session.
    /// </summary>
    [Fact]
    public async Task WaitsForAnAgentThatIsStillStartingUp()
    {
        string name = UniquePipeName();
        await using var server = new AgentPipeServer(name);

        Task<AgentPipeClient> connecting = AgentPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(10));

        await Task.Delay(400);
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await connecting.WaitAsync(Timeout);
        await using AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        Assert.True(agentSide.IsConnected);
    }

    /// <summary>
    /// When the agent restarts, the wrapper must notice and stop sharing cleanly
    /// rather than blocking forever on a pipe nobody is reading.
    /// </summary>
    [Fact]
    public async Task EndsTheCommandStreamWhenTheAgentGoesAway()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        await agentSide.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (AgentCommand _ in client.Commands.ReadAllAsync().WithCancellation(CancelAfter(Timeout)))
            {
            }
        });
    }

    /// <summary>
    /// A frame kind from a newer peer must be skipped, not treated as corruption:
    /// the wrapper and the agent are updated independently on a user's machine.
    /// </summary>
    [Fact]
    public async Task IgnoresFrameKindsItDoesNotUnderstand()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(server.PipeName);
        await using AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        await agentSide.SendAsync((PipeMessageKind)200, new InterruptMessage());
        await agentSide.SendAsync(PipeMessageKind.Input, new InputMessage { Bytes = [0x61] });

        var input = Assert.IsType<AgentCommand.Input>(await Next(client));
        Assert.Equal([0x61], input.Bytes);
    }

    private static async Task<PipeEnvelope> Receive(AgentPipeConnection connection)
    {
        PipeEnvelope? envelope = await connection.ReceiveAsync(CancelAfter(Timeout));
        Assert.NotNull(envelope);
        return envelope;
    }

    private static async Task<AgentCommand> Next(AgentPipeClient client) =>
        await client.Commands.ReadAsync(CancelAfter(Timeout));

    private static CancellationToken CancelAfter(TimeSpan delay) => new CancellationTokenSource(delay).Token;

    /// <summary>
    /// Tests must not collide with each other or with a real agent on this machine,
    /// so each one gets its own name.
    /// </summary>
    private static string UniquePipeName() => $"1remotecli-test-{Guid.NewGuid():N}";

    private static Stream GetStream(AgentPipeConnection connection) =>
        (Stream)typeof(AgentPipeConnection)
            .GetField("_stream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(connection)!;
}
