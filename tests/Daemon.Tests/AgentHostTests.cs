using System.Runtime.Versioning;
using System.Text;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Ipc;
using OneRemoteCli.Daemon.Wrapper;

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

    [Fact]
    public async Task ForwardsOutputToTheSink()
    {
        await using var fixture = await AgentFixture.StartAsync();

        AgentPipeClient wrapper = await fixture.ConnectAsync();
        await wrapper.OpenSessionAsync(StartInfo("pwsh"), default).WaitAsync(Timeout);

        await wrapper.SendOutputAsync(Encoding.UTF8.GetBytes("hello"), default);

        await WaitUntilAsync(() => fixture.Sink.Output.Contains("hello"));
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

        public static async Task<AgentFixture> StartAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"1remote-agent-{Guid.NewGuid():N}");
            var sink = new RecordingSink();
            var host = new AgentHost(
                MachineIdentity.Load(Path.Combine(directory, "machine.json")),
                sink: sink,
                server: new AgentPipeServer($"1remotecli-test-{Guid.NewGuid():N}"));

            var fixture = new AgentFixture(host, sink, directory);
            fixture._run = Task.Run(() => host.RunAsync(fixture._stopping.Token));

            // The accept loop must be listening before a client tries to connect;
            // the client's own retry covers the rest.
            await Task.Delay(50);
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
