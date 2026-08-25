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
}
