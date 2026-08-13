using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Hub;
using OneRemoteCli.Protocol;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Daemon.Tests;

/// <summary>
/// The agent's hub client against a real SignalR server on a real socket, speaking
/// real MessagePack.
/// <para>
/// The server here is a stand-in rather than the production hub, because the daemon
/// targets <c>net8.0-windows</c> and the hub does not — but it implements the same
/// method names from the same shared <see cref="HubMethods"/> constants and the same
/// message types, so the two halves cannot drift apart without one side failing to
/// compile. The hub's own routing is covered by its own tests.
/// </para>
/// </summary>
public sealed class AgentHubClientTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly Recorder _recorder = new();
    private WebApplication _server = null!;
    private Uri _hubUri = null!;
    private readonly List<CancellationTokenSource> _running = [];
    private readonly List<AgentHubClient> _clients = [];

    public async Task InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_recorder);
        builder.Services.AddSignalR().AddMessagePackProtocol();

        _server = builder.Build();
        _server.MapHub<RecordingHub>("/hub");

        await _server.StartAsync();

        _hubUri = new Uri(new Uri(_server.Urls.First()), "hub");
    }

    public async Task DisposeAsync()
    {
        foreach (CancellationTokenSource cts in _running)
        {
            cts.Cancel();
            cts.Dispose();
        }

        foreach (AgentHubClient client in _clients)
        {
            await client.DisposeAsync();
        }

        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task IntroducesTheMachineAsSoonAsItConnects()
    {
        await StartAsync(new SessionRegistry());

        RegisterMachineRequest request = await Next<RegisterMachineRequest>();

        Assert.Equal("machine-under-test", request.MachineId);
        Assert.Equal("Test Machine", request.DisplayName);
        Assert.Equal(ProtocolVersion.Current, request.ProtocolVersion);
        Assert.NotEmpty(request.Os);
        Assert.NotEmpty(request.AgentVersion);
    }

    [Fact]
    public async Task RepublishesSessionsThatWereAlreadyRunning()
    {
        // An agent that connects late — or reconnects — must not present an online
        // machine with nothing on it. The hub forgets a machine's sessions when its
        // agent drops, so re-announcing them is the only way they come back.
        var sessions = new SessionRegistry();
        sessions.Add("pwsh", [], @"C:\Projects", 120, 30, null, new FakeChannel());
        sessions.Add("claude", ["--resume"], @"C:\Projects", 80, 24, "Claude", new FakeChannel());

        await StartAsync(sessions);

        await Next<RegisterMachineRequest>();

        AgentSessionOpenedNotification first = await Next<AgentSessionOpenedNotification>();
        AgentSessionOpenedNotification second = await Next<AgentSessionOpenedNotification>();

        Assert.Equal(
            new[] { "claude", "pwsh" },
            new[] { first.Session.Program, second.Session.Program }.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReportsSessionsAsWrappersComeAndGo()
    {
        var sessions = new SessionRegistry();
        AgentHubClient client = await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession session = sessions.Add("pwsh", ["-nologo"], @"C:\Work", 100, 40, "Shell", new FakeChannel());
        await client.OnOpenedAsync(session);

        AgentSessionOpenedNotification opened = await Next<AgentSessionOpenedNotification>();
        Assert.Equal(session.SessionId, opened.Session.SessionId);
        Assert.Equal("pwsh", opened.Session.Program);
        Assert.Equal(new[] { "-nologo" }, opened.Session.Args);
        Assert.Equal(@"C:\Work", opened.Session.Cwd);
        Assert.Equal(100, opened.Session.Cols);
        Assert.Equal("Shell", opened.Session.DisplayName);

        await client.OnClosedAsync(session, exitCode: 3);

        AgentSessionClosedNotification closed = await Next<AgentSessionClosedNotification>();
        Assert.Equal(session.SessionId, closed.SessionId);
        Assert.Equal(3, closed.ExitCode);
    }

    [Fact]
    public async Task StreamsOutputWithAnIncreasingSequence()
    {
        var sessions = new SessionRegistry();
        AgentHubClient client = await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession session = sessions.Add("pwsh", [], @"C:\", 80, 24, null, new FakeChannel());
        await client.OnOpenedAsync(session);
        await Next<AgentSessionOpenedNotification>();

        await client.OnOutputAsync(session, "hello "u8.ToArray());
        await client.OnOutputAsync(session, "world"u8.ToArray());

        TerminalOutputNotification first = await Next<TerminalOutputNotification>();
        TerminalOutputNotification second = await Next<TerminalOutputNotification>();

        Assert.Equal("hello "u8.ToArray(), first.Data);
        Assert.Equal("world"u8.ToArray(), second.Data);
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(TerminalOutputKind.Delta, first.Kind);
    }

    [Fact]
    public async Task SendsNothingForAnEmptyWrite()
    {
        // ConPTY produces empty reads; forwarding them would be pure overhead.
        var sessions = new SessionRegistry();
        AgentHubClient client = await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession session = sessions.Add("pwsh", [], @"C:\", 80, 24, null, new FakeChannel());
        await client.OnOpenedAsync(session);
        await Next<AgentSessionOpenedNotification>();

        await client.OnOutputAsync(session, ReadOnlyMemory<byte>.Empty);
        await client.OnOutputAsync(session, "real"u8.ToArray());

        Assert.Equal("real"u8.ToArray(), (await Next<TerminalOutputNotification>()).Data);
    }

    [Fact]
    public async Task DeliversInputToTheSessionThatOwnsIt()
    {
        var sessions = new SessionRegistry();
        var wanted = new FakeChannel();
        var other = new FakeChannel();

        await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession target = sessions.Add("pwsh", [], @"C:\", 80, 24, null, wanted);
        sessions.Add("claude", [], @"C:\", 80, 24, null, other);

        await SendToAgentAsync(
            HubMethods.Agent.SendInput,
            new SendInputNotification { SessionId = target.SessionId, Data = "dir\r"u8.ToArray() });

        Assert.Equal("dir\r"u8.ToArray(), await wanted.NextInputAsync());
        Assert.Empty(other.Inputs);
    }

    [Fact]
    public async Task DeliversAnInterrupt()
    {
        // The action the product exists for, so it gets its own test rather than
        // riding along with the others.
        var sessions = new SessionRegistry();
        var channel = new FakeChannel();

        await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession session = sessions.Add("claude", [], @"C:\", 80, 24, null, channel);

        await SendToAgentAsync(
            HubMethods.Agent.InterruptSession,
            new InterruptSessionNotification { SessionId = session.SessionId });

        await channel.NextInterruptAsync();
    }

    [Fact]
    public async Task DeliversAResize()
    {
        var sessions = new SessionRegistry();
        var channel = new FakeChannel();

        await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession session = sessions.Add("pwsh", [], @"C:\", 120, 30, null, channel);

        await SendToAgentAsync(
            HubMethods.Agent.ResizeTerminal,
            new ResizeTerminalNotification { SessionId = session.SessionId, Cols = 60, Rows = 20 });

        Assert.Equal((60, 20), await channel.NextResizeAsync());
        Assert.Equal(60, session.Cols);
        Assert.Equal(20, session.Rows);
    }

    [Fact]
    public async Task AdoptsThePhonesGeometryTheMomentItAttaches()
    {
        // The phone is authoritative while attached, and it should not have to send a
        // separate resize to say what it already said in the attach.
        var sessions = new SessionRegistry();
        var channel = new FakeChannel();

        await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        TerminalSession session = sessions.Add("pwsh", [], @"C:\", 200, 50, null, channel);

        await SendToAgentAsync(
            HubMethods.Agent.AttachRequested,
            new AttachRequestedNotification
            {
                SessionId = session.SessionId,
                ClientConnectionId = "phone",
                Cols = 40,
                Rows = 18,
            });

        Assert.Equal((40, 18), await channel.NextResizeAsync());
    }

    [Fact]
    public async Task SurvivesARequestForASessionThatHasAlreadyExited()
    {
        // A real race: a phone can press a key in the instant after a program exits.
        // Letting it throw would tear down the connection for every other session.
        var sessions = new SessionRegistry();
        var channel = new FakeChannel();

        AgentHubClient client = await StartAsync(sessions);
        await Next<RegisterMachineRequest>();

        await SendToAgentAsync(
            HubMethods.Agent.SendInput,
            new SendInputNotification { SessionId = "already-gone", Data = "y\r"u8.ToArray() });

        TerminalSession session = sessions.Add("pwsh", [], @"C:\", 80, 24, null, channel);

        await SendToAgentAsync(
            HubMethods.Agent.SendInput,
            new SendInputNotification { SessionId = session.SessionId, Data = "still here\r"u8.ToArray() });

        Assert.Equal("still here\r"u8.ToArray(), await channel.NextInputAsync());
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task DropsOutputRatherThanQueueingItWhenTheHubIsUnreachable()
    {
        // No buffer until Stage 3. An unbounded queue behind an unreachable hub would
        // turn somebody's network problem into a memory problem on their own machine.
        var sessions = new SessionRegistry();
        await using var client = new AgentHubClient(
            new Uri("http://127.0.0.1:1/hub"),
            Identity(),
            sessions,
            _ => Task.FromResult<string?>("token"));

        TerminalSession session = sessions.Add("pwsh", [], @"C:\", 80, 24, null, new FakeChannel());

        await client.OnOpenedAsync(session);
        await client.OnOutputAsync(session, "into the void"u8.ToArray());
        await client.OnClosedAsync(session, 0);

        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task WaitsForSomebodyToSignInInsteadOfGivingUp()
    {
        // The agent is expected to start before anyone signs in — at boot, say — and
        // must keep the machine's local sessions working while it waits.
        var sessions = new SessionRegistry();
        var logs = new RecordingLogger();

        await using var client = new AgentHubClient(
            _hubUri,
            Identity(),
            sessions,
            _ => Task.FromResult<string?>(null),
            logs.CreateLogger("agent"));

        using var stopping = new CancellationTokenSource();
        Task run = client.RunAsync(stopping.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(run.IsFaulted);
        Assert.False(client.IsConnected);
        Assert.Contains("1remote login", logs.All(), StringComparison.Ordinal);

        stopping.Cancel();
        await run;
    }

    [Fact]
    public async Task ReportsAHubThatRefusesTheMachine()
    {
        // A refusal is not an exception, so it would be silently ignored unless the
        // agent looks at what the hub returned.
        _recorder.RefuseRegistration = new ErrorNotification
        {
            Code = ErrorCodes.AccountNotAllowed,
            Message = "Ask an administrator.",
        };

        var logs = new RecordingLogger();
        await StartAsync(new SessionRegistry(), logs);

        await Next<RegisterMachineRequest>();
        await WaitUntil(() => logs.All().Contains(ErrorCodes.AccountNotAllowed, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "https://1remotecli-hub.azurewebsites.net/hub")]
    [InlineData("", "https://1remotecli-hub.azurewebsites.net/hub")]
    [InlineData("not a url", "https://1remotecli-hub.azurewebsites.net/hub")]
    [InlineData("http://localhost:5199", "http://localhost:5199/hub")]
    [InlineData("http://localhost:5199/", "http://localhost:5199/hub")]
    [InlineData("http://localhost:5199/hub", "http://localhost:5199/hub")]
    [InlineData("https://relay.example.com/1remote", "https://relay.example.com/1remote/hub")]
    public void ResolvesTheHubAddressFromWhateverThePersonTyped(string? configured, string expected) =>
        Assert.Equal(expected, HubEndpoint.Resolve(configured).ToString());

    // Helpers.

    private async Task<AgentHubClient> StartAsync(SessionRegistry sessions, RecordingLogger? logs = null)
    {
        var client = new AgentHubClient(
            _hubUri,
            Identity(),
            sessions,
            _ => Task.FromResult<string?>("token"),
            logs?.CreateLogger("agent") ?? NullLogger.Instance);

        _clients.Add(client);

        var stopping = new CancellationTokenSource();
        _running.Add(stopping);

        _ = client.RunAsync(stopping.Token);

        await WaitUntil(() => client.IsConnected);

        return client;
    }

    private static MachineIdentity Identity() =>
        new("machine-under-test", "Test Machine");

    private async Task SendToAgentAsync<T>(string method, T notification)
    {
        string connectionId = await _recorder.ConnectionId.Task.WaitAsync(Patience);

        await _server.Services
            .GetRequiredService<IHubContext<RecordingHub>>()
            .Clients.Client(connectionId)
            .SendAsync(method, notification);
    }

    private async Task<T> Next<T>()
    {
        using var timeout = new CancellationTokenSource(Patience);

        try
        {
            while (true)
            {
                object call = await _recorder.Calls.Reader.ReadAsync(timeout.Token);

                if (call is T wanted)
                {
                    return wanted;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The agent never sent a {typeof(T).Name}.");
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition never became true.");
    }

    /// <summary>Records everything the agent says, and hands out its connection id so tests can talk back.</summary>
    private sealed class Recorder
    {
        public Channel<object> Calls { get; } = Channel.CreateUnbounded<object>();

        public TaskCompletionSource<string> ConnectionId { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ErrorNotification? RefuseRegistration { get; set; }
    }

    private sealed class RecordingHub(Recorder recorder) : Microsoft.AspNetCore.SignalR.Hub
    {
        public override Task OnConnectedAsync()
        {
            recorder.ConnectionId.TrySetResult(Context.ConnectionId);
            return base.OnConnectedAsync();
        }

        public ErrorNotification? RegisterMachine(RegisterMachineRequest request)
        {
            recorder.Calls.Writer.TryWrite(request);
            return recorder.RefuseRegistration;
        }

        public ErrorNotification? SessionOpened(AgentSessionOpenedNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return null;
        }

        public ErrorNotification? SessionClosed(AgentSessionClosedNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return null;
        }

        public void TerminalOutput(TerminalOutputNotification notification) =>
            recorder.Calls.Writer.TryWrite(notification);

        public ErrorNotification? SessionAwaitingInput(SessionAwaitingInputNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return null;
        }
    }

    /// <summary>A wrapper that only remembers what it was told.</summary>
    private sealed class FakeChannel : ISessionChannel
    {
        private readonly Channel<byte[]> _inputs = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<(int Cols, int Rows)> _resizes = Channel.CreateUnbounded<(int, int)>();
        private readonly Channel<bool> _interrupts = Channel.CreateUnbounded<bool>();

        public List<byte[]> Inputs { get; } = [];

        public ValueTask SendInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            byte[] copy = bytes.ToArray();
            Inputs.Add(copy);
            _inputs.Writer.TryWrite(copy);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendResizeAsync(int cols, int rows, CancellationToken cancellationToken = default)
        {
            _resizes.Writer.TryWrite((cols, rows));
            return ValueTask.CompletedTask;
        }

        public ValueTask SendInterruptAsync(CancellationToken cancellationToken = default)
        {
            _interrupts.Writer.TryWrite(true);
            return ValueTask.CompletedTask;
        }

        public async Task<byte[]> NextInputAsync() => await Read(_inputs);

        public async Task<(int Cols, int Rows)> NextResizeAsync() => await Read(_resizes);

        public async Task NextInterruptAsync() => await Read(_interrupts);

        private static async Task<T> Read<T>(Channel<T> channel)
        {
            using var timeout = new CancellationTokenSource(Patience);

            try
            {
                return await channel.Reader.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The wrapper was never given a {typeof(T).Name}.");
            }
        }
    }
}
