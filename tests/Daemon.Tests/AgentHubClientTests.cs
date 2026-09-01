using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneRemoteCli.Daemon.Agent;
using OneRemoteCli.Daemon.Chat;
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
        builder.Services
            .AddSignalR(options => options.MaximumReceiveMessageSize = 1024 * 1024)
            .AddMessagePackProtocol();

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
        Assert.Equal(NotificationLevel.AllAttentionEvents, request.NotificationLevel);
        Assert.NotEmpty(request.Os);
        Assert.NotEmpty(request.AgentVersion);
    }

    [Fact]
    public async Task RegistersAndChangesThePhoneNotificationLevel()
    {
        AgentHubClient client = await StartAsync(
            new SessionRegistry(),
            notificationLevel: NotificationLevel.ActionRequired);

        RegisterMachineRequest registration = await Next<RegisterMachineRequest>();
        Assert.Equal(NotificationLevel.ActionRequired, registration.NotificationLevel);

        client.SetNotificationLevel(NotificationLevel.Off);

        SetMachineNotificationLevelRequest update =
            await Next<SetMachineNotificationLevelRequest>();
        Assert.Equal(NotificationLevel.Off, update.NotificationLevel);
        Assert.Equal(NotificationLevel.Off, client.NotificationLevel);
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
    public async Task SavesTerminalUploadChunksAndReturnsTheRemotePath()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-upload-{Guid.NewGuid():n}");

        try
        {
            var sessions = new SessionRegistry(root);
            await StartAsync(sessions);
            await Next<RegisterMachineRequest>();

            TerminalSession session =
                sessions.Add("pwsh", [], @"C:\", 80, 24, null, new FakeChannel());
            string uploadId = Guid.NewGuid().ToString();

            TerminalUploadReply begun = await InvokeAgentAsync<BeginTerminalUploadNotification, TerminalUploadReply>(
                HubMethods.Agent.BeginTerminalUpload,
                new BeginTerminalUploadNotification
                {
                    SessionId = session.SessionId,
                    ClientConnectionId = "phone",
                    UploadId = uploadId,
                    FileName = "hello.txt",
                    TotalBytes = 5,
                });
            Assert.Null(begun.ErrorCode);

            TerminalUploadReply completed =
                await InvokeAgentAsync<TerminalUploadChunkNotification, TerminalUploadReply>(
                    HubMethods.Agent.UploadTerminalChunk,
                    new TerminalUploadChunkNotification
                    {
                        SessionId = session.SessionId,
                        ClientConnectionId = "phone",
                        UploadId = uploadId,
                        Offset = 0,
                        Data = "hello"u8.ToArray(),
                    });

            Assert.Equal("hello", File.ReadAllText(completed.RemotePath!));
            Assert.True(sessions.Remove(session.SessionId));
            Assert.False(File.Exists(completed.RemotePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagesChatAttachmentsAndSendsThemAsAcpPromptContent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-chat-attachment-{Guid.NewGuid():n}");
        var prompts = new List<JsonObject>();

        Task<JsonElement> Call(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            if (method == "session/prompt")
            {
                prompts.Add(parameters);
            }

            return Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "chat-1" }),
                "session/prompt" => JsonSerializer.SerializeToElement(new { stopReason = "end_turn" }),
                _ => throw new InvalidOperationException(method),
            });
        }

        try
        {
            await using var chat = new AcpProvider(Call);
            AgentHubClient client = await StartAsync(
                new SessionRegistry(),
                chatAttachmentRoot: root);
            client.AttachChatProvider(chat);
            await Next<RegisterMachineRequest>();

            AcpSession session = await chat.CreateAsync(@"C:\repo", "Chat");
            session.Loaded = true;
            session.UpdateLocalTasks(
            [
                new ChatTaskEntry
                {
                    TaskId = "send",
                    Title = "Send image prompt",
                    Status = "in_progress",
                    DependsOn = ["stage"],
                },
            ]);
            await chat.ApplyCapabilitiesAsync(new AcpPromptCapabilities(Image: true, EmbeddedContext: true));

            AgentSessionUpdatedNotification updated = await Next<AgentSessionUpdatedNotification>();
            Assert.NotNull(updated.Session.ChatCapabilities);
            Assert.True(updated.Session.ChatCapabilities!.Image);
            ChatTaskEntry advertisedTask = Assert.Single(updated.Session.LocalTasks!);
            Assert.Equal("send", advertisedTask.TaskId);
            Assert.Equal(["stage"], advertisedTask.DependsOn);

            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x07];
            string attachmentId = Guid.NewGuid().ToString();

            ChatAttachmentReply begun =
                await InvokeAgentAsync<BeginChatAttachmentNotification, ChatAttachmentReply>(
                    HubMethods.Agent.BeginChatAttachment,
                    new BeginChatAttachmentNotification
                    {
                        SessionId = session.SessionId,
                        ClientConnectionId = "phone",
                        AttachmentId = attachmentId,
                        FileName = "receipt.png",
                        MimeType = "image/png",
                        TotalBytes = png.Length,
                    });
            Assert.Null(begun.ErrorCode);

            ChatAttachmentReply staged =
                await InvokeAgentAsync<ChatAttachmentChunkNotification, ChatAttachmentReply>(
                    HubMethods.Agent.UploadChatAttachmentChunk,
                    new ChatAttachmentChunkNotification
                    {
                        SessionId = session.SessionId,
                        ClientConnectionId = "phone",
                        AttachmentId = attachmentId,
                        Offset = 0,
                        Data = png,
                    });
            Assert.True(staged.Completed);
            Assert.NotEmpty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));

            // Another client cannot spend an attachment it did not stage.
            ChatPromptReply stolen = await InvokeAgentAsync<SendChatPromptNotification, ChatPromptReply>(
                HubMethods.Agent.SendChatPrompt,
                new SendChatPromptNotification
                {
                    SessionId = session.SessionId,
                    ClientConnectionId = "other-phone",
                    Text = "mine now",
                    AttachmentIds = [attachmentId],
                });
            Assert.False(stolen.Accepted);
            Assert.Equal(ErrorCodes.AttachmentNotFound, stolen.ErrorCode);

            ChatPromptReply accepted = await InvokeAgentAsync<SendChatPromptNotification, ChatPromptReply>(
                HubMethods.Agent.SendChatPrompt,
                new SendChatPromptNotification
                {
                    SessionId = session.SessionId,
                    ClientConnectionId = "phone",
                    Text = "what does this say?",
                    AttachmentIds = [attachmentId],
                });

            Assert.True(accepted.Accepted);
            Assert.Null(accepted.ErrorCode);

            await WaitUntil(() => prompts.Count == 1);
            JsonArray content = prompts[0]["prompt"]!.AsArray();
            Assert.Equal("text", content[0]!["type"]!.GetValue<string>());
            Assert.Equal("image", content[1]!["type"]!.GetValue<string>());
            Assert.Equal("image/png", content[1]!["mimeType"]!.GetValue<string>());
            Assert.Equal(Convert.ToBase64String(png), content[1]!["data"]!.GetValue<string>());

            // Consumed means gone: the staged copy of the user's photo does not
            // outlive the prompt that spent it.
            await WaitUntil(() => !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());

            ChatTranscriptNotification transcript = await Next<ChatTranscriptNotification>();
            ChatEvent echoed = Assert.Single(transcript.Events);
            Assert.Equal("what does this say?", echoed.Text);
            Assert.Equal("receipt.png", Assert.Single(echoed.Content).Name);
            Assert.All(echoed.Content, block => Assert.Null(block.Data));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SplitsLargeChatSnapshotsIntoBoundedOrderedFrames()
    {
        AgentHubClient client = await StartAsync(new SessionRegistry());
        await Next<RegisterMachineRequest>();

        var session = new AcpSession(
            "large-chat",
            @"C:\repo",
            "Large chat",
            DateTimeOffset.UtcNow);
        ChatEvent[] events =
        [
            .. Enumerable.Range(0, 256).Select(index => new ChatEvent
            {
                EventId = $"event-{index:D3}",
                Kind = ChatEventKind.AgentMessage,
                Text = new string((char)('a' + index % 26), 16 * 1024),
            }),
        ];

        await client.OnChatTranscriptAsync(
            session,
            ChatTranscriptKind.Snapshot,
            events,
            targetConnectionId: "phone");

        var frames = new List<ChatTranscriptNotification>();
        while (frames.Sum(frame => frame.Events.Length) < events.Length)
        {
            frames.Add(await Next<ChatTranscriptNotification>());
        }

        Assert.True(frames.Count > 1);
        Assert.Equal(ChatTranscriptKind.Snapshot, frames[0].Kind);
        Assert.All(frames.Skip(1), frame => Assert.Equal(ChatTranscriptKind.Delta, frame.Kind));
        Assert.All(frames, frame => Assert.Equal("phone", frame.TargetConnectionId));
        Assert.Equal(
            events.Select(item => item.EventId),
            frames.SelectMany(frame => frame.Events).Select(item => item.EventId));

        var options = new MessagePackHubProtocolOptions().SerializerOptions;
        Assert.All(
            frames,
            frame => Assert.True(
                MessagePackSerializer.Serialize(frame, options).Length <=
                AgentHubClient.MaximumChatTranscriptFrameBytes));
    }

    [Fact]
    public async Task DetachingRemovesTheAttachmentsThatClientStaged()
    {
        string root = Path.Combine(Path.GetTempPath(), $"agent-chat-detach-{Guid.NewGuid():n}");

        Task<JsonElement> Call(string method, JsonObject parameters, CancellationToken cancellationToken) =>
            Task.FromResult(method switch
            {
                "session/new" => JsonSerializer.SerializeToElement(new { sessionId = "chat-1" }),
                _ => throw new InvalidOperationException(method),
            });

        try
        {
            await using var chat = new AcpProvider(Call);
            AgentHubClient client = await StartAsync(
                new SessionRegistry(),
                chatAttachmentRoot: root);
            client.AttachChatProvider(chat);
            await Next<RegisterMachineRequest>();

            AcpSession session = await chat.CreateAsync(@"C:\repo", "Chat");
            string attachmentId = Guid.NewGuid().ToString();

            await InvokeAgentAsync<BeginChatAttachmentNotification, ChatAttachmentReply>(
                HubMethods.Agent.BeginChatAttachment,
                new BeginChatAttachmentNotification
                {
                    SessionId = session.SessionId,
                    ClientConnectionId = "phone",
                    AttachmentId = attachmentId,
                    FileName = "half.png",
                    MimeType = "image/png",
                    TotalBytes = 4,
                });

            await SendToAgentAsync(
                HubMethods.Agent.DetachRequested,
                new DetachRequestedNotification
                {
                    SessionId = session.SessionId,
                    ClientConnectionId = "phone",
                });

            await WaitUntil(() => !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

    [Fact]
    public async Task ReportsANotificationTheHubCouldNotTake()
    {
        // The counterpart to the test below: a notification that fails while the agent
        // is running is a real problem and must still be reported, or suppressing the
        // shutdown case would have quietly suppressed everything.
        var sessions = new SessionRegistry();
        var logs = new RecordingLogger();

        AgentHubClient client = await StartAsync(sessions, logs);
        await Next<RegisterMachineRequest>();

        _recorder.SessionClosedGate = new TaskCompletionSource<ErrorNotification?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TerminalSession session = sessions.Add("pwsh", [], @"C:\Work", 100, 40, null, new FakeChannel());
        Task goodbye = client.OnClosedAsync(session, 0).AsTask();

        await _recorder.SessionClosedEntered.Task.WaitAsync(Patience);
        _recorder.SessionClosedGate.TrySetException(new InvalidOperationException("the hub fell over"));

        await goodbye.WaitAsync(Patience);

        Assert.Contains("[1900]", logs.All(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotReportTheGoodbyeItCouldNotDeliverWhenQuitting()
    {
        // Sessions and the relay are cancelled together, so the last SessionClosed can
        // land while the connection is being torn down. Reporting that would end every
        // clean exit with an error line, and a channel that cries wolf on every
        // shutdown is one nobody reads on the day it means something.
        var sessions = new SessionRegistry();
        var logs = new RecordingLogger();

        await using var client = new AgentHubClient(
            _hubUri,
            Identity(),
            sessions,
            _ => Task.FromResult<string?>("token"),
            logs.CreateLogger("agent"));

        using var stopping = new CancellationTokenSource();
        Task run = client.RunAsync(stopping.Token);

        await WaitUntil(() => client.IsConnected);
        await Next<RegisterMachineRequest>();

        _recorder.SessionClosedGate = new TaskCompletionSource<ErrorNotification?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TerminalSession session = sessions.Add("pwsh", [], @"C:\Work", 100, 40, null, new FakeChannel());
        Task goodbye = client.OnClosedAsync(session, 0).AsTask();

        // Held open at the hub, so the goodbye is genuinely in flight when the quit
        // arrives — which is the race the agent hits for real.
        await _recorder.SessionClosedEntered.Task.WaitAsync(Patience);

        await stopping.CancelAsync();
        _recorder.SessionClosedGate.TrySetException(new InvalidOperationException("shutting down"));

        await goodbye.WaitAsync(Patience);
        await run.WaitAsync(Patience);

        Assert.DoesNotContain("[1900]", logs.All(), StringComparison.Ordinal);
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

    /// <summary>
    /// Signing out has to reach the hub, not just the local cache.
    /// <para>
    /// The token is read once per connection attempt, so an agent that is already
    /// connected would otherwise keep relaying on the old account's token until the
    /// socket happened to drop. The hub lists a machine for exactly as long as its
    /// agent holds a connection, so the only way a sign-out takes the machine off the
    /// phone is if the connection goes with it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StopsRelayingWhenTheAccountSignsOutWhileConnected()
    {
        string? token = "token";

        AgentHubClient client = Start(() => token);
        await WaitUntil(() => client.IsConnected);

        token = null;
        await client.CredentialsChangedAsync();

        await WaitUntil(() => !client.IsConnected);
        await WaitUntil(() => client.IsSignedOut);
    }

    /// <summary>
    /// And signing back in has to take effect while the user is still looking at the
    /// tray, rather than whenever the signed-out retry next comes round.
    /// </summary>
    [Fact]
    public async Task ConnectsAsSoonAsSomebodySignsBackIn()
    {
        string? token = null;

        AgentHubClient client = Start(() => token);
        await WaitUntil(() => client.IsSignedOut);

        token = "token";
        await client.CredentialsChangedAsync();

        // Patience is well under the signed-out retry, so arriving at all proves the
        // wait was cut short rather than slept through.
        await WaitUntil(() => client.IsConnected);
    }

    private async Task<AgentHubClient> StartAsync(
        SessionRegistry sessions,
        RecordingLogger? logs = null,
        NotificationLevel notificationLevel = NotificationLevel.AllAttentionEvents,
        string? chatAttachmentRoot = null)
    {
        var client = new AgentHubClient(
            _hubUri,
            Identity(),
            sessions,
            _ => Task.FromResult<string?>("token"),
            logs?.CreateLogger("agent") ?? NullLogger.Instance,
            notificationLevel: notificationLevel,
            chatAttachmentRoot: chatAttachmentRoot);

        _clients.Add(client);

        var stopping = new CancellationTokenSource();
        _running.Add(stopping);

        _ = client.RunAsync(stopping.Token);

        await WaitUntil(() => client.IsConnected);

        return client;
    }

    /// <summary>
    /// Starts a client whose token the test can change, so it can play the part of
    /// somebody signing in or out in another process.
    /// </summary>
    private AgentHubClient Start(Func<string?> token)
    {
        var client = new AgentHubClient(
            _hubUri,
            Identity(),
            new SessionRegistry(),
            _ => Task.FromResult(token()),
            NullLogger.Instance);

        _clients.Add(client);

        var stopping = new CancellationTokenSource();
        _running.Add(stopping);

        _ = client.RunAsync(stopping.Token);

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

    private async Task<TResult> InvokeAgentAsync<TRequest, TResult>(string method, TRequest request)
    {
        string connectionId = await _recorder.ConnectionId.Task.WaitAsync(Patience);

        return await _server.Services
            .GetRequiredService<IHubContext<RecordingHub>>()
            .Clients.Client(connectionId)
            .InvokeCoreAsync<TResult>(method, [request!], CancellationToken.None);
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

        /// <summary>
        /// Holds the hub's <c>SessionClosed</c> open so a test can decide when — and
        /// how — it finishes. Left null, the method answers immediately as usual.
        /// </summary>
        public TaskCompletionSource<ErrorNotification?>? SessionClosedGate { get; set; }

        /// <summary>Completes as soon as the agent's goodbye reaches the hub.</summary>
        public TaskCompletionSource SessionClosedEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ErrorNotification?> OnSessionClosed()
        {
            SessionClosedEntered.TrySetResult();

            return SessionClosedGate?.Task ?? Task.FromResult<ErrorNotification?>(null);
        }
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

        public ErrorNotification? SetMachineNotificationLevel(
            SetMachineNotificationLevelRequest request)
        {
            recorder.Calls.Writer.TryWrite(request);
            return null;
        }

        public ErrorNotification? SessionOpened(AgentSessionOpenedNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return null;
        }

        public ErrorNotification? SessionUpdated(AgentSessionUpdatedNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return null;
        }

        public void ChatTranscript(ChatTranscriptNotification notification) =>
            recorder.Calls.Writer.TryWrite(notification);

        public ErrorNotification? SessionAttention(SessionAttentionNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return null;
        }

        public Task<ErrorNotification?> SessionClosed(AgentSessionClosedNotification notification)
        {
            recorder.Calls.Writer.TryWrite(notification);
            return recorder.OnSessionClosed();
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
