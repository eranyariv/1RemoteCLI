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
using OneRemoteCli.Hub.Projects;
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
    private readonly string _projectStatePath =
        Path.Combine(Path.GetTempPath(), $"relay-hub-projects-{Guid.NewGuid():N}.json");
    private readonly string _projectIconRoot =
        Path.Combine(Path.GetTempPath(), $"relay-hub-project-icons-{Guid.NewGuid():N}");

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

                // ProjectStore, unlike the (already-shared) operator state, is
                // asserted on directly by name and by count in the tests below.
                // Left at its default path it would read and write a real file
                // shared across every run of this suite, so a second run would see
                // the first run's projects and fail on the very first duplicate
                // name. Scoped to this test class's lifetime instead.
                services.Configure<ProjectsOptions>(options =>
                {
                    options.StatePath = _projectStatePath;
                    options.IconRoot = _projectIconRoot;
                });
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

        File.Delete(_projectStatePath);

        if (Directory.Exists(_projectIconRoot))
        {
            Directory.Delete(_projectIconRoot, recursive: true);
        }
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
    public async Task TerminalUploadsReachOnlyTheAttachedTerminalAndReturnAgentConfirmedProgress()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        var began = new TaskCompletionSource<BeginTerminalUploadNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chunked = new TaskCompletionSource<TerminalUploadChunkNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable beginHandler = agent.On<BeginTerminalUploadNotification, TerminalUploadReply>(
            HubMethods.Agent.BeginTerminalUpload,
            request =>
            {
                began.TrySetResult(request);
                return new TerminalUploadReply
                {
                    UploadId = request.UploadId,
                    TotalBytes = request.TotalBytes,
                };
            });
        using IDisposable chunkHandler = agent.On<TerminalUploadChunkNotification, TerminalUploadReply>(
            HubMethods.Agent.UploadTerminalChunk,
            request =>
            {
                chunked.TrySetResult(request);
                return new TerminalUploadReply
                {
                    UploadId = request.UploadId,
                    ConfirmedBytes = request.Offset + request.Data.LongLength,
                    TotalBytes = request.Offset + request.Data.LongLength,
                    RemotePath = @"C:\Temp\photo.jpg",
                };
            });

        await OpenSessionAsync(agent, "terminal-1", "pwsh");
        await OpenSessionAsync(agent, "chat-1", "GitHub Copilot", SessionKind.AgentChat);

        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(alice, "machine-a", "terminal-1");
        string uploadId = Guid.NewGuid().ToString();

        TerminalUploadReply started = await alice.InvokeAsync<TerminalUploadReply>(
            HubMethods.Server.BeginTerminalUpload,
            new BeginTerminalUploadRequest
            {
                SessionId = "terminal-1",
                UploadId = uploadId,
                FileName = "photo.jpg",
                TotalBytes = 4,
            });
        Assert.Null(started.ErrorCode);

        BeginTerminalUploadNotification begin = await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("terminal-1", begin.SessionId);
        Assert.NotEmpty(begin.ClientConnectionId);

        TerminalUploadReply completed = await alice.InvokeAsync<TerminalUploadReply>(
            HubMethods.Server.UploadTerminalChunk,
            new TerminalUploadChunkRequest
            {
                SessionId = "terminal-1",
                UploadId = uploadId,
                Offset = 0,
                Data = [1, 2, 3, 4],
            });
        Assert.Equal(4, completed.ConfirmedBytes);
        Assert.Equal(@"C:\Temp\photo.jpg", completed.RemotePath);
        Assert.Equal([1, 2, 3, 4], (await chunked.Task.WaitAsync(TimeSpan.FromSeconds(5))).Data);

        HubConnection bystander = await ConnectClientAsync(AliceTenant, AliceObject);
        TerminalUploadReply unattached = await bystander.InvokeAsync<TerminalUploadReply>(
            HubMethods.Server.BeginTerminalUpload,
            new BeginTerminalUploadRequest
            {
                SessionId = "terminal-1",
                UploadId = Guid.NewGuid().ToString(),
                FileName = "stolen.txt",
                TotalBytes = 1,
            });
        Assert.Equal(ErrorCodes.NotAttached, unattached.ErrorCode);

        await AttachAsync(alice, "machine-a", "chat-1");
        TerminalUploadReply wrongKind = await alice.InvokeAsync<TerminalUploadReply>(
            HubMethods.Server.BeginTerminalUpload,
            new BeginTerminalUploadRequest
            {
                SessionId = "chat-1",
                UploadId = Guid.NewGuid().ToString(),
                FileName = "chat.txt",
                TotalBytes = 1,
            });
        Assert.Equal(ErrorCodes.InvalidRequest, wrongKind.ErrorCode);
    }

    [Fact]
    public async Task OversizedTerminalUploadsAreRejectedBeforeTheyReachTheAgent()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable handler = agent.On<BeginTerminalUploadNotification, TerminalUploadReply>(
            HubMethods.Agent.BeginTerminalUpload,
            request =>
            {
                received.TrySetResult();
                return new TerminalUploadReply { UploadId = request.UploadId };
            });
        await OpenSessionAsync(agent, "terminal-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "terminal-1");

        TerminalUploadReply reply = await client.InvokeAsync<TerminalUploadReply>(
            HubMethods.Server.BeginTerminalUpload,
            new BeginTerminalUploadRequest
            {
                SessionId = "terminal-1",
                UploadId = Guid.NewGuid().ToString(),
                FileName = "large.bin",
                TotalBytes = TerminalUploadLimits.MaxFileBytes + 1,
            });

        Assert.Equal(ErrorCodes.FileTooLarge, reply.ErrorCode);
        await Task.Delay(100);
        Assert.False(received.Task.IsCompleted);
    }

    [Fact]
    public async Task OlderAgentsReturnAStableUploadUnavailableError()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "terminal-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "terminal-1");

        TerminalUploadReply reply = await client.InvokeAsync<TerminalUploadReply>(
            HubMethods.Server.BeginTerminalUpload,
            new BeginTerminalUploadRequest
            {
                SessionId = "terminal-1",
                UploadId = Guid.NewGuid().ToString(),
                FileName = "notes.txt",
                TotalBytes = 1,
            });

        Assert.Equal(ErrorCodes.UploadUnavailable, reply.ErrorCode);
    }

    [Fact]
    public async Task ChatMessagesAndPermissionResponsesReachTheAttachedAgent()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<SendChatMessageNotification> messages =
            Listen<SendChatMessageNotification>(agent, HubMethods.Agent.SendChatMessage);
        Channel<RespondChatPermissionNotification> permissions =
            Listen<RespondChatPermissionNotification>(agent, HubMethods.Agent.RespondChatPermission);
        await OpenSessionAsync(agent, "chat-1", "GitHub Copilot", SessionKind.AgentChat);

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "chat-1");

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendChatMessage,
            new SendChatMessageRequest { SessionId = "chat-1", Text = "  continue  " }));
        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RespondChatPermission,
            new RespondChatPermissionRequest
            {
                SessionId = "chat-1",
                RequestId = "request-1",
                OptionId = "allow-once",
            }));

        SendChatMessageNotification message = await Next(messages);
        Assert.Equal("chat-1", message.SessionId);
        Assert.Equal("continue", message.Text);

        RespondChatPermissionNotification permission = await Next(permissions);
        Assert.Equal("request-1", permission.RequestId);
        Assert.Equal("allow-once", permission.OptionId);
    }

    [Fact]
    public async Task ChatCommandsRequireAnAttachment()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "chat-1", "GitHub Copilot", SessionKind.AgentChat);
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ErrorNotification? message = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendChatMessage,
            new SendChatMessageRequest { SessionId = "chat-1", Text = "continue" });
        ErrorNotification? permission = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.RespondChatPermission,
            new RespondChatPermissionRequest
            {
                SessionId = "chat-1",
                RequestId = "request-1",
                OptionId = "allow-once",
            });

        Assert.Equal(ErrorCodes.NotAttached, message!.Code);
        Assert.Equal(ErrorCodes.NotAttached, permission!.Code);
    }

    [Fact]
    public async Task SessionKindsCannotBeDrivenThroughTheWrongProtocol()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "terminal-1", "pwsh");
        await OpenSessionAsync(agent, "chat-1", "GitHub Copilot", SessionKind.AgentChat);
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        await AttachAsync(client, "machine-a", "terminal-1");
        ErrorNotification? chat = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendChatMessage,
            new SendChatMessageRequest { SessionId = "terminal-1", Text = "continue" });

        await AttachAsync(client, "machine-a", "chat-1");
        ErrorNotification? input = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SendInput,
            new SendInputRequest { SessionId = "chat-1", Data = "dir\r"u8.ToArray() });

        Assert.Equal(ErrorCodes.InvalidRequest, chat!.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, input!.Code);
    }

    [Fact]
    public async Task ChatTranscriptsReachWatchersAndNobodyElse()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "chat-1", "GitHub Copilot", SessionKind.AgentChat);

        HubConnection watcher = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ChatTranscriptNotification> watched =
            Listen<ChatTranscriptNotification>(watcher, HubMethods.Client.ChatTranscript);
        await AttachAsync(watcher, "machine-a", "chat-1");

        HubConnection bystander = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ChatTranscriptNotification> ignored =
            Listen<ChatTranscriptNotification>(bystander, HubMethods.Client.ChatTranscript);

        await agent.InvokeAsync(
            HubMethods.Server.ChatTranscript,
            new ChatTranscriptNotification
            {
                SessionId = "chat-1",
                Seq = 7,
                Kind = ChatTranscriptKind.Delta,
                Events =
                [
                    new ChatEvent
                    {
                        EventId = "answer",
                        Kind = ChatEventKind.AgentMessage,
                        Text = "Done",
                    },
                ],
            });

        ChatTranscriptNotification transcript = await Next(watched);
        Assert.Equal(7, transcript.Seq);
        Assert.Equal("Done", Assert.Single(transcript.Events).Text);
        await AssertSilent(ignored);
    }

    [Fact]
    public async Task LargeChatTranscriptReachesItsWatcher()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "chat-1", "GitHub Copilot", SessionKind.AgentChat);

        HubConnection watcher = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ChatTranscriptNotification> watched =
            Listen<ChatTranscriptNotification>(watcher, HubMethods.Client.ChatTranscript);
        await AttachAsync(watcher, "machine-a", "chat-1");

        string text = new('x', 4 * 1024 * 1024);
        await agent.InvokeAsync(
            HubMethods.Server.ChatTranscript,
            new ChatTranscriptNotification
            {
                SessionId = "chat-1",
                Seq = 1,
                Kind = ChatTranscriptKind.Snapshot,
                Events =
                [
                    new ChatEvent
                    {
                        EventId = "large-answer",
                        Kind = ChatEventKind.AgentMessage,
                        Text = text,
                    },
                ],
            });

        ChatTranscriptNotification transcript = await Next(watched);
        Assert.Equal(text.Length, Assert.Single(transcript.Events).Text.Length);
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

    /// <summary>
    /// The full round trip of a correction: client asks, agent is told, agent
    /// re-announces, every device hears.
    /// <para>
    /// Worth an end-to-end test rather than two unit tests, because the value of the
    /// feature is entirely in the loop closing. A client that set the type locally
    /// would pass every test of its own and still leave the phone and the settings
    /// window on the desk disagreeing about what is running.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CorrectingASessionTypeGoesToTheAgentAndComesBackToEveryClient()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        Channel<SetSessionTypeRequestedNotification> requests =
            Listen<SetSessionTypeRequestedNotification>(agent, HubMethods.Agent.SetSessionTypeRequested);
        await OpenSessionAsync(agent, "session-1", "node");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionUpdatedNotification> updates =
            Listen<ClientSessionUpdatedNotification>(client, HubMethods.Client.SessionUpdated);
        await AttachAsync(client, "machine-a", "session-1");

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionType,
            new SetSessionTypeRequest { SessionId = "session-1", CliType = CliType.ClaudeCode }));

        SetSessionTypeRequestedNotification asked = await Next(requests);
        Assert.Equal("session-1", asked.SessionId);
        Assert.Equal(CliType.ClaudeCode, asked.CliType);

        // The agent is the only writer of session state, so nothing has changed until
        // it says so.
        SessionInfo corrected = NewSession("session-1", "node");
        corrected.CliType = CliType.ClaudeCode;

        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionUpdated,
            new AgentSessionUpdatedNotification { Session = corrected }));

        ClientSessionUpdatedNotification update = await Next(updates);
        Assert.Equal("machine-a", update.MachineId);
        Assert.Equal(CliType.ClaudeCode, update.Session.CliType);
    }

    [Fact]
    public async Task RefusesACliTypeThatDoesNotExist()
    {
        // The enum arrives from a browser, so it is input rather than a type. Passing
        // it on unchecked would put a number the agent has no case for into the record
        // every client then renders.
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await AttachAsync(client, "machine-a", "session-1");

        ErrorNotification? refused = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionType,
            new SetSessionTypeRequest { SessionId = "session-1", CliType = (CliType)97 });

        Assert.Equal(ErrorCodes.InvalidRequest, refused!.Code);
    }

    /// <summary>
    /// A rename is answered by the hub, not carried to the agent, and every device
    /// this user has open is told — including the ones that are not attached to
    /// anything, which on a phone is the only state the list is ever in.
    /// </summary>
    [Fact]
    public async Task RenamingASessionReachesEveryOneOfTheUsersDevices()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection phone = await ConnectClientAsync(AliceTenant, AliceObject);
        HubConnection laptop = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionUpdatedNotification> onLaptop =
            Listen<ClientSessionUpdatedNotification>(laptop, HubMethods.Client.SessionUpdated);

        // Deliberately without attaching first. Renaming is done from the list.
        Assert.Null(await phone.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionName,
            new SetSessionNameRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                Name = "The deploy",
            }));

        ClientSessionUpdatedNotification update = await Next(onLaptop);
        Assert.Equal("machine-a", update.MachineId);
        Assert.Equal("The deploy", update.Session.CustomName);
    }

    [Fact]
    public async Task ANameSurvivesInTheListTheHubSends()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await RenameAsync(client, "machine-a", "session-1", "The deploy");
        await PinAsync(client, "machine-a", "session-1", pinned: true);

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        SessionInfo session = Assert.Single(Assert.Single(list.Machines).Sessions);
        Assert.Equal("The deploy", session.CustomName);
        Assert.True(session.Pinned);
    }

    /// <summary>
    /// The reason the label is kept beside the session record rather than on it.
    /// <para>
    /// An agent that drops off the network has its session records cleared, and gets
    /// them back by announcing the same sessions again. A name stored on the record
    /// itself would be lost to any wifi blip — which, for a feature whose whole promise
    /// is "for as long as the session runs", would be a lie.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ANameSurvivesTheAgentReconnecting()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await RenameAsync(client, "machine-a", "session-1", "The deploy");

        await agent.DisposeAsync();

        HubConnection again = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(again, "session-1", "pwsh");

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Equal("The deploy", Assert.Single(Assert.Single(list.Machines).Sessions).CustomName);
    }

    /// <summary>The name lasts as long as the session and not one moment longer.</summary>
    [Fact]
    public async Task ANameDiesWithItsSession()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await RenameAsync(client, "machine-a", "session-1", "The deploy");

        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionClosed,
            new AgentSessionClosedNotification { SessionId = "session-1", ExitCode = 0 }));

        // Same id, new session. Reusing an id is not something the agent does, but the
        // point stands whichever way the id arrives: a name belongs to a session, not
        // to a string.
        await OpenSessionAsync(agent, "session-1", "pwsh");

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).CustomName);
    }

    [Fact]
    public async Task ClearingANameRevealsTheAgentsOwnAgain()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await RenameAsync(client, "machine-a", "session-1", "The deploy");
        await RenameAsync(client, "machine-a", "session-1", null);

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).CustomName);
    }

    /// <summary>
    /// Blank is not a name. A row rendered from an empty string has nothing in it, and
    /// the way back from that is not obvious to the person who typed the spaces.
    /// </summary>
    [Fact]
    public async Task ANameOfNothingButSpacesClearsItInstead()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await RenameAsync(client, "machine-a", "session-1", "The deploy");
        await RenameAsync(client, "machine-a", "session-1", "   ");

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).CustomName);
    }

    /// <summary>
    /// The name is typed by a person and drawn on a lock screen. It is sanitised on
    /// the way in, once, rather than at each of the places it is later rendered.
    /// </summary>
    [Fact]
    public async Task ANameIsCleanedBeforeItIsStored()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        await RenameAsync(client, "machine-a", "session-1", "\u202ethe\ndeploy");

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Equal("the deploy", Assert.Single(Assert.Single(list.Machines).Sessions).CustomName);
    }

    /// <summary>
    /// Rename and pin resolve the machine from the caller's own partition rather than
    /// from an attachment, so this is the test that the partition still holds: a valid
    /// user handed a real machine id from someone else's account finds nothing.
    /// </summary>
    [Fact]
    public async Task AnotherUserCannotRenameASessionTheyCannotSee()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        ErrorNotification? refused = await bob.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionName,
            new SetSessionNameRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                Name = "mine now",
            });

        Assert.Equal(ErrorCodes.MachineNotFound, refused!.Code);

        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);
        MachineListNotification list = await alice.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).CustomName);
    }

    [Fact]
    public async Task WillNotRenameASessionThatWasNeverOpened()
    {
        await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ErrorNotification? refused = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionName,
            new SetSessionNameRequest
            {
                MachineId = "machine-a",
                SessionId = "ghost",
                Name = "The deploy",
            });

        Assert.Equal(ErrorCodes.SessionNotFound, refused!.Code);
    }

    [Fact]
    public async Task PinningAndUnpinningBothReachEveryDevice()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection phone = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionUpdatedNotification> updates =
            Listen<ClientSessionUpdatedNotification>(phone, HubMethods.Client.SessionUpdated);

        await PinAsync(phone, "machine-a", "session-1", pinned: true);
        Assert.True((await Next(updates)).Session.Pinned);

        await PinAsync(phone, "machine-a", "session-1", pinned: false);
        Assert.False((await Next(updates)).Session.Pinned);
    }

    // Projects: end-to-end CRUD, ownership isolation, General invariants, moving a
    // live session, and the delete-time sweep with its reconnect backstop.

    [Fact]
    public async Task ANewUserAlwaysHasAGeneralProject()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ProjectListNotification list = await client.InvokeAsync<ProjectListNotification>(
            HubMethods.Server.ListProjects);

        ProjectInfo general = Assert.Single(list.Projects);
        Assert.True(general.IsGeneral);
        Assert.Equal("General", general.Name);
    }

    [Fact]
    public async Task CreatingAProjectReturnsItAndReachesEveryDevice()
    {
        HubConnection phone = await ConnectClientAsync(AliceTenant, AliceObject);
        HubConnection laptop = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ProjectCreatedNotification> created =
            Listen<ProjectCreatedNotification>(laptop, HubMethods.Client.ProjectCreated);

        ProjectResult result = await phone.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject,
            new CreateProjectRequest { Name = "Website", Description = "The public site" });

        Assert.Null(result.Error);
        Assert.Equal("Website", result.Project!.Name);
        Assert.False(result.Project.IsGeneral);

        ProjectCreatedNotification notification = await Next(created);
        Assert.Equal(result.Project.ProjectId, notification.Project.ProjectId);
    }

    [Fact]
    public async Task CreatingAProjectWithANameAlreadyTakenIsRefused()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        ProjectResult duplicate = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "WEBSITE" });

        Assert.Null(duplicate.Project);
        Assert.Equal(ErrorCodes.DuplicateProjectName, duplicate.Error);
    }

    [Fact]
    public async Task UpdatingAProjectReachesEveryDevice()
    {
        HubConnection phone = await ConnectClientAsync(AliceTenant, AliceObject);
        HubConnection laptop = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ProjectUpdatedNotification> updated =
            Listen<ProjectUpdatedNotification>(laptop, HubMethods.Client.ProjectUpdated);

        ProjectResult created = await phone.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        ProjectResult result = await phone.InvokeAsync<ProjectResult>(
            HubMethods.Server.UpdateProject,
            new UpdateProjectRequest
            {
                ProjectId = created.Project!.ProjectId,
                Name = "Website v2",
                SiteUrl = "https://example.com",
            });

        Assert.Null(result.Error);
        Assert.Equal("Website v2", result.Project!.Name);

        ProjectUpdatedNotification notification = await Next(updated);
        Assert.Equal("Website v2", notification.Project.Name);
        Assert.Equal("https://example.com", notification.Project.SiteUrl);
    }

    [Fact]
    public async Task GeneralCannotBeRenamedOrDeleted()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectListNotification list = await client.InvokeAsync<ProjectListNotification>(
            HubMethods.Server.ListProjects);
        string generalId = Assert.Single(list.Projects).ProjectId;

        ProjectResult renamed = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.UpdateProject,
            new UpdateProjectRequest { ProjectId = generalId, Name = "Everything" });

        Assert.Equal(ErrorCodes.InvalidRequest, renamed.Error);
        Assert.Null(renamed.Project);

        ErrorNotification? refused = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DeleteProject, new DeleteProjectRequest { ProjectId = generalId });

        Assert.Equal(ErrorCodes.CannotDeleteGeneralProject, refused!.Code);
    }

    [Fact]
    public async Task BobCannotSeeEditOrDeleteAlicesProjects()
    {
        HubConnection alice = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectResult created = await alice.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Alice's project" });

        HubConnection bob = await ConnectClientAsync(BobTenant, BobObject);

        ProjectListNotification bobsList = await bob.InvokeAsync<ProjectListNotification>(
            HubMethods.Server.ListProjects);
        Assert.Single(bobsList.Projects); // only Bob's own General, never Alice's

        ProjectResult editAttempt = await bob.InvokeAsync<ProjectResult>(
            HubMethods.Server.UpdateProject,
            new UpdateProjectRequest { ProjectId = created.Project!.ProjectId, Name = "mine now" });

        Assert.Null(editAttempt.Project);
        Assert.Equal(ErrorCodes.ProjectNotFound, editAttempt.Error);

        ErrorNotification? deleteAttempt = await bob.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DeleteProject, new DeleteProjectRequest { ProjectId = created.Project.ProjectId });

        Assert.Equal(ErrorCodes.ProjectNotFound, deleteAttempt!.Code);
    }

    [Fact]
    public async Task ANewSessionDefaultsToGeneral()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);

        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).ProjectId);
    }

    [Fact]
    public async Task MovingASessionReachesEveryDevice()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection phone = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectResult project = await phone.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        HubConnection laptop = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionUpdatedNotification> updates =
            Listen<ClientSessionUpdatedNotification>(laptop, HubMethods.Client.SessionUpdated);

        Assert.Null(await phone.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                ProjectId = project.Project!.ProjectId,
            }));

        ClientSessionUpdatedNotification update = await Next(updates);
        Assert.Equal(project.Project.ProjectId, update.Session.ProjectId);
    }

    [Fact]
    public async Task MovingASessionToAProjectThatDoesNotExistIsRefused()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ErrorNotification? refused = await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                ProjectId = "does-not-exist",
            });

        Assert.Equal(ErrorCodes.ProjectNotFound, refused!.Code);
    }

    [Fact]
    public async Task MovingASessionBackToGeneralWithNullClearsTheProject()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectResult project = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                ProjectId = project.Project!.ProjectId,
            });

        await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest { MachineId = "machine-a", SessionId = "session-1", ProjectId = null });

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);
        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).ProjectId);
    }

    /// <summary>
    /// A project assignment survives the agent reconnecting, exactly like a rename -
    /// the label lives at the hub, not on the agent.
    /// </summary>
    [Fact]
    public async Task AProjectAssignmentSurvivesTheAgentReconnecting()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectResult project = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                ProjectId = project.Project!.ProjectId,
            });

        await agent.DisposeAsync();

        HubConnection again = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(again, "session-1", "pwsh");

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);
        Assert.Equal(
            project.Project.ProjectId,
            Assert.Single(Assert.Single(list.Machines).Sessions).ProjectId);
    }

    /// <summary>
    /// Deleting a project reassigns its live sessions back to General and tells
    /// every device both what happened to the sessions and that the project is gone.
    /// </summary>
    [Fact]
    public async Task DeletingAProjectReassignsItsLiveSessionsAndAnnouncesBoth()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectResult project = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                ProjectId = project.Project!.ProjectId,
            });

        HubConnection phone = await ConnectClientAsync(AliceTenant, AliceObject);
        Channel<ClientSessionUpdatedNotification> sessionUpdates =
            Listen<ClientSessionUpdatedNotification>(phone, HubMethods.Client.SessionUpdated);
        Channel<ProjectDeletedNotification> projectDeletes =
            Listen<ProjectDeletedNotification>(phone, HubMethods.Client.ProjectDeleted);

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DeleteProject, new DeleteProjectRequest { ProjectId = project.Project.ProjectId }));

        Assert.Null((await Next(sessionUpdates)).Session.ProjectId);
        Assert.Equal(project.Project.ProjectId, (await Next(projectDeletes)).ProjectId);

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);
        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).ProjectId);
    }

    /// <summary>
    /// The backstop: a machine offline at delete time still carries the old project
    /// id in its label and re-announces it verbatim on reconnect. The hub corrects it
    /// on arrival rather than trusting the sweep to have already reached it.
    /// </summary>
    [Fact]
    public async Task ASessionThatReannouncesADeletedProjectSelfCorrectsToGeneral()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(agent, "session-1", "pwsh");

        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);
        ProjectResult project = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "Website" });

        await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionProject,
            new SetSessionProjectRequest
            {
                MachineId = "machine-a",
                SessionId = "session-1",
                ProjectId = project.Project!.ProjectId,
            });

        // The agent drops off the network before the delete's sweep can reach it.
        await agent.DisposeAsync();

        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.DeleteProject, new DeleteProjectRequest { ProjectId = project.Project.ProjectId }));

        // It reconnects and re-announces the same session, still labelled with the
        // now-deleted project id, exactly as it was the moment it went offline.
        HubConnection again = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");
        await OpenSessionAsync(again, "session-1", "pwsh");

        MachineListNotification list = await client.InvokeAsync<MachineListNotification>(
            HubMethods.Server.ListMachines);
        Assert.Null(Assert.Single(Assert.Single(list.Machines).Sessions).ProjectId);
    }

    [Fact]
    public async Task AnUnknownProjectIdInARequestIsRefusedRatherThanCrashing()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ProjectResult refused = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.UpdateProject,
            new UpdateProjectRequest { ProjectId = "does-not-exist", Name = "New name" });

        Assert.Null(refused.Project);
        Assert.Equal(ErrorCodes.ProjectNotFound, refused.Error);
    }

    [Fact]
    public async Task ANameThatIsBlankIsRefused()
    {
        HubConnection client = await ConnectClientAsync(AliceTenant, AliceObject);

        ProjectResult refused = await client.InvokeAsync<ProjectResult>(
            HubMethods.Server.CreateProject, new CreateProjectRequest { Name = "   " });

        Assert.Null(refused.Project);
        Assert.Equal(ErrorCodes.InvalidRequest, refused.Error);
    }

    [Fact]
    public async Task WillNotUpdateASessionThatWasNeverOpened()
    {
        HubConnection agent = await ConnectAgentAsync(AliceTenant, AliceObject, "machine-a");

        ErrorNotification? refused = await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionUpdated,
            new AgentSessionUpdatedNotification { Session = NewSession("ghost", "pwsh") });

        Assert.Equal(ErrorCodes.SessionNotFound, refused!.Code);
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

    private static async Task OpenSessionAsync(
        HubConnection agent,
        string sessionId,
        string program,
        SessionKind kind = SessionKind.Terminal) =>
        Assert.Null(await agent.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SessionOpened,
            new AgentSessionOpenedNotification
            {
                Session = NewSession(sessionId, program, kind),
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

    private static async Task RenameAsync(
        HubConnection client,
        string machineId,
        string sessionId,
        string? name) =>
        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionName,
            new SetSessionNameRequest { MachineId = machineId, SessionId = sessionId, Name = name }));

    private static async Task PinAsync(
        HubConnection client,
        string machineId,
        string sessionId,
        bool pinned) =>
        Assert.Null(await client.InvokeAsync<ErrorNotification?>(
            HubMethods.Server.SetSessionPinned,
            new SetSessionPinnedRequest { MachineId = machineId, SessionId = sessionId, Pinned = pinned }));

    private static SessionInfo NewSession(
        string sessionId,
        string program,
        SessionKind kind = SessionKind.Terminal) => new()
    {
        SessionId = sessionId,
        Program = program,
        Args = [],
        Cwd = @"C:\Projects\1RemoteCLI",
        Cols = 120,
        Rows = 30,
        StartedAt = DateTimeOffset.UtcNow,
        Kind = kind,
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
}
