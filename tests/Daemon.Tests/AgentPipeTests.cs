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
    /// Before any session exists, a lost pipe is exactly as fatal as it always was:
    /// there is nothing yet worth reconnecting for. This covers both the shared
    /// connection code before <c>OpenSessionAsync</c> completes and the dedicated
    /// <c>CreateChatAsync</c> channel, which never opens a session at all.
    /// </summary>
    [Fact]
    public async Task EndsTheCommandStreamWhenNoSessionWasEverEstablished()
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
    /// The one-shot channel a shortcut launcher uses to create a chat never opens a
    /// terminal session, so it must keep the old, bounded, loud behaviour too — a
    /// launcher waiting on <c>CreateChatAsync</c> must hear about a lost agent rather
    /// than have the call silently retry forever.
    /// </summary>
    [Fact]
    public async Task ChatCreationFailsLoudlyRatherThanReconnectingWhenTheAgentGoesAway()
    {
        await using var server = new AgentPipeServer(UniquePipeName());
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(
            server.PipeName,
            reconnectDelay: TimeSpan.FromMilliseconds(10));
        AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        Task<ChatCreatedMessage> creating = client.CreateChatAsync(@"C:\repo", "My repo", CliType.CopilotCli);

        await Receive(agentSide); // the ChatCreate request itself
        await agentSide.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => creating.WaitAsync(Timeout));
    }

    /// <summary>
    /// The headline behaviour: once a session exists, the agent going away — an
    /// update restarting it — is not the end of sharing. The wrapper dials the same
    /// pipe again, asks for its old id back, and gets it, because a fresh agent's
    /// registry is empty and has no reason to refuse.
    /// </summary>
    [Fact]
    public async Task ReconnectsAfterTheAgentGoesAwayAndKeepsTheSameSessionId()
    {
        string name = UniquePipeName();
        await using var server = new AgentPipeServer(name);
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(
            name,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        AgentPipeConnection firstAgentSide = await accepting.WaitAsync(Timeout);

        Task<string> opening = client.OpenSessionAsync(
            new SessionStartInfo("pwsh", ["-NoLogo"], @"C:\work", 80, 24, "nightly build"),
            CancellationToken.None);

        PipeEnvelope opened = await Receive(firstAgentSide);
        var firstOpen = PipeFraming.DecodePayload<SessionOpenedMessage>(opened);
        Assert.Null(firstOpen.PriorSessionId);
        Assert.True(firstOpen.SupportsReconnect);

        await firstAgentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-1" });
        Assert.Equal("s-1", await opening.WaitAsync(Timeout));

        // The agent restarts: its end of the pipe goes away, and a fresh instance
        // starts listening on the same name.
        Task<AgentPipeConnection> acceptingAgain = server.AcceptAsync();
        await firstAgentSide.DisposeAsync();

        // Nothing about the public surface breaks while that is happening.
        await using AgentPipeConnection secondAgentSide = await acceptingAgain.WaitAsync(Timeout);

        PipeEnvelope reopened = await Receive(secondAgentSide);
        Assert.Equal(PipeMessageKind.SessionOpened, reopened.Kind);
        var reopen = PipeFraming.DecodePayload<SessionOpenedMessage>(reopened);
        Assert.Equal("pwsh", reopen.Program);
        Assert.Equal("s-1", reopen.PriorSessionId);
        Assert.True(reopen.SupportsReconnect);

        await secondAgentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-1" });

        // A snapshot always follows a successful reopen, so the fresh agent is not
        // left rendering a blank screen for a session that already has output.
        PipeEnvelope snapshot = await Receive(secondAgentSide);
        Assert.Equal(PipeMessageKind.Output, snapshot.Kind);

        // And commands keep flowing on the new connection.
        await secondAgentSide.SendAsync(PipeMessageKind.Input, new InputMessage { Bytes = Encoding.UTF8.GetBytes("dir\r") });
        var input = Assert.IsType<AgentCommand.Input>(await Next(client));
        Assert.Equal("dir\r", Encoding.UTF8.GetString(input.Bytes));
    }

    /// <summary>
    /// Output produced while the agent is away must not vanish, and must not be
    /// replayed twice once it comes back: the reconnect snapshot already reflects it,
    /// so only output produced <em>after</em> the snapshot may arrive as its own
    /// frame, and it must arrive after the snapshot rather than before or mixed in.
    /// </summary>
    [Fact]
    public async Task SendsAScreenSnapshotBeforeAnyOutputQueuedDuringTheGap()
    {
        string name = UniquePipeName();
        await using var server = new AgentPipeServer(name);
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(
            name,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        AgentPipeConnection firstAgentSide = await accepting.WaitAsync(Timeout);

        Task<string> opening = client.OpenSessionAsync(StartInfo("pwsh"), CancellationToken.None);
        await Receive(firstAgentSide);
        await firstAgentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-1" });
        await opening.WaitAsync(Timeout);

        // Output before the gap: this is what the snapshot must carry.
        await client.SendOutputAsync(Encoding.UTF8.GetBytes("before the gap"), CancellationToken.None);

        Task<AgentPipeConnection> acceptingAgain = server.AcceptAsync();
        await firstAgentSide.DisposeAsync();
        await using AgentPipeConnection secondAgentSide = await acceptingAgain.WaitAsync(Timeout);

        await Receive(secondAgentSide); // the reopened SessionOpened
        await secondAgentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-1" });

        PipeEnvelope snapshot = await Receive(secondAgentSide);
        Assert.Equal(PipeMessageKind.Output, snapshot.Kind);
        string snapshotText = Encoding.UTF8.GetString(PipeFraming.DecodePayload<OutputMessage>(snapshot).Bytes);
        Assert.Contains("before the gap", snapshotText, StringComparison.Ordinal);

        await client.SendOutputAsync(Encoding.UTF8.GetBytes("after the gap"), CancellationToken.None);

        PipeEnvelope after = await Receive(secondAgentSide);
        Assert.Equal(PipeMessageKind.Output, after.Kind);
        Assert.Equal(
            "after the gap",
            Encoding.UTF8.GetString(PipeFraming.DecodePayload<OutputMessage>(after).Bytes));
    }

    [Fact]
    public async Task BusyOutputDuringReconnectIsFoldedIntoTheSnapshotWithoutOverflowing()
    {
        string name = UniquePipeName();
        await using var server = new AgentPipeServer(name);
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(
            name,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        AgentPipeConnection firstAgentSide = await accepting.WaitAsync(Timeout);

        Task<string> opening = client.OpenSessionAsync(StartInfo("pwsh"), CancellationToken.None);
        await Receive(firstAgentSide);
        await firstAgentSide.SendAsync(
            PipeMessageKind.SessionAccepted,
            new SessionAcceptedMessage { SessionId = "s-1" });
        await opening.WaitAsync(Timeout);

        await firstAgentSide.DisposeAsync();
        await Task.Delay(100);

        for (int i = 0; i < 2_000; i++)
        {
            await client.SendOutputAsync(Encoding.UTF8.GetBytes($"line {i}\r\n"), CancellationToken.None);
        }

        Task<AgentPipeConnection> acceptingAgain = server.AcceptAsync();
        await using AgentPipeConnection secondAgentSide = await acceptingAgain.WaitAsync(Timeout);
        await Receive(secondAgentSide);
        await secondAgentSide.SendAsync(
            PipeMessageKind.SessionAccepted,
            new SessionAcceptedMessage { SessionId = "s-1" });

        PipeEnvelope snapshot = await Receive(secondAgentSide);
        string text = Encoding.UTF8.GetString(PipeFraming.DecodePayload<OutputMessage>(snapshot).Bytes);
        Assert.Contains("line 1999", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservesAnIncompleteEscapeSequenceAfterTheReconnectSnapshot()
    {
        string name = UniquePipeName();
        await using var server = new AgentPipeServer(name);
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(
            name,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        AgentPipeConnection firstAgentSide = await accepting.WaitAsync(Timeout);

        Task<string> opening = client.OpenSessionAsync(StartInfo("pwsh"), CancellationToken.None);
        await Receive(firstAgentSide);
        await firstAgentSide.SendAsync(
            PipeMessageKind.SessionAccepted,
            new SessionAcceptedMessage { SessionId = "s-1" });
        await opening.WaitAsync(Timeout);

        await client.SendOutputAsync("hello\u001b["u8.ToArray(), CancellationToken.None);
        Task<AgentPipeConnection> acceptingAgain = server.AcceptAsync();
        await firstAgentSide.DisposeAsync();

        await using AgentPipeConnection secondAgentSide = await acceptingAgain.WaitAsync(Timeout);
        await Receive(secondAgentSide);
        await secondAgentSide.SendAsync(
            PipeMessageKind.SessionAccepted,
            new SessionAcceptedMessage { SessionId = "s-1" });

        PipeEnvelope snapshot = await Receive(secondAgentSide);
        Assert.Equal(PipeMessageKind.Output, snapshot.Kind);
        Assert.Contains(
            "hello",
            Encoding.UTF8.GetString(PipeFraming.DecodePayload<OutputMessage>(snapshot).Bytes),
            StringComparison.Ordinal);

        PipeEnvelope unsafeTail = await Receive(secondAgentSide);
        Assert.Equal("\u001b[", Encoding.UTF8.GetString(
            PipeFraming.DecodePayload<OutputMessage>(unsafeTail).Bytes));
    }

    /// <summary>
    /// The reconnect work must not weaken the existing backpressure rule: a link that
    /// cannot keep up is still a real problem the user must be told about, loudly and
    /// permanently, not something retried past silently.
    /// </summary>
    [Fact]
    public async Task StillTreatsAFullOutboundQueueAsALoudPermanentFailure()
    {
        string name = UniquePipeName();

        // Deliberately tiny buffers: the point is a link that is up but cannot carry
        // any more, and a 64KB default buffer would need megabytes of filler before
        // that became true. This makes the very first write block instead.
        using var serverStream = new NamedPipeServerStream(
            name,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 1,
            outBufferSize: 1);
        Task accepted = serverStream.WaitForConnectionAsync();

        await using AgentPipeClient client = await AgentPipeClient.ConnectAsync(name);
        await accepted.WaitAsync(Timeout);
        await using var agentSide = new AgentPipeConnection(serverStream);

        Task<string> opening = client.OpenSessionAsync(StartInfo("pwsh"), CancellationToken.None);
        await Receive(agentSide);
        await agentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-1" });
        await opening.WaitAsync(Timeout);

        // The link stays up, but nothing ever reads it again from here on: the same
        // shape as a redraw arriving faster than a slow phone connection can carry
        // it, not a dropped pipe, so this is the ordinary overflow rule rather than
        // reconnect.
        byte[] chunk = new byte[64];

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            for (int i = 0; i < 2000; i++)
            {
                await client.SendOutputAsync(chunk, CancellationToken.None);
            }
        });
    }

    /// <summary>
    /// Disposal is the one thing that must always win over reconnecting, however long
    /// the agent has been away: a wrapper whose child already exited must not keep a
    /// background loop alive trying to find an agent that may never come back.
    /// </summary>
    [Fact]
    public async Task DisposingWhileReconnectingStopsRetryingPromptly()
    {
        string name = UniquePipeName();
        await using var server = new AgentPipeServer(name);
        Task<AgentPipeConnection> accepting = server.AcceptAsync();

        AgentPipeClient client = await AgentPipeClient.ConnectAsync(
            name,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        AgentPipeConnection agentSide = await accepting.WaitAsync(Timeout);

        Task<string> opening = client.OpenSessionAsync(StartInfo("pwsh"), CancellationToken.None);
        await Receive(agentSide);
        await agentSide.SendAsync(PipeMessageKind.SessionAccepted, new SessionAcceptedMessage { SessionId = "s-1" });
        await opening.WaitAsync(Timeout);

        // Nothing is listening on this name again, so every reconnect attempt fails
        // and retries — exactly the state disposal has to cut through.
        await agentSide.DisposeAsync();
        await Task.Delay(100);

        await client.DisposeAsync().AsTask().WaitAsync(Timeout);
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

    private static SessionStartInfo StartInfo(string program) =>
        new(program, [], @"C:\work", 80, 24, null);

    private static Stream GetStream(AgentPipeConnection connection) =>
        (Stream)typeof(AgentPipeConnection)
            .GetField("_stream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(connection)!;
}
