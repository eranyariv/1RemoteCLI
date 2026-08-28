using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Ipc;
using OneRemoteCli.Daemon.Wrapper;
using OneRemoteCli.Protocol.Hub;
using OneRemoteCli.Protocol.Pipe;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// Exercises the agent against real wrapper clients over a real pipe. The registry
/// is only trustworthy if it stays right when wrappers come and go for reasons the
/// agent never sees, so these tests use the actual transport rather than fakes.
/// </summary>
[SupportedOSPlatform("windows")]
public class AgentHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RegistersASessionAndGivesItAnId()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        string sessionId = await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);

        Assert.True(Guid.TryParse(sessionId, out _));

        TerminalSession session = await fixture.WaitForSessionAsync(sessionId);
        Assert.Equal("pwsh", session.Program);
        Assert.Equal(120, session.Cols);
        Assert.Equal(30, session.Rows);
    }

    [Fact]
    public async Task ConfirmedTypeOverridesCommandLineDetection()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        string sessionId = await wrapper.OpenSessionAsync(
            StartInfo("copilot") with { CliType = CliType.Generic },
            default).WaitAsync(Timeout);

        TerminalSession session = await fixture.WaitForSessionAsync(sessionId);
        Assert.Equal(CliType.Generic, session.CliType);
    }

    [Fact]
    public async Task RoutesAChatCreationRequestToTheAgentCallback()
    {
        ChatCreateMessage? received = null;
        await using var fixture = await AgentFixture.StartAsync(
            createChat: (request, _) =>
            {
                received = request;
                return Task.FromResult(new ChatCreatedMessage
                {
                    MachineId = "machine",
                    SessionId = "chat",
                });
            });

        AgentPipeClient launcher = await fixture.ConnectAsync();
        ChatCreatedMessage created = await launcher.CreateChatAsync(
            @"C:\repo",
            "My repo",
            CliType.CopilotCli).WaitAsync(Timeout);

        Assert.True(created.Ok);
        Assert.NotNull(received);
        Assert.Equal(@"C:\repo", received.Cwd);
        Assert.Equal("My repo", received.DisplayName);
        Assert.Equal(CliType.CopilotCli, received.CliType);
    }

    /// <summary>The headline requirement: two terminals on one desk are two sessions.</summary>
    [Fact]
    public async Task TracksTwoConcurrentWrappersSeparately()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient first = await fixture.ConnectAsync();
        AgentPipeClient second = await fixture.ConnectAsync();

        string a = await first.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);
        string b = await second.OpenSessionAsync(StartInfo("cmd.exe"), default).WaitAsync(Timeout);

        Assert.NotEqual(a, b);
        await fixture.WaitForCountAsync(2);

        IReadOnlyList<TerminalSession> snapshot = fixture.Host.Sessions.Snapshot();
        Assert.Equal([a, b], snapshot.Select(s => s.SessionId));
    }

    /// <summary>
    /// A session cannot outlive its wrapper, so a closed console window must leave
    /// nothing behind — including when the wrapper never said goodbye.
    /// </summary>
    [Fact]
    public async Task DropsASessionWhenItsWrapperDisconnects()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient first = await fixture.ConnectAsync();
        AgentPipeClient second = await fixture.ConnectAsync();

        string a = await first.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);
        await second.OpenSessionAsync(StartInfo("cmd.exe"), default).WaitAsync(Timeout);
        await fixture.WaitForCountAsync(2);

        await second.DisposeAsync();

        await fixture.WaitForCountAsync(1);
        Assert.Equal(a, Assert.Single(fixture.Host.Sessions.Snapshot()).SessionId);
    }

    [Fact]
    public async Task DropsASessionThatClosesCleanly()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);
        await fixture.WaitForCountAsync(1);

        await wrapper.CloseSessionAsync(42, default);

        await fixture.WaitForCountAsync(0);
        Assert.Equal(42, fixture.Sink.LastExitCode);
    }

    /// <summary>
    /// The registry-reuse half of reconnect support (issue #174), exercised through
    /// the real wire format rather than calling <see cref="SessionRegistry"/>
    /// directly: a wrapper reconnecting with its prior id gets it back, and is marked
    /// reconnect-capable, exactly as a real <c>AgentPipeClient</c> reconnect does.
    /// </summary>
    [Fact]
    public async Task ReusesAWrappersRequestedIdWhenItReconnectsThroughTheRealWrapperConnection()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient first = await fixture.ConnectAsync();
        string original = await first.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);
        await fixture.WaitForCountAsync(1);

        // Ends the connection without telling the agent why — indistinguishable, from
        // the agent's side, from the agent itself having just restarted.
        await first.DisposeAsync();
        await fixture.WaitForCountAsync(0);

        // A second connection asks for that id back, the same way a reconnecting
        // AgentPipeClient does, but crafted directly so the test controls exactly
        // what the wire carries.
        using var stream = new NamedPipeClientStream(
            ".",
            fixture.Host.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await stream.ConnectAsync((int)Timeout.TotalMilliseconds);
        await using var connection = new AgentPipeConnection(stream);

        await connection.SendAsync(
            PipeMessageKind.SessionOpened,
            new SessionOpenedMessage
            {
                Program = "pwsh",
                Cwd = @"C:\work",
                Cols = 120,
                Rows = 30,
                PriorSessionId = original,
                SupportsReconnect = true,
            });

        PipeEnvelope accepted = await Receive(connection);
        Assert.Equal(PipeMessageKind.SessionAccepted, accepted.Kind);
        string reused = PipeFraming.DecodePayload<SessionAcceptedMessage>(accepted).SessionId;

        Assert.Equal(original, reused);

        TerminalSession session = await fixture.WaitForSessionAsync(reused);
        Assert.True(session.SupportsReconnect);
    }

    [Fact]
    public async Task DoesNotReportAReconnectableSessionClosedWhenTheAgentStops()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);
        await fixture.WaitForCountAsync(1);

        await fixture.StopHostAsync();

        Assert.Null(fixture.Sink.LastExitCode);
    }

    [Fact]
    public async Task ForwardsOutputToTheSink()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);

        await wrapper.SendOutputAsync(Encoding.UTF8.GetBytes("hello"), default);

        await WaitUntilAsync(() => fixture.Sink.Output.Contains("hello"));
    }

    /// <summary>
    /// A thousand small writes do not become a thousand messages.
    /// <para>
    /// This is the difference between a phone that works on a train and one that does
    /// not. A full-screen program redrawing at its own pace produces writes far faster
    /// than any link can carry individual messages, and the redraws in between are
    /// overwritten before anyone could have seen them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AFloodOfSmallWritesIsCoalescedIntoFarFewerFrames()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);

        // Kept under the wrapper's outbound queue depth on purpose. Exceeding it is a
        // different failure — the wrapper deliberately drops the agent rather than
        // stall the desk terminal — and a test that tripped it would be measuring
        // that rule instead of this one.
        const int writes = 400;
        byte[] line = Encoding.UTF8.GetBytes("redraw\r\n");

        for (int i = 0; i < writes; i++)
        {
            await wrapper.SendOutputAsync(line, default);
        }

        int expected = writes * line.Length;

        await WaitUntilAsync(() => fixture.Sink.FrameSizes.Sum() == expected);

        IReadOnlyList<int> frames = fixture.Sink.FrameSizes;

        // Nothing is lost or duplicated, whatever the framing did.
        Assert.Equal(expected, frames.Sum());

        // The real assertion. An uncoalesced path produces one frame per write; the
        // bound is generous because the exact count depends on how many ticks the
        // writes happened to span, and pinning that would make this a clock test.
        Assert.True(frames.Count < writes / 10, $"{frames.Count} frames for {writes} writes.");

        Assert.All(frames, size => Assert.InRange(size, 1, OutputCoalescer.MaxFrameBytes));
    }

    /// <summary>
    /// A burst larger than the message limit is split, and no piece exceeds it.
    /// SignalR refuses an oversized message outright, so this is the difference
    /// between a slow screen and a dropped connection.
    /// </summary>
    [Fact]
    public async Task ABurstLargerThanTheMessageLimitIsSplitIntoBoundedFrames()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);

        byte[] burst = Encoding.UTF8.GetBytes(new string('x', OutputCoalescer.MaxFrameBytes * 4));
        await wrapper.SendOutputAsync(burst, default);

        await WaitUntilAsync(() => fixture.Sink.FrameSizes.Sum() == burst.Length);

        IReadOnlyList<int> frames = fixture.Sink.FrameSizes;

        Assert.All(frames, size => Assert.InRange(size, 1, OutputCoalescer.MaxFrameBytes));
        Assert.True(frames.Count >= 4, $"expected at least 4 frames, got {frames.Count}.");
    }

    /// <summary>
    /// The last thing a program writes still arrives, even though it lands between two
    /// ticks. This is the moment the user is most likely to be looking: the error the
    /// program printed just before it stopped.
    /// </summary>
    [Fact]
    public async Task TheFinalOutputSurvivesTheSessionEnding()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);

        await wrapper.SendOutputAsync(Encoding.UTF8.GetBytes("last words"), default);
        await wrapper.CloseSessionAsync(7, default);

        await WaitUntilAsync(() => fixture.Sink.LastExitCode == 7);

        Assert.Contains("last words", fixture.Sink.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutesInputResizeAndInterruptToTheRightWrapper()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient first = await fixture.ConnectAsync();
        AgentPipeClient second = await fixture.ConnectAsync();

        string a = await first.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);
        string b = await second.OpenSessionAsync(StartInfo("cmd.exe"), default).WaitAsync(Timeout);
        await fixture.WaitForCountAsync(2);

        await fixture.Host.Sessions.SendInputAsync(b, Encoding.UTF8.GetBytes("dir\r"));
        await fixture.Host.Sessions.ResizeAsync(b, 100, 40);
        await fixture.Host.Sessions.InterruptAsync(b);

        var received = new List<AgentCommand>();
        while (received.Count < 3)
        {
            received.Add(await second.Commands.ReadAsync(default).AsTask().WaitAsync(Timeout));
        }

        Assert.Equal("dir\r", Encoding.UTF8.GetString(Assert.IsType<AgentCommand.Input>(received[0]).Bytes));
        AgentCommand.Resize resize = Assert.IsType<AgentCommand.Resize>(received[1]);
        Assert.Equal((100, 40), (resize.Cols, resize.Rows));
        Assert.IsType<AgentCommand.Interrupt>(received[2]);

        // The other session must not have seen any of it.
        Assert.False(first.Commands.TryRead(out _));
        Assert.Equal((100, 40), (fixture.Host.Sessions.Get(b).Cols, fixture.Host.Sessions.Get(b).Rows));
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// An unroutable keystroke must fail loudly. Dropping it silently is worse than
    /// an error, because the user cannot tell it apart from a slow command.
    /// </summary>
    [Fact]
    public async Task RejectsMessagesForUnknownSessions()
    {
        await using var fixture = await AgentFixture.StartAsync();

        var unknown = Guid.NewGuid().ToString("n");

        UnknownSessionException ex = await Assert.ThrowsAsync<UnknownSessionException>(
            async () => await fixture.Host.Sessions.SendInputAsync(unknown, new byte[] { 3 }));

        Assert.Equal(unknown, ex.SessionId);
        await Assert.ThrowsAsync<UnknownSessionException>(
            async () => await fixture.Host.Sessions.ResizeAsync(unknown, 80, 24));
        await Assert.ThrowsAsync<UnknownSessionException>(
            async () => await fixture.Host.Sessions.InterruptAsync(unknown));
    }

    /// <summary>
    /// Two agents for one user would each own half the sessions and neither would be
    /// able to show the user a complete machine, so the second must refuse to start.
    /// </summary>
    [Fact]
    public async Task RefusesToStartWhenAnotherAgentOwnsThePipe()
    {
        await using var fixture = await AgentFixture.StartAsync();

        await using var second = new AgentHost(
            MachineIdentity.Load(Path.Combine(Path.GetTempPath(), $"1remote-{Guid.NewGuid():N}", "machine.json")),
            server: new AgentPipeServer(fixture.Host.PipeName));

        await Assert.ThrowsAsync<AgentAlreadyRunningException>(
            async () => await second.RunAsync(new CancellationTokenSource(Timeout).Token));
    }

    private static SessionStartInfo StartInfo(string program) =>
        new(program, [], @"C:\work", 120, 30, null);

    private static async Task<PipeEnvelope> Receive(AgentPipeConnection connection)
    {
        PipeEnvelope? envelope = await connection.ReceiveAsync(new CancellationTokenSource(Timeout).Token);
        Assert.NotNull(envelope);
        return envelope;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The expected agent state never arrived.");
    }

    /// <summary>A running agent on a private pipe, plus the wrappers a test attaches to it.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class AgentFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<AgentPipeClient> _clients = [];
        private readonly string _identityDirectory;
        private Task _run = Task.CompletedTask;

        private AgentFixture(AgentHost host, RecordingSink sink, string identityDirectory)
        {
            Host = host;
            Sink = sink;
            _identityDirectory = identityDirectory;
        }

        public AgentHost Host { get; }

        public RecordingSink Sink { get; }

        public static async Task<AgentFixture> StartAsync(
            Func<ChatCreateMessage, CancellationToken, Task<ChatCreatedMessage>>? createChat = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"1remote-agent-{Guid.NewGuid():N}");
            var sink = new RecordingSink();
            var host = new AgentHost(
                MachineIdentity.Load(Path.Combine(directory, "machine.json")),
                sink: sink,
                server: new AgentPipeServer($"1remotecli-test-{Guid.NewGuid():N}"),
                createChat: createChat);

            var fixture = new AgentFixture(host, sink, directory);
            fixture._run = Task.Run(() => host.RunAsync(fixture._stopping.Token));

            // Waited for, not slept through. A named pipe shows up in the object
            // namespace the moment its first instance is created, so this asks the
            // question directly rather than guessing how long Task.Run takes to be
            // scheduled.
            //
            // A sleep was enough for most tests here only because the client retries
            // until the timeout. RefusesToStartWhenAnotherAgentOwnsThePipe has no
            // client: it races a second host against this one, and if this one has not
            // reached NamedPipeServerStreamAcl.Create yet, the second takes the name
            // with FirstPipeInstance and succeeds -- so the test fails claiming the
            // agent allows two copies of itself. That failed on CI and never here.
            await WaitUntilAsync(() => File.Exists($@"\\.\pipe\{host.PipeName}"));
            return fixture;
        }

        public async Task<AgentPipeClient> ConnectAsync()
        {
            AgentPipeClient client = await AgentPipeClient.ConnectAsync(Host.PipeName, Timeout);
            _clients.Add(client);
            return client;
        }

        public async Task<TerminalSession> WaitForSessionAsync(string sessionId)
        {
            await WaitUntilAsync(() => Host.Sessions.TryGet(sessionId, out _));
            return Host.Sessions.Get(sessionId);
        }

        public Task WaitForCountAsync(int expected) => WaitUntilAsync(() => Host.Sessions.Count == expected);

        public async Task StopHostAsync()
        {
            await _stopping.CancelAsync();
            await _run.WaitAsync(Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (AgentPipeClient client in _clients)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (IOException)
                {
                }
            }

            await _stopping.CancelAsync();
            await Host.DisposeAsync();
            await Task.WhenAny(_run, Task.Delay(TimeSpan.FromSeconds(5)));
            _stopping.Dispose();

            if (Directory.Exists(_identityDirectory))
            {
                Directory.Delete(_identityDirectory, recursive: true);
            }
        }
    }

    private sealed class RecordingSink : ISessionSink
    {
        private readonly StringBuilder _output = new();
        private readonly List<int> _frameSizes = [];
        private readonly object _lock = new();

        public string Output
        {
            get
            {
                lock (_lock)
                {
                    return _output.ToString();
                }
            }
        }

        /// <summary>The size of every frame, in order. Coalescing is a claim about these.</summary>
        public IReadOnlyList<int> FrameSizes
        {
            get
            {
                lock (_lock)
                {
                    return [.. _frameSizes];
                }
            }
        }

        public int? LastExitCode { get; private set; }

        public ValueTask OnOpenedAsync(TerminalSession session, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnOutputAsync(
            TerminalSession session,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _frameSizes.Add(bytes.Length);
                _output.Append(Encoding.UTF8.GetString(bytes.Span));
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnClosedAsync(
            TerminalSession session,
            int exitCode,
            CancellationToken cancellationToken = default)
        {
            LastExitCode = exitCode;
            return ValueTask.CompletedTask;
        }
    }
}
