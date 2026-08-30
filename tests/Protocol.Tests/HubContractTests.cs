using MessagePack;
using OneRemoteCli.Protocol.Hub;

namespace OneRemoteCli.Protocol.Tests;

public class HubContractTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    [Fact]
    public void TerminalOutputTreatsDataAsOpaqueBytes()
    {
        // The hub must never need to look inside `data`. Keeping it as raw bytes is
        // what lets end-to-end encryption be added later without a shape change.
        var sent = new TerminalOutputNotification
        {
            SessionId = "s-a94f29901",
            Seq = 4821,
            Kind = TerminalOutputKind.Delta,
            Data = [0x1b, (byte)'[', (byte)'2', (byte)'J', 0x00, 0xff],
        };

        var received = RoundTrip(sent);

        Assert.Equal(sent.SessionId, received.SessionId);
        Assert.Equal(sent.Seq, received.Seq);
        Assert.Equal(TerminalOutputKind.Delta, received.Kind);
        Assert.Equal(sent.Data, received.Data);
    }

    [Fact]
    public void SnapshotKindSurvivesTheRoundTrip()
    {
        var received = RoundTrip(new TerminalOutputNotification
        {
            SessionId = "s-1",
            Seq = 1,
            Kind = TerminalOutputKind.Snapshot,
            Data = [],
        });

        Assert.Equal(TerminalOutputKind.Snapshot, received.Kind);
    }

    [Fact]
    public void RegisterMachineCarriesTheProtocolVersion()
    {
        var received = RoundTrip(new RegisterMachineRequest
        {
            MachineId = "6f9a1c22-4d18-4b0e-9d3a-2a7e5b0c81f4",
            DisplayName = "Primary Dev Workstation",
            Os = "Microsoft Windows 11 Pro 10.0.26100",
            AgentVersion = "1.0.0",
            ProtocolVersion = ProtocolVersion.Current,
            NotificationLevel = NotificationLevel.ActionRequired,
        });

        Assert.Equal(ProtocolVersion.Current, received.ProtocolVersion);
        Assert.Equal(NotificationLevel.ActionRequired, received.NotificationLevel);
        Assert.True(ProtocolVersion.IsSupported(received.ProtocolVersion));
    }

    [Fact]
    public void MachineNotificationLevelSurvivesTheRoundTrip()
    {
        SetMachineNotificationLevelRequest received = RoundTrip(
            new SetMachineNotificationLevelRequest
            {
                NotificationLevel = NotificationLevel.Off,
            });

        Assert.Equal(NotificationLevel.Off, received.NotificationLevel);
    }

    [Fact]
    public void VersionFourRegistrationDefaultsToAllAttentionEvents()
    {
        byte[] bytes = MessagePackSerializer.Serialize(
            new VersionFourRegisterMachineRequest
            {
                MachineId = "machine",
                DisplayName = "Machine",
                Os = "Windows",
                AgentVersion = "0.36",
                ProtocolVersion = 4,
            },
            Options);

        RegisterMachineRequest received =
            MessagePackSerializer.Deserialize<RegisterMachineRequest>(bytes, Options);

        Assert.Equal(NotificationLevel.AllAttentionEvents, received.NotificationLevel);
    }

    [Fact]
    public void OlderPushRegistrationKeepsEveryNotificationKindEnabled()
    {
        byte[] bytes = MessagePackSerializer.Serialize(
            new OriginalRegisterPushRequest
            {
                Endpoint = "https://push.example/device",
                Keys = new PushKeys { P256dh = "p256dh", Auth = "auth" },
            },
            Options);

        RegisterPushRequest received =
            MessagePackSerializer.Deserialize<RegisterPushRequest>(bytes, Options);

        Assert.False(received.DisableAwaitingInput);
        Assert.False(received.DisableSessionFinished);
        Assert.False(received.DisableAnnouncements);
    }

    [Fact]
    public void OlderHubCanReadANewerPushRegistration()
    {
        byte[] bytes = MessagePackSerializer.Serialize(
            new RegisterPushRequest
            {
                Endpoint = "https://push.example/device",
                Keys = new PushKeys { P256dh = "p256dh", Auth = "auth" },
                DisableAwaitingInput = true,
                DisableAnnouncements = true,
            },
            Options);

        OriginalRegisterPushRequest received =
            MessagePackSerializer.Deserialize<OriginalRegisterPushRequest>(bytes, Options);

        Assert.Equal("https://push.example/device", received.Endpoint);
        Assert.Equal("p256dh", received.Keys.P256dh);
    }

    [Fact]
    public void AttachCarriesAnOptionalLastSeqForResume()
    {
        Assert.Null(RoundTrip(new AttachSessionRequest { MachineId = "m", SessionId = "s", Cols = 80, Rows = 24 }).LastSeq);
        Assert.Equal(4821, RoundTrip(new AttachSessionRequest { MachineId = "m", SessionId = "s", Cols = 80, Rows = 24, LastSeq = 4821 }).LastSeq);
    }

    [Fact]
    public void InputIsPassedThroughByteForByte()
    {
        // Ctrl+C, cursor-up, and a plain "y\r" must all arrive unmodified so phone
        // input is indistinguishable from keyboard input.
        byte[][] sequences =
        [
            [0x03],
            [0x1b, (byte)'[', (byte)'A'],
            [(byte)'y', (byte)'\r'],
        ];

        foreach (byte[] sequence in sequences)
        {
            Assert.Equal(sequence, RoundTrip(new SendInputRequest { SessionId = "s", Data = sequence }).Data);
        }
    }

    [Fact]
    public void TerminalUploadMessagesPreserveOrderingAndBytes()
    {
        const string uploadId = "29fb210b-e7b4-4d41-8913-74a57a4eb753";

        BeginTerminalUploadRequest begin = RoundTrip(new BeginTerminalUploadRequest
        {
            SessionId = "s-1",
            UploadId = uploadId,
            FileName = "photo.jpg",
            TotalBytes = 4,
        });
        Assert.Equal("photo.jpg", begin.FileName);
        Assert.Equal(4, begin.TotalBytes);

        TerminalUploadChunkRequest chunk = RoundTrip(new TerminalUploadChunkRequest
        {
            SessionId = "s-1",
            UploadId = uploadId,
            Offset = 2,
            Data = [0x00, 0xff],
        });
        Assert.Equal(2, chunk.Offset);
        Assert.Equal([0x00, 0xff], chunk.Data);

        TerminalUploadReply reply = RoundTrip(new TerminalUploadReply
        {
            UploadId = uploadId,
            ConfirmedBytes = 4,
            TotalBytes = 4,
            RemotePath = @"C:\Temp\photo.jpg",
        });
        Assert.Equal(4, reply.ConfirmedBytes);
        Assert.Equal(@"C:\Temp\photo.jpg", reply.RemotePath);
        Assert.Null(reply.ErrorCode);
    }

    [Fact]
    public void ChatAttachmentMessagesPreserveOrderingAndBytes()
    {
        const string attachmentId = "6cbb0f4d-2a41-4cf6-8b4a-0a26f2b71f61";

        BeginChatAttachmentRequest begin = RoundTrip(new BeginChatAttachmentRequest
        {
            SessionId = "chat-1",
            AttachmentId = attachmentId,
            FileName = "receipt.png",
            MimeType = "image/png",
            TotalBytes = 4,
        });
        Assert.Equal("receipt.png", begin.FileName);
        Assert.Equal("image/png", begin.MimeType);
        Assert.Equal(4, begin.TotalBytes);

        ChatAttachmentChunkRequest chunk = RoundTrip(new ChatAttachmentChunkRequest
        {
            SessionId = "chat-1",
            AttachmentId = attachmentId,
            Offset = 2,
            Data = [0x00, 0xff],
        });
        Assert.Equal(2, chunk.Offset);
        Assert.Equal([0x00, 0xff], chunk.Data);

        ChatAttachmentReply reply = RoundTrip(new ChatAttachmentReply
        {
            AttachmentId = attachmentId,
            ConfirmedBytes = 4,
            TotalBytes = 4,
            Completed = true,
        });
        Assert.True(reply.Completed);
        Assert.Null(reply.ErrorCode);

        SendChatPromptRequest prompt = RoundTrip(new SendChatPromptRequest
        {
            SessionId = "chat-1",
            Text = "What does this say?",
            AttachmentIds = [attachmentId],
        });
        Assert.Equal("What does this say?", prompt.Text);
        Assert.Equal([attachmentId], prompt.AttachmentIds);

        ChatPromptReply accepted = RoundTrip(new ChatPromptReply { Accepted = true });
        Assert.True(accepted.Accepted);
        Assert.Null(accepted.ErrorCode);
    }

    /// <summary>
    /// A chat attachment reply must never grow a path field. The whole point of the
    /// separate family is that browser-selected chat bytes become prompt content, so
    /// there is nothing on the machine for the phone to be told about.
    /// </summary>
    [Fact]
    public void ChatAttachmentRepliesCarryNoMachinePath()
    {
        Assert.DoesNotContain(
            typeof(ChatAttachmentReply).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChatCapabilitiesAreAppendedAndDefaultToNothing()
    {
        SessionInfo negotiated = RoundTrip(new SessionInfo
        {
            SessionId = "chat-1",
            Kind = SessionKind.AgentChat,
            ChatCapabilities = new ChatCapabilities { Image = true, EmbeddedContext = false },
        });

        Assert.NotNull(negotiated.ChatCapabilities);
        Assert.True(negotiated.ChatCapabilities!.Image);
        Assert.False(negotiated.ChatCapabilities.EmbeddedContext);

        // What a version 5 agent sends: the payload ends before the appended field,
        // which has to read as "no attachment support" rather than as unknown.
        byte[] bytes = MessagePackSerializer.Serialize(
            new VersionFiveSessionInfo
            {
                SessionId = "chat-1",
                Program = "GitHub Copilot",
                Kind = SessionKind.AgentChat,
            },
            Options);

        SessionInfo older = MessagePackSerializer.Deserialize<SessionInfo>(bytes, Options);

        Assert.Null(older.ChatCapabilities);
        Assert.Equal(SessionKind.AgentChat, older.Kind);
    }

    [Fact]
    public void ChatAttachmentLimitsStayBelowTheTerminalUploadCeiling()
    {
        // Base64 inflates by four thirds before the bytes reach the ACP agent, and
        // then again against a context window, so these two are deliberately not the
        // same number as the terminal limit.
        Assert.True(ChatAttachmentLimits.MaxAttachmentBytes < TerminalUploadLimits.MaxFileBytes);
        Assert.True(ChatAttachmentLimits.MaxPromptBytes < TerminalUploadLimits.MaxFileBytes);
        Assert.True(
            ChatAttachmentLimits.MaxPromptBytes <
            ChatAttachmentLimits.MaxAttachmentBytes * ChatAttachmentLimits.MaxAttachmentCount);
        Assert.Equal(TerminalUploadLimits.MaxChunkBytes, ChatAttachmentLimits.MaxChunkBytes);
    }

    [Fact]
    public void MachineInfoNestsItsSessions()
    {
        var received = RoundTrip(new MachineInfo
        {
            MachineId = "m-1",
            DisplayName = "Primary",
            Os = "Windows",
            AgentVersion = "1.0.0",
            Online = true,
            Sessions =
            [
                new SessionInfo
                {
                    SessionId = "s-1",
                    Program = "claude",
                    Args = ["--resume"],
                    Cwd = @"C:\Projects",
                    Cols = 120,
                    Rows = 30,
                    StartedAt = new DateTimeOffset(2026, 8, 13, 15, 22, 4, TimeSpan.Zero),
                    AwaitingInput = true,
                },
            ],
        });

        Assert.True(received.Online);
        SessionInfo session = Assert.Single(received.Sessions);
        Assert.Equal("claude", session.Program);
        Assert.True(session.AwaitingInput);
        Assert.Equal(new DateTimeOffset(2026, 8, 13, 15, 22, 4, TimeSpan.Zero), session.StartedAt);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(ProtocolVersion.Current, true)]
    [InlineData(ProtocolVersion.Current + 1, false)]
    public void ProtocolVersionSupportIsBounded(int version, bool supported) =>
        Assert.Equal(supported, ProtocolVersion.IsSupported(version));

    private static T RoundTrip<T>(T value) =>
        MessagePackSerializer.Deserialize<T>(MessagePackSerializer.Serialize(value, Options), Options);

    [MessagePackObject]
    public sealed class VersionFourRegisterMachineRequest
    {
        [Key(0)]
        public string MachineId { get; set; } = string.Empty;

        [Key(1)]
        public string DisplayName { get; set; } = string.Empty;

        [Key(2)]
        public string Os { get; set; } = string.Empty;

        [Key(3)]
        public string AgentVersion { get; set; } = string.Empty;

        [Key(4)]
        public int ProtocolVersion { get; set; }
    }

    [MessagePackObject]
    public sealed class OriginalRegisterPushRequest
    {
        [Key(0)]
        public string Endpoint { get; set; } = string.Empty;

        [Key(1)]
        public PushKeys Keys { get; set; } = new();
    }

    /// <summary>A session as a version 5 agent describes one: nothing past ProjectId.</summary>
    [MessagePackObject]
    public sealed class VersionFiveSessionInfo
    {
        [Key(0)]
        public string SessionId { get; set; } = string.Empty;

        [Key(1)]
        public string Program { get; set; } = string.Empty;

        [Key(2)]
        public string[] Args { get; set; } = [];

        [Key(3)]
        public string Cwd { get; set; } = string.Empty;

        [Key(4)]
        public int Cols { get; set; }

        [Key(5)]
        public int Rows { get; set; }

        [Key(6)]
        public DateTimeOffset StartedAt { get; set; }

        [Key(7)]
        public string? DisplayName { get; set; }

        [Key(8)]
        public bool AwaitingInput { get; set; }

        [Key(9)]
        public CliType CliType { get; set; }

        [Key(10)]
        public string? CustomName { get; set; }

        [Key(11)]
        public bool Pinned { get; set; }

        [Key(12)]
        public SessionKind Kind { get; set; }

        [Key(13)]
        public string? ProjectId { get; set; }
    }
}
